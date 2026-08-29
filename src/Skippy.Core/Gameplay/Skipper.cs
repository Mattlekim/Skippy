using Microsoft.Xna.Framework;

namespace Skippy.Core.Gameplay;

/// <summary>
/// The player entity — a little square that accelerates toward wherever you
/// point (touch/mouse) or push (keyboard), and bounces off the screen edges.
/// Deliberately tiny; it exists to prove the shared game loop is alive on
/// every platform. Replace it with your real game objects.
/// </summary>
public sealed class Skipper
{
    public Vector2 Position;
    public Vector2 Velocity;
    public float Size = 48f;
    public Color Color = new(255, 122, 66); // warm orange

    private const float Acceleration = 2600f;
    private const float MaxSpeed = 950f;
    private const float Damping = 3.5f;
    private const float Restitution = 0.6f; // energy kept after a wall bounce

    public void Update(GameTime gameTime, Rectangle bounds, Vector2 moveInput, Vector2? pointerTarget)
    {
        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;

        Vector2 desired = moveInput;
        if (pointerTarget is Vector2 target)
        {
            Vector2 toTarget = target - Center;
            if (toTarget.LengthSquared() > 4f)
            {
                toTarget.Normalize();
                desired = toTarget;
            }
        }

        Velocity += desired * Acceleration * dt;
        Velocity -= Velocity * Damping * dt; // friction so it settles when idle
        if (Velocity.Length() > MaxSpeed)
            Velocity = Vector2.Normalize(Velocity) * MaxSpeed;

        Position += Velocity * dt;

        // Bounce off the four edges.
        if (Position.X < bounds.Left)
        {
            Position.X = bounds.Left;
            Velocity.X = -Velocity.X * Restitution;
        }
        if (Position.Y < bounds.Top)
        {
            Position.Y = bounds.Top;
            Velocity.Y = -Velocity.Y * Restitution;
        }
        if (Position.X + Size > bounds.Right)
        {
            Position.X = bounds.Right - Size;
            Velocity.X = -Velocity.X * Restitution;
        }
        if (Position.Y + Size > bounds.Bottom)
        {
            Position.Y = bounds.Bottom - Size;
            Velocity.Y = -Velocity.Y * Restitution;
        }
    }

    private Vector2 Center => Position + new Vector2(Size / 2f);
}
