namespace Skippy.Core.Worlds.Generation;

/// <summary>
/// Chooses per-tile vegetation from the tile's biome and climate. A low-frequency
/// density field creates clumps — dense forests, scattered savanna trees — while a
/// per-tile jitter adds fine variation so clumps aren't uniform.
///
/// Both come in already sampled rather than being taken here, because both have to
/// close around the world: the clumping field is one of the generator's wrapped
/// fields, and the jitter is hashed from the tile's canonical position, so a tile and
/// the same tile a lap east grow the same thing (see <see cref="WorldBounds"/>).
///
/// Which tree a wooded tile grows is not asked here: it is whatever owns the wood
/// the tile stands in (see <see cref="Forests"/>), so a tile in an evergreen stand
/// comes back a pine and the same tile in a broadleaf one comes back an oak.
/// </summary>
public static class VegetationPicker
{
    /// <param name="clump">The clumping field at this tile, 0..1.</param>
    /// <param name="jitter">Per-tile noise-free variation, 0..1.</param>
    public static Vegetation Pick(in Tile tile, float clump, float jitter)
    {
        if (tile.IsWater || tile.Biome == Biome.Beach)
            return Vegetation.None;

        float d = clump;
        float r = jitter;
        Vegetation tree = Forests.Primary(tile.Forest);

        return tile.Biome switch
        {
            Biome.TemperateForest => d > 0.55f ? tree : r > 0.75f ? Vegetation.Bush : Vegetation.GrassTuft,
            Biome.Taiga           => d > 0.52f ? tree : r > 0.82f ? Vegetation.Bush : Vegetation.None,
            Biome.Jungle          => d > 0.42f ? tree : r > 0.55f ? Vegetation.Bush : Vegetation.GrassTuft,
            Biome.Grassland       => d > 0.82f ? tree : r > 0.50f ? Vegetation.GrassTuft : Vegetation.None,
            Biome.Savanna         => d > 0.86f ? tree : r > 0.60f ? Vegetation.GrassTuft : Vegetation.None,
            Biome.Desert          => d > 0.93f ? Vegetation.Cactus : Vegetation.None,
            Biome.Tundra          => r > 0.90f ? Vegetation.Bush   : Vegetation.None,
            Biome.Swamp           => d > 0.60f ? Vegetation.Bush   : r > 0.60f ? Vegetation.GrassTuft : Vegetation.None,
            Biome.Rock            => r > 0.85f ? Vegetation.Rock   : Vegetation.None,
            _                     => Vegetation.None,
        };
    }
}
