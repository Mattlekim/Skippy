using System.Collections.Generic;

namespace Skippy.Core.Worlds.Items;

/// <summary>
/// The world's grazeable greenery, as far as the animals eating it are concerned. A tile
/// grows one representative plant (<see cref="Tile.Vegetation"/>); where that plant is
/// edible — grass, flowers, a bush — this hands out a <see cref="Vegetation"/> item to eat
/// from, and remembers how much of it is left once an animal has been at it.
///
/// It is kept apart from the tiles themselves on purpose. Tiles are value types packed into
/// chunks that stream in and out and are rebuilt wholesale by the generator; grazed-down
/// food is a running state that has to outlive a redraw and belongs to no one chunk. So the
/// forage of a place is stored here, on the world, keyed by tile — created only where an
/// animal actually stops to eat (there is no point tracking the food on ground nothing
/// grazes), and grown back over time so a cropped meadow recovers once the flock moves on.
/// </summary>
public sealed class WorldForage
{
    /// <summary>Food a patch grows back per world day. A stripped tuft of grass fills back in over a few days.</summary>
    private const float RegrowPerDay = 2f;

    /// <summary>The patches that have been grazed at least once, by packed tile coordinate.</summary>
    private readonly Dictionary<long, Vegetation> _patches = new();

    /// <summary>Scratch list of full patches to forget, so growing back frees the memory a graze took.</summary>
    private readonly List<long> _recovered = new();

    private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

    /// <summary>
    /// The forage at a tile, or <c>null</c> where nothing edible grows. Reads the tile's own
    /// plant, so a grass or flower tile comes back grazeable and a tree, rock or bare tile
    /// does not. The patch is created the first time it is asked for and kept thereafter.
    /// </summary>
    public Vegetation? At(int x, int y, PlantKind kind)
    {
        long key = Key(x, y);
        if (_patches.TryGetValue(key, out Vegetation? patch))
            return patch;

        if (WorldItems.Create(kind) is not Vegetation fresh)
            return null; // material, or bare ground — nothing to eat

        _patches[key] = fresh;
        return fresh;
    }

    /// <summary>
    /// Takes up to <paramref name="amount"/> of food from the tile's forage and returns how
    /// much was actually there — 0 where nothing edible grows or the patch is already grazed
    /// bare. The animal feeds by exactly what it gets back, so the patch can never give up
    /// more food than it holds however many mouths are at it.
    /// </summary>
    public float Graze(int x, int y, PlantKind kind, float amount)
        => At(x, y, kind)?.Consume(amount) ?? 0f;

    /// <summary>How full a tile's forage is right now, 0 (bare) to 1 (untouched or ungrazed).</summary>
    public float Fullness(int x, int y)
        => _patches.TryGetValue(Key(x, y), out Vegetation? patch) ? patch.Fullness : 1f;

    /// <summary>
    /// Grows every grazed patch back a little, over <paramref name="days"/> world days, and
    /// forgets the ones that have filled right back up — an untouched patch costs nothing to
    /// remember, so only the ones still recovering are kept.
    /// </summary>
    public void Advance(float days)
    {
        if (days <= 0f || _patches.Count == 0)
            return;

        _recovered.Clear();
        foreach (KeyValuePair<long, Vegetation> entry in _patches)
        {
            entry.Value.Regrow(RegrowPerDay, days);
            if (!entry.Value.IsDepleted && entry.Value.Fullness >= 1f)
                _recovered.Add(entry.Key);
        }

        foreach (long key in _recovered)
            _patches.Remove(key);
    }

    /// <summary>Forgets all grazed state — used when the world is regenerated under the same object.</summary>
    public void Clear() => _patches.Clear();
}
