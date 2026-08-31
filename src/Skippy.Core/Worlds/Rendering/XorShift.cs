namespace Skippy.Core.Worlds.Rendering;

/// <summary>
/// A tiny, fast pseudo-random generator, seeded per thing rather than per world.
///
/// Everything scattered on the map — a plant's height and lean, a rock's facets, the
/// break in a line of surf, a sheep's exact shade — rolls its own from a seed derived
/// from where it stands. That is what makes the variation deterministic and free of any
/// shared state: the same tile grows the same wood whichever chunk was built first, and
/// whichever thread built it.
///
/// A value type on purpose, passed by <c>ref</c>, so a run of rolls costs no allocation.
/// </summary>
internal struct XorShift
{
    private uint _s;

    public XorShift(uint seed) { _s = seed == 0 ? 1u : seed; }

    public uint NextBits()
    {
        _s ^= _s << 13;
        _s ^= _s >> 17;
        _s ^= _s << 5;
        return _s;
    }

    public float Next() => (NextBits() & 0xFFFFFF) / 16777216f;

    /// <summary>A count in [min, max].</summary>
    public int Range(int min, int max) => min + (int)(Next() * (max - min + 1));
}
