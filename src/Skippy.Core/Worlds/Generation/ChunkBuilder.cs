using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace Skippy.Core.Worlds.Generation;

/// <summary>
/// Builds chunks on worker threads and hands the finished ones back to the update thread.
///
/// <para>
/// A chunk is not cheap: every one of its 1024 tiles is a dozen-odd octaves of noise, and
/// the coastal cleanup alone evaluates the height field nine times over near a shoreline.
/// Built where they used to be — inside <c>World.StreamChunks</c>, one after another — a
/// step across a chunk border cost seven of them in a single frame, and the frame it cost
/// them in was the one the player was looking at. That is the stutter this exists to end.
/// </para>
/// <para>
/// The shape of it is deliberately small. The update thread <see cref="Request"/>s the
/// chunks it wants and <see cref="TryTakeFinished"/>s whatever is ready; in between, the
/// work happens somewhere else and the world simply does not have those chunks yet. There
/// is nothing to draw where a chunk has not arrived, and the screen is cleared to black,
/// so a chunk still being built shows as black ground until it lands — which is the whole
/// of the placeholder, and why there is no placeholder geometry anywhere.
/// </para>
/// <para>
/// Nothing here is locked, because nothing here is shared. <see cref="_inFlight"/> is only
/// ever touched from the update thread (both <see cref="Request"/> and
/// <see cref="TryTakeFinished"/> are its calls), a chunk belongs to exactly one worker
/// from the moment it is made until the moment it is queued, and the queue between them is
/// a <see cref="ConcurrentQueue{T}"/>. The generator is shared, and answering for that is
/// the generator's job — see <see cref="IWorldGenerator"/>.
/// </para>
/// </summary>
public sealed class ChunkBuilder
{
    /// <summary>
    /// A finished chunk on its way back to the update thread, and whatever went wrong
    /// building it. A worker that throws cannot usefully report it from where it is, and
    /// swallowing it would leave the coordinate stuck in flight forever — a hole in the
    /// map that never fills — so the failure rides home with the chunk and is rethrown on
    /// the update thread, where it will be noticed.
    /// </summary>
    private readonly struct Built
    {
        public readonly Chunk Chunk;
        public readonly ExceptionDispatchInfo? Error;

        public Built(Chunk chunk, ExceptionDispatchInfo? error)
        {
            Chunk = chunk;
            Error = error;
        }
    }

    private readonly IWorldGenerator _generator;

    /// <summary>Coordinates handed to a worker and not yet collected. Update thread only.</summary>
    private readonly HashSet<ChunkCoord> _inFlight = new();

    private readonly ConcurrentQueue<Built> _finished = new();

    /// <summary>
    /// How many chunks may be out with workers at once.
    ///
    /// One per spare core — the update thread keeps its own — and then doubled, so a core
    /// that finishes a chunk mid-frame has the next one already waiting instead of idling
    /// until the following frame dispatches it. Capping it at all is what keeps the queue
    /// answerable to the camera: only this many are committed to, so a camera that turns
    /// around re-picks what to build next from where it is now, rather than grinding
    /// through a backlog of chunks nobody is walking towards any more.
    ///
    /// A single-core machine (a WebAssembly build, in practice) gets 2, and the work
    /// lands between frames on the one thread there is instead of inside one.
    /// </summary>
    public int Capacity { get; }

    public ChunkBuilder(IWorldGenerator generator)
    {
        _generator = generator;
        Capacity = Math.Max(1, Environment.ProcessorCount - 1) * 2;
    }

    /// <summary>How many chunks are being built right now.</summary>
    public int InFlight => _inFlight.Count;

    /// <summary>Whether no more may be started until something comes back.</summary>
    public bool AtCapacity => _inFlight.Count >= Capacity;

    /// <summary>Whether this chunk is already out with a worker (so it must not be asked for twice).</summary>
    public bool IsBuilding(ChunkCoord coord) => _inFlight.Contains(coord);

    /// <summary>
    /// Sets a worker building this chunk. Does nothing if it is already in flight; the
    /// caller is expected to have checked <see cref="AtCapacity"/> first.
    /// </summary>
    public void Request(ChunkCoord coord)
    {
        if (!_inFlight.Add(coord))
            return;

        // Made here rather than on the worker so the chunk is fully constructed before it
        // is published to another thread, and so it carries its own coordinate home.
        var chunk = new Chunk(coord);
        Task.Run(() =>
        {
            ExceptionDispatchInfo? error = null;
            try
            {
                _generator.Generate(chunk);
            }
            catch (Exception ex)
            {
                error = ExceptionDispatchInfo.Capture(ex);
            }

            // Queued whatever happened: a chunk that never comes back is a coordinate
            // stuck in flight, which the streamer would never ask for again.
            _finished.Enqueue(new Built(chunk, error));
        });
    }

    /// <summary>
    /// Takes one finished chunk, if there is one. Call it until it returns false to
    /// collect everything the workers got through since the last frame.
    /// </summary>
    /// <exception cref="Exception">Whatever the worker threw, rethrown here with its
    /// original stack, because there was nowhere useful to throw it out there.</exception>
    public bool TryTakeFinished(out Chunk chunk)
    {
        if (!_finished.TryDequeue(out Built built))
        {
            chunk = null!;
            return false;
        }

        _inFlight.Remove(built.Chunk.Coord);
        built.Error?.Throw();

        chunk = built.Chunk;
        return true;
    }
}
