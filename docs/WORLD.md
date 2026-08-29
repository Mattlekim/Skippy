# World engine

An isometric, chunked, procedurally generated world — Age of Empires II in
silhouette: stepped terrain height, climate-driven biomes, and vegetation that
follows the climate. Everything lives in `src/Skippy.Core/Worlds/`.

![Skippy world](world-preview.png)

*Coast → temperate broadleaf forest → taiga on the cooler high ground, the
terrain sloping smoothly between elevations. The pine belt appears where
elevation has cooled the land — height driving temperature driving biome.*

## The `World` base class

`World` is the base class the game builds on. **A world owns everything about the
map** and drives its own loop:

- `Update(gameTime, input)` — moves the camera (keyboard / drag / wheel) and
  streams chunks in and out around it.
- `Draw(spriteBatch, graphicsDevice)` — isometric render of the visible tiles.

Override the virtual seams to make a specific game world without touching the
streaming or rendering machinery:

| Seam | Purpose |
| --- | --- |
| `CreateGenerator(seed)` | Swap in a custom `IWorldGenerator`. |
| `OnChunkGenerated(chunk)` | React when a chunk first appears (spawn entities, etc.). |
| `Update` / `Draw` | Extend or replace the per-frame behavior. |

```csharp
public sealed class CampaignWorld : World
{
    public CampaignWorld(int seed) : base(seed) { }
    protected override IWorldGenerator CreateGenerator(int seed) => new MyGenerator(seed);
    protected override void OnChunkGenerated(Chunk chunk) { /* place resources */ }
}
```

## Infinite world via chunks

The map is broken into fixed **32×32-tile chunks** (`Chunk.Size`). 32 is a
deliberate middle ground: big enough that per-chunk overhead is amortized over
1024 tiles, small enough that one chunk generates in well under a frame and culls
cleanly. Chunks are:

- stored in a `Dictionary<ChunkCoord, Chunk>`,
- generated on demand within `LoadRadius` chunks of the camera,
- discarded once outside it.

So the world is **effectively infinite** — walk in any direction and terrain
keeps generating, deterministically (same seed + coordinate ⇒ same tile), because
generation is driven by coordinate-based noise rather than any fixed-size buffer.

A `Tile` is a value type (`Height`, `Level`, `Temperature`, `Moisture`, `Biome`,
`Vegetation`), so a chunk is one contiguous array with no per-tile allocation.

## Climate model

`DefaultWorldGenerator` evaluates three independent seeded Perlin fBm fields over
infinite space, then couples them the way a real climate does:

1. **Height** — fractal noise, reshaped for relief (broad oceans/lowlands, peaks
   that rise to the snow line), then quantized into discrete elevation steps.
2. **Temperature** — a broad base field, **then cooled by altitude** (a lapse
   rate): `temperature = base − max(0, height − seaLevel) × lapse`. Mountains are
   cold even in warm latitudes.
3. **Moisture** — its own field; land near/under the water table reads wetter.

### Biomes = temperature × moisture

`BiomeClassifier` maps the resulting climate to a biome with a Whittaker-style
diagram. Height overrides handle the extremes (water below sea level, rock/snow
above the snow line); everything else is a temperature × moisture grid:

```
            dry ───────────── moisture ───────────── wet
 cold │ Tundra          Taiga            Snow
 temp │ Grassland       TemperateForest  Swamp
 warm │ Savanna         Grassland        Jungle
 hot  │ Desert          Savanna          Jungle
```

Tune the thresholds in `BiomeClassifier`, and the sea/beach/snow lines in
`WorldConstants`.

### Vegetation follows climate

Each tile carries a low-frequency **vegetation-density** field (its clumping
value). At render time this drives an **Age-of-Empires-II-style scatter**: a tile
places *many* individual plants (up to ~12) at randomized, deterministic
positions within its quad — dense broadleaf/pine forests, scattered savanna
trees, desert cactus, tundra shrubs — rather than one sprite per tile. Species
come from the biome; water and beaches stay clear. (`VegetationPicker` still
computes a single representative type per tile, handy for gameplay queries.)

## Isometric rendering

`WorldRenderer` draws the visible tiles in painter's order along screen diagonals.

Terrain is **smooth, Age-of-Empires-II-style**: elevation lives at tile *corners*,
so each tile is a four-corner quad that ramps between its neighbors instead of
stepping with vertical cliffs. Shared corners use the same level, so the surface
stays seamless. Tiles are drawn as colored triangles (two per tile) through a
`BasicEffect`.

**Biome edges blend, with no hard tile borders.** Each corner is colored by
averaging the biomes of the (up to four) tiles meeting there, and those colors
are assigned per-vertex so the GPU interpolates a smooth gradient across the
quad — grass fades into sand, and the shoreline softens from deep water through
shallows to wet sand. Per-tile elevation and slope-lighting shading is folded in
before the blend. Vegetation is then stamped on top with a `SpriteBatch`, using
runtime-generated art (a disc, a triangle, a pixel) — no content-pipeline assets.

`Iso` holds the 2:1 projection (`TileToIso` / `IsoToTile`); `Camera` tracks a
focus point in world-tile space plus zoom, so it stays put as chunks stream.

## Controls

| Input | Action |
| --- | --- |
| Arrow keys / WASD | Pan the camera |
| Mouse drag / touch drag | Pan the camera |
| Mouse wheel | Zoom in / out |
| F11 | Toggle fullscreen (desktop) |

## Where to go next

- **Rivers / lakes** — add a hydrology pass in the generator (flow from height).
- **Resources & entities** — spawn in `OnChunkGenerated`; store per-chunk.
- **Save / load** — chunks are plain data keyed by `ChunkCoord`; serialize the
  dictionary or regenerate from seed.
- **Real art** — replace the runtime shapes in `WorldRenderer` with sprites from
  the content pipeline (see `Content/README.md`); the projection is unchanged.
