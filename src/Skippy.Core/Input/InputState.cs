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

    /// <summary>Pointer movement in screen pixels since last frame, while pressed (else zero).</summary>
    public Vector2 PointerDelta { get; private set; }

    /// <summary>Mouse-wheel change since last frame: positive = scroll up.</summary>
    public int ScrollDelta { get; private set; }

    public bool ExitRequested { get; private set; }
    public bool ToggleFullscreenRequested { get; private set; }

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

        ExitRequested = keyboard.IsKeyDown(Keys.Escape);
        ToggleFullscreenRequested =
            keyboard.IsKeyDown(Keys.F11) && _prevKeyboard.IsKeyUp(Keys.F11);
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
        _prevPointer = pointer;
        _prevPointerDown = PointerDown;

        ScrollDelta = mouse.ScrollWheelValue - _prevScroll;
        _prevScroll = mouse.ScrollWheelValue;
    }
}
