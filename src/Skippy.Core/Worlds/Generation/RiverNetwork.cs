using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Skippy.Core.Worlds.Generation;

/// <summary>
/// Rivers for an endless map.
///
/// A river is not a property of a tile — it is a path — and the map is far too big
/// to run a drainage simulation over. So rivers are traced instead: springs sit on a
/// coarse deterministic grid, and each is walked downhill through the height field
/// until it reaches the sea. That is what makes a river actually arrive at the ocean
/// rather than merely look like it might; a river is a route there by construction.
///
/// A chunk asks for every river whose path crosses it and stamps those in. Traces are
/// cached by spring, so a river that runs through forty chunks is still walked once.
/// </summary>
public sealed class RiverNetwork
{
    /// <summary>
    /// One spring per this many tiles, jittered within its cell. Springs at 220 tiles
    /// apart were so sparse you could play for a long time without meeting a river.
    /// </summary>
    private const int SpringSpacing = 90;

    /// <summary>
    /// A river gives up after this many steps; it has normally found the sea long
    /// before. This also bounds the search: a chunk has to consider every spring
    /// within this many tiles of it, so the two constants trade off against each other.
    /// </summary>
    private const int MaxSteps = 400;

    /// <summary>A spring only forms on ground at least this far above sea level.</summary>
    private const float SpringMinRise = 0.14f;

    private readonly int _seed;
    private readonly Func<int, int, float> _heightAt;
    private readonly Dictionary<long, RiverPath> _paths = new();

    public RiverNetwork(int seed, Func<int, int, float> heightAt)
    {
        _seed = seed;
        _heightAt = heightAt;
    }

    /// <summary>A traced river, with the bounds of its course for quick rejection.</summary>
    public readonly struct RiverPath
    {
        public readonly Vector2[] Points;
        public readonly Vector2 Min, Max;

        public RiverPath(Vector2[] points, Vector2 min, Vector2 max)
        {
            Points = points;
            Min = min;
            Max = max;
        }

        /// <summary>True for the default value too: a cell with no spring caches one of those.</summary>
        public bool IsEmpty => Points is null || Points.Length < 2;
    }

    /// <summary>Adds every river whose course crosses the given tile rectangle.</summary>
    public void Collect(int minX, int minY, int maxX, int maxY, List<RiverPath> into)
    {
        int cx0 = FloorDiv(minX - MaxSteps, SpringSpacing);
        int cx1 = FloorDiv(maxX + MaxSteps, SpringSpacing);
        int cy0 = FloorDiv(minY - MaxSteps, SpringSpacing);
        int cy1 = FloorDiv(maxY + MaxSteps, SpringSpacing);

        for (int cy = cy0; cy <= cy1; cy++)
        {
            for (int cx = cx0; cx <= cx1; cx++)
            {
                RiverPath path = PathFor(cx, cy);
                if (path.IsEmpty)
                    continue;

                // Most springs in range never come anywhere near this chunk.
                if (path.Max.X < minX || path.Min.X > maxX || path.Max.Y < minY || path.Min.Y > maxY)
                    continue;

                into.Add(path);
            }
        }
    }

    private RiverPath PathFor(int cellX, int cellY)
    {
        long key = ((long)cellX << 32) ^ (uint)cellY;
        if (_paths.TryGetValue(key, out RiverPath cached))
            return cached;

        RiverPath path = Trace(cellX, cellY);
        _paths[key] = path;
        return path;
    }

    /// <summary>
    /// Walks one spring downhill to the sea by steepest descent over its eight
    /// neighbours.
    /// </summary>
    private RiverPath Trace(int cellX, int cellY)
    {
        uint h = Hash(cellX, cellY, _seed);
        int x = cellX * SpringSpacing + (int)((h & 0xFFFF) / 65535f * SpringSpacing);
        int y = cellY * SpringSpacing + (int)(((h >> 16) & 0xFFFF) / 65535f * SpringSpacing);

        // Springs belong on high ground. Low ground near the sea drains too briefly
        // to make a river worth drawing.
        if (_heightAt(x, y) < WorldConstants.SeaLevel + SpringMinRise)
            return default;

        var points = new List<Vector2>(128);
        var visited = new HashSet<long>();
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);

        for (int step = 0; step < MaxSteps; step++)
        {
            points.Add(new Vector2(x, y));
            visited.Add(((long)x << 32) ^ (uint)y);
            if (x < min.X) min.X = x;
            if (y < min.Y) min.Y = y;
            if (x > max.X) max.X = x;
            if (y > max.Y) max.Y = y;

            if (_heightAt(x, y) < WorldConstants.SeaLevel)
                break; // the sea: this river is done

            // Step to the lowest neighbour it has not already been through.
            //
            // Refusing to revisit is what makes this work. Plain steepest descent
            // deadlocks in the first pit it meets — and a fractal height field is
            // full of them — walking out and falling straight back in, which carves
            // a puddle instead of a river. Taking the lowest *unvisited* neighbour
            // instead means a pit is climbed out of by its lowest rim, which is
            // where a real lake would spill, and the descent picks up on the far side.
            int bx = x, by = y;
            float lowest = float.MaxValue;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;

                    int nx = x + dx, ny = y + dy;
                    if (visited.Contains(((long)nx << 32) ^ (uint)ny))
                        continue;

                    float n = _heightAt(nx, ny);
                    if (n < lowest)
                    {
                        lowest = n;
                        bx = nx;
                        by = ny;
                    }
                }
            }

            if (bx == x && by == y)
                break; // hemmed in on all sides by its own course

            x = bx;
            y = by;
        }

        return points.Count < 2 ? default : new RiverPath(points.ToArray(), min, max);
    }

    private static uint Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint n = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
            n = (n ^ (n >> 13)) * 1274126177u;
            return n ^ (n >> 16);
        }
    }

    private static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);
}
