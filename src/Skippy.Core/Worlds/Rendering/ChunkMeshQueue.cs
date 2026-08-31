using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace Skippy.Core.Worlds.Rendering;

/// <summary>
/// Builds chunk geometry on worker threads and hands the finished data back to the
/// thread that owns the graphics device.
///
/// <para>
/// This is the other half of the streaming stutter. Generating a chunk's tiles moved off
/// the update thread first (see <c>ChunkBuilder</c>), which left the bigger cost still
/// sitting on it: turning those tiles into vertices ran 28 ms for an ordinary chunk and
/// over 200 ms for a forested one. Only about an eighth of that was the upload to the
/// GPU; the rest was arithmetic that never needed a graphics device at all. So the
/// arithmetic happens here, on workers, and what comes back is a <see cref="MeshData"/>
/// for the main thread to upload — which is the one part that has to stay.
/// </para>
/// <para>
/// A request captures everything the mesh depends on: a <see cref="ChunkView"/> of the
/// chunk and its neighbours, the neighbour mask, and the tile size and map view the mesh
/// is being cut for. Any of those can move while the worker is busy — the player resizes
/// the tiles, cycles the map view, regenerates the world — and a mesh built for a world
/// that has since changed is simply dropped on arrival rather than guarded against. It
/// costs one wasted build and keeps every worker lock-free.
/// </para>
/// </summary>
public sealed class ChunkMeshQueue
{
    /// <summary>One chunk's finished geometry on its way back to the main thread.</summary>
    public sealed class Built
    {
        public ChunkCoord Coord;
        public byte NeighborMask;

        /// <summary>What the mesh was cut for, so a stale one can be recognised on arrival.</summary>
        public int IsoVersion;
        public MapView MapView;

        public MeshData? Ground, Shadows, Veg;
        public ExceptionDispatchInfo? Error;

        /// <summary>Which run of the queue this belongs to; see <see cref="AbandonInFlight"/>.</summary>
        public int Epoch;

        /// <summary>Hands every layer's pooled arrays back without uploading them.</summary>
        public void Discard()
        {
            Ground?.Discard();
            Shadows?.Discard();
            Veg?.Discard();
            Ground = Shadows = Veg = null;
        }
    }

    private readonly TextureAtlas _atlas;

    /// <summary>
    /// Meshers waiting to be used again. One is rented per job and returned after it, so
    /// the number ever made settles at however many jobs actually overlap — and each
    /// carries a good deal of scratch, so making one per job would be wasteful.
    /// </summary>
    private readonly ConcurrentBag<ChunkMesher> _meshers = new();

    /// <summary>Chunks out with a worker. Main thread only — every caller here is on it.</summary>
    private readonly HashSet<ChunkCoord> _inFlight = new();

    private readonly ConcurrentQueue<Built> _finished = new();

    /// <summary>
    /// Bumped every time the outstanding work is abandoned, so a result started before
    /// that can be told from one started after and dropped without ever being looked at.
    /// </summary>
    private int _epoch;

    /// <summary>
    /// How many chunks may be meshed at once. One per spare core: unlike terrain
    /// generation there is no point queueing them deeper, because the results have to
    /// come back through a single-threaded upload anyway, and holding the cap low is what
    /// lets a camera that turns around be followed rather than obeyed late.
    /// </summary>
    public int Capacity { get; } = Math.Max(1, Environment.ProcessorCount - 1);

    public ChunkMeshQueue(TextureAtlas atlas) => _atlas = atlas;

    public int InFlight => _inFlight.Count;
    public bool AtCapacity => _inFlight.Count >= Capacity;
    public bool IsBuilding(ChunkCoord coord) => _inFlight.Contains(coord);

    /// <summary>
    /// Sets a worker building this chunk's geometry. The view must have been taken on
    /// this thread, just now; see <see cref="ChunkView"/>.
    /// </summary>
    public void Request(in ChunkView view, Chunk chunk, byte neighborMask, int isoVersion)
    {
        if (!_inFlight.Add(chunk.Coord))
            return;

        // Copied into a local before the closure: `view` is an `in` parameter and cannot
        // be captured, and the point of it is to be a snapshot anyway.
        ChunkView snapshot = view;
        var built = new Built
        {
            Coord = chunk.Coord,
            NeighborMask = neighborMask,
            IsoVersion = isoVersion,
            MapView = view.MapView,
            Epoch = _epoch,
        };

        Task.Run(() =>
        {
            ChunkMesher mesher = _meshers.TryTake(out ChunkMesher? m) ? m : new ChunkMesher(_atlas);
            try
            {
                mesher.BuildMesh(snapshot, chunk, neighborMask,
                                 out MeshData ground, out MeshData shadows, out MeshData veg);
                built.Ground = ground;
                built.Shadows = shadows;
                built.Veg = veg;
            }
            catch (Exception ex)
            {
                built.Error = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                _meshers.Add(mesher);

                // Queued whatever happened: a chunk that never comes back is a coordinate
                // stuck in flight, and the renderer would never ask for its mesh again.
                _finished.Enqueue(built);
            }
        });
    }

    /// <summary>
    /// Takes one finished chunk, if there is one. The caller must upload or discard every
    /// layer it carries.
    /// </summary>
    /// <exception cref="Exception">Whatever the worker threw, rethrown here with its
    /// original stack, because there was nowhere useful to throw it out there.</exception>
    public bool TryTakeFinished(out Built built)
    {
        while (_finished.TryDequeue(out Built? b))
        {
            _inFlight.Remove(b.Coord);

            // Started before the last abandonment: it was cut from a world that has since
            // been thrown away, so it goes straight back to the pool unlooked at.
            if (b.Epoch != _epoch)
            {
                b.Discard();
                continue;
            }

            b.Error?.Throw();
            built = b;
            return true;
        }

        built = null!;
        return false;
    }

    /// <summary>
    /// Forgets every chunk in flight, so their results are dropped as they arrive. For
    /// when the meshes are all being thrown away anyway — a resize, a map-view change, a
    /// regenerated world. The workers finish harmlessly; nothing is waited on.
    /// </summary>
    public void AbandonInFlight()
    {
        _epoch++;
        _inFlight.Clear();
        while (_finished.TryDequeue(out Built? b))
            b.Discard();
    }
}
