using System;
using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Skippy.Core.Diagnostics;
using Skippy.Core.Input;
using Skippy.Core.Worlds;
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
    private World _world = null!;
    private HudOverlay _hud = null!;

    public SkippyGame()
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1280,
            PreferredBackBufferHeight = 720,
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
    }

    protected override void Update(GameTime gameTime)
    {
        _input.Update();

        if (_input.ExitRequested)
        {
            try { Exit(); } catch { /* not supported on this platform (e.g. WASM) */ }
        }

        if (_input.ToggleFullscreenRequested)
        {
            _graphics.IsFullScreen = !_graphics.IsFullScreen;
            _graphics.ApplyChanges();
        }

        _world.Update(gameTime, _input);

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(_world.Sky.SkyColor);
        _world.Draw(_spriteBatch, GraphicsDevice);
        DrawHud();
        base.Draw(gameTime);
    }

    /// <summary>
    /// The debug read-out: the world settings, plus the climate of the tile under
    /// the pointer while it is over loaded ground.
    /// </summary>
    private void DrawHud()
    {
        Viewport viewport = GraphicsDevice.Viewport;
        string tile = "TILE " + Iso.TileWidth;
        string view = "VIEW " + _world.MapView.Name();
        string river = "RIVER " + WorldConstants.RiverWidth.ToString("0.0", CultureInfo.InvariantCulture);

        // The mouse reports a position even when it is outside the window, so check
        // it is actually over the world before reading a tile off it.
        if (_input.PointerPosition is Vector2 pointer
            && viewport.Bounds.Contains((int)pointer.X, (int)pointer.Y)
            && _world.TryGetTileAtScreen(pointer, viewport, out Tile hovered))
        {
            _hud.Draw(_spriteBatch, viewport, tile, view, river,
                      "HGT " + Percent(hovered.Height),
                      "TMP " + (int)MathF.Round(hovered.TemperatureCelsius) + "C",
                      "WET " + Percent(hovered.Moisture));
            return;
        }

        _hud.Draw(_spriteBatch, viewport, tile, view, river);
    }

    /// <summary>A 0..1 field as whole percent — a 3×5 font has no room for decimals.</summary>
    private static int Percent(float value) => (int)MathF.Round(value * 100f);
}
