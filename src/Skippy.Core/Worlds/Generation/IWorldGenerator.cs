namespace Skippy.Core.Worlds.Generation;

/// <summary>
/// Fills a chunk's tiles with terrain, climate, biome and vegetation data.
/// Swap in a custom implementation (via <c>World.CreateGenerator</c>) to change
/// how worlds are shaped without touching streaming or rendering.
/// </summary>
public interface IWorldGenerator
{
    void Generate(Chunk chunk);
}
