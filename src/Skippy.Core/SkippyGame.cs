using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Skippy.Core.Diagnostics;
using Skippy.Core.Input;
using Skippy.Core.Worlds;
using Skippy.Core.Worlds.Generation;
using Skippy.Core.Worlds.Mobs;
using Skippy.Core.Worlds.Rendering;

namespace Skippy.Core;

/// <summary>
/// The one and only game class. Every platform head (Desktop, Android, Web)
/// creates and runs an instance of this. It owns the active <see cref="World"/>
/// and does nothing more than pump input → world.Update → world.Draw.
///
/// Written against the standard <c>Microsoft.Xna.Framework</c> API, provided by
/// MonoGame on desktop/Android and by KNI (an API-compatible fork) in the browser.
/// </summary>
public class SkippyGame : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private SpriteBatch _spriteBatch = null!;

    private readonly InputState _input = new();

    /// <summary>
    /// What the world is given to read while the settings panel is open: an input state that
    /// is never updated, so everything in it reads as untouched. The panel and the camera
    /// share the arrow keys, and this is what stops both acting on the same press.
    /// </summary>
    private readonly InputState _idle = new();

    private World _world = null!;
    private HudOverlay _hud = null!;
    private SettingsMenu _settings = null!;

    public SkippyGame()
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1280,
            PreferredBackBufferHeight = 720,

            // A depth buffer for the depth-tested vegetation/mob path (toggled with B).
            // It is the default format anyway, but the painter path never needed one, so
            // ask for it explicitly rather than lean on the platform default — the browser
            // (KNI/WebGL) head in particular. Clear(SkyColor) resets it to far each frame.
            PreferredDepthStencilFormat = DepthFormat.Depth24Stencil8,
        };
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
    }

    protected override void Initialize()
    {
        Window.AllowUserResizing = true;
        base.Initialize();
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);

        // Build the world once a GraphicsDevice exists (the renderer needs it).
        _world = new World(seed: 1337);
        _world.InitializeGraphics(GraphicsDevice);

        _hud = new HudOverlay(GraphicsDevice);
        _settings = new SettingsMenu(GraphicsDevice, _world);
    }

    protected override void Update(GameTime gameTime)
    {
        _input.Update();

        if (_input.ToggleSettingsRequested)
            _settings.Toggle();

        // Escape closes the settings panel if it is open, and only quits from the world.
        if (_input.CancelRequested)
        {
            if (_settings.IsOpen)
                _settings.Close();
            else
                try { Exit(); } catch { /* not supported on this platform (e.g. WASM) */ }
        }

        if (_input.ToggleFullscreenRequested)
        {
            _graphics.IsFullScreen = !_graphics.IsFullScreen;
            _graphics.ApplyChanges();
        }

        if (_settings.IsOpen)
            _settings.Update(_input, (float)gameTime.ElapsedGameTime.TotalSeconds);

        // The world keeps running behind the panel — the sun crosses the sky, chunks stream —
        // it just stops listening, so a key pressed at the panel doesn't also move the camera.
        _world.Update(gameTime, _settings.IsOpen ? _idle : _input);

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        // Black, not the sky's colour: the world has ends now (see WorldBounds), and past
        // a pole there is nothing — not unlit sky, nothing. The sky still colours the
        // light falling on the world itself.
        GraphicsDevice.Clear(Color.Black);
        _world.Draw(_spriteBatch, GraphicsDevice);
        DrawHud();
        _settings.Draw(_spriteBatch, GraphicsDevice.Viewport);
        base.Draw(gameTime);
    }

    /// <summary>Reused every frame so the read-out costs no allocation to assemble.</summary>
    private readonly List<string> _hudLines = new();

    /// <summary>
    /// The debug read-out: the seed and where the camera is looking, the world settings, the
    /// coordinates, biome and climate of the tile under the pointer while it is over loaded
    /// ground, and the vitals of the mob that has been clicked on.
    /// </summary>
    private void DrawHud()
    {
        Viewport viewport = GraphicsDevice.Viewport;
        _hudLines.Clear();
        _hudLines.Add("SEED " + _world.Seed);
        _hudLines.Add("POS " + Coords((int)MathF.Floor(_world.Camera.FocusTile.X),
                                      (int)MathF.Floor(_world.Camera.FocusTile.Y)));
        _hudLines.Add("TILE " + Iso.TileWidth);
        _hudLines.Add("VIEW " + _world.MapView.Name());
        _hudLines.Add("DEPTH " + (_world.DepthBuffer ? "ON" : "OFF") + " (B)");

        // The clock reads as a wall clock, and says so when it is being held still.
        _hudLines.Add("TIME " + _world.Sky.Hour.ToString("00", CultureInfo.InvariantCulture)
                    + ":" + _world.Sky.Minute.ToString("00", CultureInfo.InvariantCulture)
                    + (_world.Sky.Paused ? " HELD" : string.Empty));

        // The mouse reports a position even when it is outside the window, so check
        // it is actually over the world before reading a tile off it.
        if (_input.PointerPosition is Vector2 pointer
            && viewport.Bounds.Contains((int)pointer.X, (int)pointer.Y)
            && _world.TryGetTileAtScreen(pointer, viewport, out Tile hovered, out Point at)
            && !hovered.IsVoid)
        {
            _hudLines.Add("AT " + Coords(at.X, at.Y));
            _hudLines.Add("LAT " + Percent(_world.Bounds.Latitude(WorldBounds.V(at.X, at.Y))));
            _hudLines.Add("BIO " + hovered.Biome.Name());
            if (Forests.Wooded(hovered.Biome))
                _hudLines.Add("WOOD " + hovered.Forest.Name());
            _hudLines.Add("HGT " + Percent(hovered.Height));
            _hudLines.Add("TMP " + (int)MathF.Round(hovered.TemperatureCelsius) + "C");
            _hudLines.Add("WET " + Percent(hovered.Moisture));
        }

        AppendMobLines();
        _hud.Draw(_spriteBatch, viewport, _hudLines);
    }

    /// <summary>
    /// The selected mob's vitals — what it is, what it is doing, and how it is holding up.
    /// Ailments only get a line when it has some; a healthy animal has nothing to say there.
    /// </summary>
    private void AppendMobLines()
    {
        if (_world.SelectedMob is not Mob mob)
            return;

        Vitals v = mob.Vitals;
        _hudLines.Add(Upper(mob.Kind) + " " + Upper(v.Gender));
        _hudLines.Add("AGE " + (int)v.AgeDays + "D " + Upper(v.Stage));
        _hudLines.Add("HP " + (int)MathF.Ceiling(v.Health) + " OF " + (int)v.MaxHealth);
        _hudLines.Add("HUNGER " + Percent(v.Hunger));
        _hudLines.Add("TIRED " + Percent(v.Tiredness));
        _hudLines.Add("DOING " + Upper(mob.Activity));

        if (v.Ailments != MobAilment.None)
            _hudLines.Add("ILL " + v.Ailments.ToString().Replace(",", string.Empty).ToUpperInvariant());
    }

    /// <summary>An enum's name in the upper case the 3×5 read-out font is cut in.</summary>
    private static string Upper<T>(T value) where T : struct
        => value.ToString()!.ToUpperInvariant();

    /// <summary>A tile position as "x y" — east first, then south, both signed.</summary>
    private static string Coords(int x, int y) => x + " " + y;

    /// <summary>A 0..1 field as whole percent — a 3×5 font has no room for decimals.</summary>
    private static int Percent(float value) => (int)MathF.Round(value * 100f);
}
