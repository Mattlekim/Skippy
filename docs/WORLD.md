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
3. **Moisture** — its own field; ground near the water table reads damper, fading
   out with height rather than stepping at the shoreline (a flat below-sea-level
   bonus used to put a hard wet/dry edge along every coast).

**Climate changes gradually, by construction.** Hot must never border cold, nor wet
border dry, so the climate fields are the slowest-moving thing on the map: low
frequency (0.0035 / 0.0030) and only two octaves, since every extra octave adds
wiggle that reads as one tile being warmer than the one beside it. Measured over a
million tiles, a neighbouring pair differs by 0.0035 temperature and 0.0055
moisture on average — a full sweep from freezing to hot takes about **290 tiles**,
dry to soaked about **180**.

This frequency is set against `Spread` below, which multiplies the field's spatial
gradient about fourfold. That is the unavoidable price of an even distribution: the
mapping has to be steep to stretch a narrow bell across 0..1, so the raw field must
be correspondingly slow for transitions to stay wide.

Both climate fields are then **spread** through a logistic CDF. This matters more
than it sounds: fBm sums octaves, so its raw output is near-Gaussian and bunched
around 0.5 — measured over land it spanned barely 0.40–0.69. The hot and dry
corners of the diagram were simply unreachable, so grassland generated at *0% of
tiles* and 63% of land was forest. `Spread` (sigma matched to the field's measured
deviation) stretches each field evenly across 0..1, which is what makes deserts,
savanna and open grassland exist at all.

The lapse rate is deliberately gentle (0.25). At 0.9 the altitude cooling was an
order of magnitude larger than the temperature field's own variation, so height
decided climate single-handedly and a third of all land clamped to freezing.
Snow-capped peaks don't need it — the snow line caps high ground regardless.

### Rivers

A river is not a property of a tile, it is a *path*, and the map is far too large to
run a drainage simulation over. So `RiverNetwork` traces them: springs sit on a
coarse deterministic grid (one per 90 tiles, only on ground well above the sea), and
each is walked downhill through the height field until it reaches water. **A river
arrives at the ocean by construction** — it is a route there, not a shape that
resembles one. Measured over a 3000-tile square: 414 rivers, **92.5% reaching the
sea**, mean length 98 tiles.

The walk takes the lowest *unvisited* neighbour, not simply the lowest. That detail
is the whole feature: a fractal height field is full of small pits, and plain
steepest descent deadlocks in the first one — walking out and falling straight back
in, carving a puddle instead of a river. Refusing to revisit means a pit is climbed
out of by its lowest rim, which is where a real lake would spill, and the descent
resumes on the far side.

Traces are cached per spring, so a river running through forty chunks is walked once;
a chunk stamps in every river whose course crosses it, after the climate pass.
`WorldConstants.RiverWidth` is the channel width in tiles and is live-tunable
(`,` / `.`), which throws the map away and regenerates it, since channels are cut at
generation time.

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

The grid is held as **data** (`TemperatureBands` / `MoistureBands` /
`ClimateBiomes`) rather than nested ifs, so `ClimateEdgeDistance` can measure how
far a tile sits from the nearest boundary. Open country is the default and forest
is the exception — trees are held to the wet end of each temperature band — which
lands roughly 33% of land in grass and 26% in trees.

Tune the thresholds in `BiomeClassifier`, and the sea/beach/snow lines in
`WorldConstants`.

**Grass is coloured from climate, not from a swatch.** `BiomeRegistry.SurfaceColor`
gives grassland and savanna a colour along a single green-to-yellow ramp: water
drives it green, drought and heat bleach it back to yellow. It never leaves that
two-colour line, so big open country varies without going muddy.

**Tundra wears snow in patches.** The climate fields are far too slow to make
anything look patchy up close, so `Tile.Patch` is a separate, much higher-frequency
field (a few tiles across) purely for surface detail. Snow lies where it peaks, and
the colder the ground the lower the bar — the far north goes mostly white, the
fringe stays mostly bare.

### Vegetation follows climate

Each tile carries a low-frequency **vegetation-density** field (its clumping
value). At render time this drives an **Age-of-Empires-II-style scatter**: a tile
places *many* individual plants (up to ~12) at randomized, deterministic
positions within its quad — dense broadleaf/pine forests, scattered savanna
trees, desert cactus, tundra shrubs — rather than one sprite per tile. Species
come from the biome; water and beaches stay clear. (`VegetationPicker` still
computes a single representative type per tile, handy for gameplay queries.)

Scatter counts are **rates per base-sized tile and may be fractional** — "a tree on
one tile in twenty" is a rate, not a number, and rounding it would lose it. The
remainder is settled with a coin toss, so a rate of 0.05 holds at any tile size.
That is what puts the odd lone tree out on open grassland, where the count used to
be a hard switch that left half the grassland bare and made a forest of the rest.

**Trees and bushes are painted art**, from the sheets in `Assets/`. A `VegItem`
carries a seed, and the draw picks which of the sheet's variants that plant gets, so
a wood is a mix rather than one tree stamped over and over. Grass, cactus and rocks
are still drawn in code — the sheets carry no art for them — and jungles borrow the
broadleaves, there being no palm on the sheets.

**Some plants come up dead**, and the sheets have their own bare-wood art for that.
Each `Add` call takes the share that should be deadwood: a few percent in healthy
woods, more in swamp and savanna where trees drown or bake, and **always** on
tundra, where nothing holds a leaf.

### Getting the art out of the sheets

`TextureAtlas` builds one texture at runtime out of the procedural shapes *and* the
sheets, so the world still draws with a single texture bound. The sheets are
embedded in the assembly rather than read off disk, so every head loads them the
same way and the browser build needs no file server.

Two parts of this are worth knowing, because the obvious approach fails at both:

- **Keying.** The sheets are painted on a flat dark card, not on transparency.
  Thresholding on distance from the card's colour cannot work: measured off the art,
  the card sits at luminance ~18 with almost no colour in it, while *shaded foliage
  is darker than the card* and is only distinguishable by being coloured at all. Any
  threshold loose enough to keep that foliage also keeps the soft glow the sheets
  carry around each subject, leaving every plant on a visible rectangle of card; any
  threshold tight enough to remove the glow eats the foliage. So the card is
  **flooded** from the sheet's edges instead — only backdrop that connects to the
  outside is removed, and shade enclosed by a plant survives however dark it is.
- **Cutting.** The sprites are found by looking for the empty gaps between them, not
  by assuming a grid; the rows are not evenly spaced and a fixed grid slices the
  tall trees in half.

Sprites are resampled to a few times their drawn size and the atlas carries mip
levels, sampled `LinearClamp` with a transparent gutter around each. Point-sampling
art that draws several times smaller than it is stored makes foliage crawl.

**Trees thin out towards a biome's edge.** Canopy counts are scaled by
`ClimateEdgeDistance`, so a wood frays into the open ground beside it instead of
stopping on a hard line. Only trees fade — undergrowth stays put, and the fade
floors just above zero so two neighbouring woods don't leave a bald seam.

## Isometric rendering

`WorldRenderer` draws the visible tiles in painter's order along screen diagonals.

Terrain is **smooth, Age-of-Empires-II-style**: elevation lives at tile *corners*,
so each tile is a four-corner quad that ramps between its neighbors instead of
stepping with vertical cliffs. Shared corners use the same level, so the surface
stays seamless. Tiles are drawn as colored triangles (two per tile) through a
`BasicEffect`.

**The shore is a shore, not a blend.** Terrain colour is interpolated across each
tile quad, so a land vertex beside a water vertex smears green into blue over *the
whole width of a tile* — barely noticeable at 64 pixels, a hundred pixels of haze at
312. Picking a winning side per vertex does not fix that; it only moves which
colours smear. So the waterline is cut instead: each vertex records the side it came
down on (majority of the tiles meeting there, ties to water so narrow rivers keep
their width), and a tile on the far side of one re-derives that corner **from its
own side only**. Water tiles are therefore all water, land tiles all land, and the
boundary falls exactly on the tile edge. Everywhere else the shared vertex is used
and the terrain stays seamless.

The join is covered by **surf**: breakers just inside the water's edge with a
fainter swell behind, each end pushed out by a different amount so the foam doesn't
trace the tile grid's staircase back in white. They are sized and spaced in **iso
pixels, not as a fraction of the edge** — laid out proportionally, a 312-pixel tile
got three enormous dashes lying out in the water. Rivers get the same treatment,
being water meeting land like any other.

**Biome edges blend, with no hard tile borders.** Each corner is colored by
averaging the biomes of the (up to four) tiles meeting there, and those colors
are assigned per-vertex so the GPU interpolates a smooth gradient across the
quad — grass fades into sand, and the shoreline softens from deep water through
shallows to wet sand. Per-tile elevation and slope-lighting shading is folded in
before the blend. Vegetation is then stamped on top with a `SpriteBatch`, using
runtime-generated art (a disc, a triangle, a pixel) — no content-pipeline assets.

`Iso` holds the 2:1 projection (`TileToIso` / `IsoToTile`); `Camera` tracks a
focus point in world-tile space plus zoom, so it stays put as chunks stream.

**Tile size is one knob, live-adjustable.** `Iso.TileWidth` is the only authored
value; the diamond height (tiles are 2:1) and the elevation `HeightStep` follow
from it. Hold `[` / `]` in game to step it by 8 px between 16 and 512 — the
current value is in the top-right read-out, so a size can be picked by eye and
then written back as the `Iso` default.

**A tile is an amount of ground, not a zoom level.** What stands on a tile keeps
its own absolute size however big the tile is, so a wider tile is simply more room
and fits more on it. Scatter counts are authored per `Iso.BaseTileWidth` tile and
multiplied by `Iso.TileAreaScale`, which keeps plants-per-acre constant as tiles
resize; `MaxPlantsPerTile` caps that so a very large tile can't put six figures of
sprites into one chunk mesh. To make everything bigger on screen, zoom the camera
instead.

Chunk meshes are baked in iso pixels, so every resize invalidates them:
`Iso.Version` bumps, and `WorldRenderer` throws away all cached meshes and
rebuilds the visible ones. That is why the key-repeat is deliberately unhurried.
Note that `LoadRadius` counts chunks, not screen distance, so at very small tile
sizes the edge of the loaded window can come into view.

**Hover read-out.** `World.TryGetTileAtScreen` turns a screen point back into a
tile, and the HUD shows that tile's height, temperature (in °C) and moisture. The
inverse projection ignores elevation, so over steep ground the pick lands slightly
downhill of what is drawn under the cursor.

**Map views** (`World.MapView`, M) swap the terrain's colouring from biomes to one
of the generator's raw input fields — height, temperature (after the altitude
lapse) or moisture — so you can see *why* a biome landed where it did. The data
views are drawn unshaded and without vegetation: elevation and slope lighting
would make a value impossible to read off the screen, and plants would hide the
ground. Terrain meshes bake their vertex colours, so switching view rebuilds them
exactly as a tile resize does.

**Tile grid** (`World.ShowTileGrid`, G) draws a line along every tile edge, using
the same corner elevations as the terrain so it drapes over hills. It is a
per-chunk mesh cached exactly like the terrain, built only while the grid is on.

## Controls

| Input | Action |
| --- | --- |
| Arrow keys / WASD | Pan the camera |
| Mouse drag / touch drag | Pan the camera |
| Mouse wheel | Zoom in / out (0.4× to 16×) |
| Mouse over a tile | Read its height, temperature and moisture in the HUD |
| G | Toggle the tile-grid overlay |
| `[` / `]` | Shrink / grow the tiles (hold to repeat) |
| M | Cycle the map view: biome → height → temp → moisture |
| `,` / `.` | Narrow / widen rivers (rebuilds the map) |
| F11 | Toggle fullscreen (desktop) |

## Where to go next

- **Rivers / lakes** — add a hydrology pass in the generator (flow from height).
- **Resources & entities** — spawn in `OnChunkGenerated`; store per-chunk.
- **Save / load** — chunks are plain data keyed by `ChunkCoord`; serialize the
  dictionary or regenerate from seed.
- **Real art** — replace the runtime shapes in `WorldRenderer` with sprites from
  the content pipeline (see `Content/README.md`); the projection is unchanged.
