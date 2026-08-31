namespace Skippy.Core.Worlds.Generation;

/// <summary>
/// Fills a chunk's tiles with terrain, climate, biome and vegetation data.
/// Swap in a custom implementation (via <c>World.CreateGenerator</c>) to change
/// how worlds are shaped without touching streaming or rendering.
///
/// <para>
/// <b><see cref="Generate"/> is called from worker threads, several at once</b> — that is
/// how the world streams without stopping the frame (see <see cref="ChunkBuilder"/>). So
/// it must be safe to call from several threads at the same time, which for a generator
/// built out of position-indexed noise it already is: everything it reads is fixed at
/// construction and nothing it writes is shared. Anything a generator does have to work
/// out once and remember — <see cref="LakeFinder"/> is the one case here — has to carry
/// its own answer to this, and any state that changes with the world's settings belongs
/// in <see cref="Prepare"/>, which is called on the update thread with no worker running.
/// </para>
/// </summary>
public interface IWorldGenerator
{
    /// <summary>
    /// Builds one chunk's worth of tiles. Called from a worker thread; see the note on
    /// the interface about what that requires.
    /// </summary>
    void Generate(Chunk chunk);

    /// <summary>
    /// Brings the generator up to date with anything that may have changed since the last
    /// chunk was built — the world's own dials, above all. Called on the update thread,
    /// before any worker is handed a chunk, so it is the one place a generator may write
    /// state that <see cref="Generate"/> then reads from every thread at once.
    ///
    /// A generator that only ever reads a position has nothing to bring up to date, so
    /// this does nothing by default.
    /// </summary>
    void Prepare() { }
}
