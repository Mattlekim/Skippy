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
    public static PlantKind Pick(in Tile tile, float clump, float jitter)
    {
        if (tile.IsWater || tile.Biome == Biome.Beach)
            return PlantKind.None;

        float d = clump;
        float r = jitter;
        PlantKind tree = Forests.Primary(tile.Forest);

        return tile.Biome switch
        {
            // Flowers turn up in the temperate understory too, but sparingly — the odd
            // patch where the canopy opens, not the meadow's carpet.
            Biome.TemperateForest => d > 0.55f ? tree : r > 0.75f ? PlantKind.Bush : r > 0.62f ? PlantKind.Flowers : PlantKind.GrassTuft,
            Biome.Taiga           => d > 0.52f ? tree : r > 0.82f ? PlantKind.Bush : PlantKind.None,
            Biome.Jungle          => d > 0.42f ? tree : r > 0.55f ? PlantKind.Bush : PlantKind.GrassTuft,
            // Grassland is where flowers belong: a good band of the open ground comes up
            // flowering, the rest plain grass, with the odd tree where the clumping peaks.
            Biome.Grassland       => d > 0.82f ? tree : r > 0.70f ? PlantKind.Flowers : r > 0.45f ? PlantKind.GrassTuft : PlantKind.None,
            Biome.Savanna         => d > 0.86f ? tree : r > 0.60f ? PlantKind.GrassTuft : PlantKind.None,
            // Desert grows nothing: bare, shifting sand.
            Biome.Tundra          => r > 0.90f ? PlantKind.Bush   : PlantKind.None,
            Biome.Swamp           => d > 0.60f ? PlantKind.Bush   : r > 0.60f ? PlantKind.GrassTuft : PlantKind.None,
            Biome.Rock            => r > 0.85f ? PlantKind.Rock   : PlantKind.None,
            _                     => PlantKind.None,
        };
    }
}
