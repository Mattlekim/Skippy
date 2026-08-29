using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Skippy.Core.Input;
using Skippy.Core.Worlds;

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
        GraphicsDevice.Clear(new Color(18, 22, 32));
        _world.Draw(_spriteBatch, GraphicsDevice);
        base.Draw(gameTime);
    }
}
