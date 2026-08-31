using System;
using Microsoft.Xna.Framework;

namespace Skippy.Core.Worlds.Rendering;

/// <summary>
/// The sun (and the sky it lights). A single time-of-day phase drives everything:
/// the sun climbs from the eastern horizon at dawn to its noon height and back down
/// to the western horizon at dusk, and its colour swings from the warm orange of a
/// low sun to near-white at noon while the sky ambient cools and brightens with it.
///
/// It exposes exactly what a directional light needs — a travel direction and a
/// diffuse colour — plus the ambient and background-sky colours, all recomputed on
/// <see cref="Advance"/>. The renderer feeds these straight into the built-in
/// directional light every frame, so the sun moves without a single vertex being
/// re-uploaded.
/// </summary>
public sealed class SkyLight
{
    private const float TwoPi = MathF.PI * 2f;

    /// <summary>0 = midnight, 0.25 = sunrise, 0.5 = noon, 0.75 = sunset. Wraps at 1.</summary>
    public float TimeOfDay = 0.32f;

    /// <summary>
    /// Real seconds for one full day–night cycle. Four minutes: long enough that the light
    /// on the ground is something you notice changing rather than watch race, short enough
    /// that a session sees several dawns.
    /// </summary>
    public float DayLength = 240f;

    /// <summary>When true the sun holds still (manual scrubbing only).</summary>
    public bool Paused;

    /// <summary>Highest the sun climbs at noon, in radians (~72°).</summary>
    private const float MaxAltitude = 1.26f;

    /// <summary>
    /// Compass bearing the sun sits at when it is highest, in radians. Placed in the
    /// screen's upper-left ("north-west") so noon light rakes down the map the way the
    /// old fixed slope-shading did, keeping relief legible.
    /// </summary>
    private const float NoonAzimuth = 3.93f; // ~225°

    /// <summary>Direction the sunlight travels (from the sun toward the ground). Unit length.</summary>
    public Vector3 LightDirection { get; private set; }

    /// <summary>Diffuse colour of direct sun, already faded out at night.</summary>
    public Vector3 SunColor { get; private set; }

    /// <summary>Sky ambient that fills the shadows — cool and dim at night, brighter by day.</summary>
    public Vector3 Ambient { get; private set; }

    /// <summary>The colour to clear the screen to behind the world.</summary>
    public Color SkyColor { get; private set; }

    /// <summary>0 in the dead of night, 1 in full daylight. Drives the ambient fill and clouds.</summary>
    public float Daylight { get; private set; }

    /// <summary>
    /// How much of the sun is up: 0 below the horizon, 1 once it is a few degrees clear of
    /// it. Unlike <see cref="Daylight"/> this comes up almost as soon as the sun does, which
    /// is what gives the golden hour its own bright, low, raking light — and it is what
    /// shadows are cast by, so there are none at all at night.
    /// </summary>
    public float Sunlight { get; private set; }

    /// <summary>
    /// How warm the light is, 0 (white noon) to 1 (orange at the horizon), and zero again
    /// once the sun is down. Tints the sun, the ambient fill and the sky band together.
    /// </summary>
    public float Warmth { get; private set; }

    /// <summary>Sun altitude above the horizon, in radians (negative at night).</summary>
    public float Altitude { get; private set; }

    public SkyLight() => Recompute();

    public void Advance(float dt)
    {
        if (!Paused)
        {
            TimeOfDay += dt / DayLength;
            TimeOfDay -= MathF.Floor(TimeOfDay); // wrap to [0,1)
        }
        Recompute();
    }

    /// <summary>Nudges the clock by hand (a scrub key), in fractions of a day.</summary>
    public void Scrub(float days)
    {
        TimeOfDay += days;
        TimeOfDay -= MathF.Floor(TimeOfDay);
        Recompute();
    }

    /// <summary>Sets the clock to a wall-clock hour (0..24, fractional), for a fixed time of day.</summary>
    public void SetHour(float hour)
    {
        TimeOfDay = hour / 24f;
        TimeOfDay -= MathF.Floor(TimeOfDay);
        Recompute();
    }

    /// <summary>The hour hand, 0..23.</summary>
    public int Hour => (int)(TimeOfDay * 24f) % 24;

    /// <summary>The minute hand, 0..59.</summary>
    public int Minute => (int)(TimeOfDay * 24f * 60f) % 60;

    private void Recompute()
    {
        // Altitude peaks at noon (0.5) and bottoms at midnight; azimuth sweeps a
        // half-circle from dawn to dusk so the sun tracks east → south → west.
        float phase = (TimeOfDay - 0.25f) * TwoPi;
        Altitude = MaxAltitude * MathF.Sin(phase);
        float azimuth = NoonAzimuth + (TimeOfDay - 0.5f) * MathF.PI;

        float ca = MathF.Cos(Altitude);
        var toSun = new Vector3(ca * MathF.Sin(azimuth), MathF.Sin(Altitude), ca * MathF.Cos(azimuth));
        LightDirection = Vector3.Normalize(-toSun);

        // Daylight: 0 while the sun is below the horizon, ramping in over the first
        // several degrees of altitude so dawn and dusk fade rather than snap.
        float sinAlt = MathF.Sin(Altitude);
        Daylight = Clamp01(sinAlt / MathF.Sin(0.30f));

        // Sunlight is the same idea over a far shorter climb: the disc is either up or it
        // is not. Keeping the two apart is what makes a golden hour — the sun at full
        // strength and deeply orange while the sky it fills the shade with is still dim.
        Sunlight = Clamp01(sinAlt / MathF.Sin(0.10f));

        // Colour temperature: a low sun is warm and orange, a high sun close to white.
        // Warmth is strongest right at the horizon and gone by the time it is well up —
        // and folded into how far the sun has risen, so nothing is tinted once it is down.
        Warmth = Clamp01(1f - sinAlt / 0.55f) * Sunlight;
        var noon = new Vector3(1.00f, 0.96f, 0.88f);
        var horizon = new Vector3(1.00f, 0.42f, 0.14f);
        SunColor = Vector3.Lerp(noon, horizon, Warmth) * (0.82f * Sunlight);

        // Ambient sky fill: dim moonlit blue at night, cool daylight by day, with the
        // sunset's warmth mixed back in on top. A low sun rakes almost flat across the
        // ground and so barely lights it directly — it is the fill that carries the
        // orange of a sunrise onto everything, and without this the golden hour is grey.
        var night = new Vector3(0.12f, 0.14f, 0.22f);
        var day = new Vector3(0.30f, 0.34f, 0.40f);
        var dusk = new Vector3(0.42f, 0.22f, 0.10f);
        Ambient = Vector3.Lerp(Vector3.Lerp(night, day, Daylight), dusk, Warmth);

        // Background sky: night indigo → warm band at the horizon hours → soft blue noon.
        var skyNight = new Vector3(0.05f, 0.06f, 0.11f);
        var skyDay = new Vector3(0.45f, 0.62f, 0.85f);
        var skyDusk = new Vector3(0.92f, 0.45f, 0.22f);
        Vector3 sky = Vector3.Lerp(skyNight, skyDay, Daylight);
        sky = Vector3.Lerp(sky, skyDusk, Warmth * 0.85f);
        SkyColor = new Color(sky);
    }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}
