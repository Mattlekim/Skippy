using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;

namespace Skippy.Core.Input;

/// <summary>
/// Collapses keyboard, mouse and touch into one small, platform-neutral snapshot
/// the game logic can read. This is the seam that lets the exact same controls
/// feel native on a PC (keyboard + mouse + wheel) and on a phone (touch drag).
///
/// Call <see cref="Update"/> once per frame before reading the properties.
/// </summary>
public sealed class InputState
{
    /// <summary>Normalised movement direction from the keyboard / D-pad (screen-space).</summary>
    public Vector2 MoveDirection { get; private set; }

    /// <summary>Screen-space position of the active pointer (touch or mouse), if any.</summary>
    public Vector2? PointerPosition { get; private set; }

    /// <summary>True while the pointer is pressed (finger down / left mouse button).</summary>
    public bool PointerDown { get; private set; }

    /// <summary>True only on the frame the pointer goes down — a click, rather than a drag.</summary>
    public bool PointerPressed { get; private set; }

    /// <summary>Pointer movement in screen pixels since last frame, while pressed (else zero).</summary>
    public Vector2 PointerDelta { get; private set; }

    /// <summary>Mouse-wheel change since last frame: positive = scroll up.</summary>
    public int ScrollDelta { get; private set; }

    public bool ToggleFullscreenRequested { get; private set; }

    /// <summary>True only on the frame the tile-grid overlay key (G) goes down.</summary>
    public bool ToggleTileGridRequested { get; private set; }

    /// <summary>-1 while [ is held, +1 while ] is held: shrink / grow the tiles. 0 otherwise.</summary>
    public int TileSizeDirection { get; private set; }

    /// <summary>True only on the frame the map-view key (M) goes down.</summary>
    public bool CycleMapViewRequested { get; private set; }

    /// <summary>-1 while , is held, +1 while . is held: wind the time of day back / forward. 0 otherwise.</summary>
    public int TimeDirection { get; private set; }

    /// <summary>True only on the frame the settings key (TAB) goes down.</summary>
    public bool ToggleSettingsRequested { get; private set; }

    /// <summary>-1 the frame UP goes down, +1 the frame DOWN does: move between settings.</summary>
    public int MenuVertical { get; private set; }

    /// <summary>-1 while LEFT is held, +1 while RIGHT is held: change the selected setting.</summary>
    public int MenuHorizontal { get; private set; }

    /// <summary>True only on the frame ENTER or SPACE goes down: act on the selected setting.</summary>
    public bool MenuSelectRequested { get; private set; }

    /// <summary>True only on the frame ESCAPE goes down.</summary>
    public bool CancelRequested { get; private set; }

    /// <summary>True only on the frame the clock key (P) goes down: hold the sun, or let it run again.</summary>
    public bool ToggleClockRequested { get; private set; }

    /// <summary>True only on the frame the depth-buffer key (B) goes down: switch the depth-tested draw path on or off.</summary>
    public bool ToggleDepthBufferRequested { get; private set; }

    private KeyboardState _prevKeyboard;
    private Vector2 _prevPointer;
    private bool _prevPointerDown;
    private int _prevScroll;

    public void Update()
    {
        KeyboardState keyboard = Keyboard.GetState();
        MouseState mouse = Mouse.GetState();

        var move = Vector2.Zero;
        if (keyboard.IsKeyDown(Keys.Left) || keyboard.IsKeyDown(Keys.A)) move.X -= 1;
        if (keyboard.IsKeyDown(Keys.Right) || keyboard.IsKeyDown(Keys.D)) move.X += 1;
        if (keyboard.IsKeyDown(Keys.Up) || keyboard.IsKeyDown(Keys.W)) move.Y -= 1;
        if (keyboard.IsKeyDown(Keys.Down) || keyboard.IsKeyDown(Keys.S)) move.Y += 1;
        if (move != Vector2.Zero) move.Normalize();
        MoveDirection = move;

        ToggleFullscreenRequested =
            keyboard.IsKeyDown(Keys.F11) && _prevKeyboard.IsKeyUp(Keys.F11);
        ToggleTileGridRequested =
            keyboard.IsKeyDown(Keys.G) && _prevKeyboard.IsKeyUp(Keys.G);
        CycleMapViewRequested =
            keyboard.IsKeyDown(Keys.M) && _prevKeyboard.IsKeyUp(Keys.M);
        ToggleClockRequested =
            keyboard.IsKeyDown(Keys.P) && _prevKeyboard.IsKeyUp(Keys.P);
        ToggleDepthBufferRequested =
            keyboard.IsKeyDown(Keys.B) && _prevKeyboard.IsKeyUp(Keys.B);

        // Held, not edge-triggered: the world applies its own press-and-repeat.
        int tileDir = 0;
        if (keyboard.IsKeyDown(Keys.OemOpenBrackets)) tileDir -= 1;
        if (keyboard.IsKeyDown(Keys.OemCloseBrackets)) tileDir += 1;
        TileSizeDirection = tileDir;

        // Held too, but scrubbed smoothly rather than in steps: the sun should sweep.
        int timeDir = 0;
        if (keyboard.IsKeyDown(Keys.OemComma)) timeDir -= 1;
        if (keyboard.IsKeyDown(Keys.OemPeriod)) timeDir += 1;
        TimeDirection = timeDir;

        // The settings panel. Its keys double as the camera's while it is closed, so the
        // game decides which of the two is listening; this only reports what was pressed.
        ToggleSettingsRequested =
            keyboard.IsKeyDown(Keys.Tab) && _prevKeyboard.IsKeyUp(Keys.Tab);
        CancelRequested =
            keyboard.IsKeyDown(Keys.Escape) && _prevKeyboard.IsKeyUp(Keys.Escape);
        MenuSelectRequested =
            (keyboard.IsKeyDown(Keys.Enter) && _prevKeyboard.IsKeyUp(Keys.Enter))
            || (keyboard.IsKeyDown(Keys.Space) && _prevKeyboard.IsKeyUp(Keys.Space));

        int menuY = 0;
        if (keyboard.IsKeyDown(Keys.Up) && _prevKeyboard.IsKeyUp(Keys.Up)) menuY -= 1;
        if (keyboard.IsKeyDown(Keys.Down) && _prevKeyboard.IsKeyUp(Keys.Down)) menuY += 1;
        MenuVertical = menuY;

        int menuX = 0;
        if (keyboard.IsKeyDown(Keys.Left)) menuX -= 1;
        if (keyboard.IsKeyDown(Keys.Right)) menuX += 1;
        MenuHorizontal = menuX;

        _prevKeyboard = keyboard;

        // Prefer touch (phones / touch laptops); fall back to the mouse.
        TouchCollection touches = TouchPanel.GetState();
        if (touches.Count > 0)
        {
            TouchLocation t = touches[0];
            PointerPosition = t.Position;
            PointerDown = t.State is TouchLocationState.Pressed or TouchLocationState.Moved;
        }
        else
        {
            PointerPosition = new Vector2(mouse.X, mouse.Y);
            PointerDown = mouse.LeftButton == ButtonState.Pressed;
        }

        Vector2 pointer = PointerPosition ?? Vector2.Zero;
        PointerDelta = (PointerDown && _prevPointerDown) ? pointer - _prevPointer : Vector2.Zero;
        PointerPressed = PointerDown && !_prevPointerDown;
        _prevPointer = pointer;
        _prevPointerDown = PointerDown;

        ScrollDelta = mouse.ScrollWheelValue - _prevScroll;
        _prevScroll = mouse.ScrollWheelValue;
    }
}
