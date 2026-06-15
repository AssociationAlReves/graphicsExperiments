using System.Numerics;

namespace SilkNetVoronoi;

/// <summary>
/// Scene 1 — a field of sites drifting in random directions and bouncing off the screen
/// edges. The original sample scene.
/// </summary>
internal sealed class RandomFieldScene : IScene
{
    public string Name => "Random field";

    private Vector2[] _positions = [];
    private Vector2[] _velocities = [];

    public ReadOnlySpan<Vector2> Positions => _positions;

    public void Init(int siteCount)
    {
        _positions = new Vector2[siteCount];
        _velocities = new Vector2[siteCount];

        var rng = Random.Shared;
        for (int i = 0; i < siteCount; i++)
        {
            // NextDouble() is [0,1); "* 2 - 1" remaps it to the NDC range [-1, 1).
            _positions[i] = new Vector2(
                (float)rng.NextDouble() * 2f - 1f,
                (float)rng.NextDouble() * 2f - 1f);

            // Random heading over a full circle, with a speed of 0.05..0.20 NDC units/second
            // (the screen spans 2 NDC units, so ~10..40 s to cross — a gentle drift).
            double angle = rng.NextDouble() * Math.PI * 2;
            float speed = 0.05f + (float)rng.NextDouble() * 0.15f;
            _velocities[i] = new Vector2(
                (float)Math.Cos(angle) * speed,
                (float)Math.Sin(angle) * speed);
        }
    }

    public void Update(double dt)
    {
        float step = (float)dt;   // seconds since last frame
        for (int i = 0; i < _positions.Length; i++)
        {
            Vector2 p = _positions[i] + _velocities[i] * step;

            // Bounce off the [-1, 1] edges: flip the velocity component and clamp back inside.
            if (p.X < -1f || p.X > 1f) { _velocities[i].X *= -1f; p.X = Math.Clamp(p.X, -1f, 1f); }
            if (p.Y < -1f || p.Y > 1f) { _velocities[i].Y *= -1f; p.Y = Math.Clamp(p.Y, -1f, 1f); }
            _positions[i] = p;
        }
    }
}
