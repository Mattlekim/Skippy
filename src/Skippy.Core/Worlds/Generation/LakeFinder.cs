using System;
using System.Collections.Generic;

namespace Skippy.Core.Worlds.Generation;

/// <summary>
/// Tells a lake from the sea. A lake is not a kind of water — it is a *size* of water:
/// a body you can walk around, where the ocean is one you cannot. So the only way to
/// know which a tile is standing in is to measure the body it belongs to, and that is
/// what this does — it floods the water outward from the tile and sees whether the
/// whole thing closes inside <see cref="MaxLakeArea"/> tiles.
///
/// Everything else in generation is a pure function of one position, and this cannot be:
/// a tile in the middle of a lake looks exactly like a tile in the middle of an ocean.
/// It is still deterministic, though, and independent of the order chunks are built in —
/// the flood reads the same height field the tiles themselves come from, so a body of
/// water gets the same answer whichever of its tiles is asked first, and whether or not
/// the rest of it is loaded.
///
/// The cost is paid once per body, not once per tile. Every tile the flood touches is
/// filed with the body's verdict, and a flood that reaches an already-filed tile stops
/// there and takes its answer: after the first tile of an ocean has been paid for, the
/// rest of that ocean costs a dictionary lookup and a step or two.
///
/// Not thread-safe: it holds its working buffers between calls, as generation is
/// single-threaded and this runs inside it.
/// </summary>
public sealed class LakeFinder
{
    /// <summary>
    /// The largest a body of water can be and still be a lake, in tiles. Above it the
    /// water is sea — an inland sea is still a sea.
    /// </summary>
    public const int MaxLakeArea = 500;

    /// <summary>
    /// How many verdicts are kept before the lot is dropped. The map is endless and the
    /// camera does come back, so this cannot grow forever; it is set high enough to hold
    /// the coastline around a good many chunks, and losing it costs work, never
    /// correctness — the floods simply run again and reach the same answer.
    /// </summary>
    private const int MaxCachedTiles = 1 << 17;

    private readonly Func<int, int, bool> _isWater;

    /// <summary>Tile → is it a lake. Every tile of one body shares the body's verdict.</summary>
    private readonly Dictionary<long, bool> _verdicts = new();

    // Working state of the flood in progress: the tiles to expand from, the water it has
    // reached, and the land it has bumped into (kept so a shoreline tile is only tested
    // once however many of the body's tiles look at it).
    private readonly Queue<long> _frontier = new();
    private readonly HashSet<long> _wet = new();
    private readonly HashSet<long> _dry = new();

    /// <param name="isWater">Whether the tile at a world position is under water.</param>
    public LakeFinder(Func<int, int, bool> isWater) => _isWater = isWater;

    /// <summary>
    /// Whether the water at this tile is a lake rather than sea. The tile must be water —
    /// dry land has no body to measure, and the answer would be meaningless.
    /// </summary>
    public bool IsLake(int wx, int wy)
        => _verdicts.TryGetValue(Key(wx, wy), out bool known) ? known : Flood(wx, wy);

    /// <summary>Forgets every verdict — for when the ground itself is about to change.</summary>
    public void Clear() => _verdicts.Clear();

    /// <summary>
    /// Floods the body this tile belongs to, and files the verdict for all of it.
    ///
    /// It ends one of three ways: the water runs out inside the cap (a lake), the cap is
    /// passed (sea), or it meets a tile already filed — in which case the body it is part
    /// of has been measured before and its answer is this tile's answer too.
    /// </summary>
    private bool Flood(int sx, int sy)
    {
        _frontier.Clear();
        _wet.Clear();
        _dry.Clear();

        long start = Key(sx, sy);
        _wet.Add(start);
        _frontier.Enqueue(start);

        bool lake = true;
        int area = 0;

        while (_frontier.Count > 0)
        {
            long k = _frontier.Dequeue();
            if (++area > MaxLakeArea)
            {
                lake = false; // too big to walk around: this is sea
                break;
            }

            int x = (int)(k >> 32), y = (int)k;
            if (!Expand(x + 1, y, ref lake) || !Expand(x - 1, y, ref lake)
             || !Expand(x, y + 1, ref lake) || !Expand(x, y - 1, ref lake))
            {
                break; // ran into water that has already been judged
            }
        }

        // The whole flood is one body of water, so every tile of it takes the one verdict —
        // including whatever is still sitting in the frontier when the flood stops early.
        if (_verdicts.Count + _wet.Count > MaxCachedTiles)
            _verdicts.Clear();

        foreach (long k in _wet)
            _verdicts[k] = lake;

        return lake;
    }

    /// <summary>
    /// Takes one step of the flood. Returns false when the neighbour has already been
    /// judged — the flood is then over, with <paramref name="lake"/> set to that verdict.
    /// </summary>
    private bool Expand(int x, int y, ref bool lake)
    {
        long k = Key(x, y);
        if (_wet.Contains(k) || _dry.Contains(k))
            return true;

        // Asked before the water test on purpose: only water is ever filed, so a tile with
        // a verdict is water by definition — and the water test is the expensive half of
        // this, being a full evaluation of the terrain. It is what makes the second tile of
        // an ocean cost a lookup instead of another five hundred height samples.
        if (_verdicts.TryGetValue(k, out bool known))
        {
            lake = known;
            return false;
        }

        if (!_isWater(x, y))
        {
            _dry.Add(k);
            return true;
        }

        _wet.Add(k);
        _frontier.Enqueue(k);
        return true;
    }

    private static long Key(int x, int y) => ((long)x << 32) | (uint)y;
}
