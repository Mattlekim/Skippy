namespace Skippy.Core.Worlds.Generation;

/// <summary>
/// Fills a chunk's tiles with terrain, climate, biome and vegetation data.
/// Swap in a custom implementation (via <c>World.CreateGenerator</c>) to change
/// how worlds are shaped without touching streaming or rendering.
/// </summary>
public interface IWorldGenerator
{
    void Generate(Chunk chunk);

    /// <summary>
    /// Drops anything the generator worked out about the world it has been building, for
    /// when that world is thrown away and the same generator is asked for another (the
    /// settings panel does exactly this). A generator that only ever reads a position
    /// holds nothing to drop, so this does nothing by default.
    /// </summary>
    void Reset() { }
}
