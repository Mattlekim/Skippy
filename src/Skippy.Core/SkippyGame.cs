using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Skippy.Core.Gameplay;
using Skippy.Core.Input;

namespace Skippy.Core;

/// <summary>
/// The one and only game class. Every platform head (Desktop, Android, Web)
/// creates and runs an instance of this. It is written against the standard
/// <c>Microsoft.Xna.Framework</c> API, which is provided by MonoGame on
/// desktop/Android and by KNI (an API-compatible MonoGame fork) in the browser.
///
/// Keep ALL game logic in this project (Skippy.Core). The head projects should
/// stay tiny — just enough to bootstrap the platform and hand control to here.
/// </summary>
public class SkippyGame : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private SpriteBatch _spriteBatch = null!;

    /// <summary>A 1x1 white texture generated at runtime so the sample runs
    /// even before any content (.xnb) has been built by the pipeline.</summary>
    private Texture2D _pixel = null!;

    private readonly InputState _input = new();
    private Skipper _skipper = null!;

    public SkippyGame()
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1280,
            PreferredBackBufferHeight = 720,
        };

        // The content root is relative to each head's output folder.
        Content.RootDirectory = "Content";

        // Show the OS cursor on desktop/web; harmless on Android.
        IsMouseVisible = true;
    }

    protected override void Initialize()
    {
        Window.AllowUserResizing = true;
        base.Initialize();

        _skipper = new Skipper
        {
            Position = new Vector2(
                GraphicsDevice.Viewport.Width / 2f,
                GraphicsDevice.Viewport.Height / 2f),
        };
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);

        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData(new[] { Color.White });

        // When you start adding real assets, load them here, e.g.:
        //   _font = Content.Load<SpriteFont>("Fonts/Default");
        //   _logo = Content.Load<Texture2D>("Sprites/Logo");
        // See Content/README.md for how the content pipeline is wired.
    }

    protected override void Update(GameTime gameTime)
    {
        _input.Update();

        if (_input.ExitRequested)
        {
            // Exit() is unsupported on some platforms (e.g. WebAssembly); guard it.
            try { Exit(); } catch { /* not supported on this platform */ }
        }

        if (_input.ToggleFullscreenRequested)
        {
            _graphics.IsFullScreen = !_graphics.IsFullScreen;
            _graphics.ApplyChanges();
        }

        var bounds = new Rectangle(0, 0,
            GraphicsDevice.Viewport.Width,
            GraphicsDevice.Viewport.Height);

        // Follow the pointer while it's held (touch/mouse), otherwise steer with
        // the keyboard/D-pad. This gives one control scheme that feels right on
        // both a phone and a PC.
        Vector2? pointerTarget = _input.PointerDown ? _input.PointerPosition : null;
        _skipper.Update(gameTime, bounds, _input.MoveDirection, pointerTarget);

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(new Color(18, 18, 28));

        _spriteBatch.Begin();
        DrawBackdrop(gameTime);

        // The player square.
        _spriteBatch.Draw(
            _pixel,
            new Rectangle(
                (int)_skipper.Position.X,
                (int)_skipper.Position.Y,
                (int)_skipper.Size,
                (int)_skipper.Size),
            _skipper.Color);

        _spriteBatch.End();
        base.Draw(gameTime);
    }

    /// <summary>A cheap animated grid so all three platforms visibly show a
    /// live, running game loop out of the box.</summary>
    private void DrawBackdrop(GameTime gameTime)
    {
        int w = GraphicsDevice.Viewport.Width;
        int h = GraphicsDevice.Viewport.Height;
        const int cell = 64;
        int offset = (int)(gameTime.TotalGameTime.TotalSeconds * 20) % cell;
        var line = new Color(40, 40, 60);

        for (int x = -cell + offset; x < w; x += cell)
            _spriteBatch.Draw(_pixel, new Rectangle(x, 0, 1, h), line);
        for (int y = -cell + offset; y < h; y += cell)
            _spriteBatch.Draw(_pixel, new Rectangle(0, y, w, 1), line);
    }
}
