using System.Numerics;
using ImGuiNET;

namespace SilkNetVoronoi;

/// <summary>
/// Scene 4 — a field with "tension" between particles: every site repels its near neighbours,
/// so the points keep a roughly even minimum spacing while still drifting.
///
/// The repulsion is ported from Generative Design's M_6_1_03 (Node.attract): for two sites
/// closer than <see cref="_repelRadius"/>, the push grows as they approach and fades to zero
/// at the radius (profile <c>1/(s+1) + (s-3)/4</c> with <c>s = distance/radius</c>), and the
/// accumulated repulsion velocity is damped and speed-clamped each frame. A gentle random
/// drift keeps the field alive on top of that.
///
/// To stay fast at thousands of sites the neighbour search uses a uniform spatial grid
/// (cell = repel radius), so each site only tests the 8 surrounding cells — O(N) not O(N^2).
///
/// The tuning fields below are exposed live via <see cref="DrawUi"/> (the ImGui overlay).
/// </summary>
internal sealed class TensionFieldScene : IScene
{
    // Base wander, in NDC units/second.
    private const float DriftSpeedMin = 0.05f;
    private const float DriftSpeedMax = 0.20f;

    // Repulsion tuning (all in NDC) — adjustable at runtime via the overlay:
    private float _repelRadiusFactor = 3f;   // repel range = this * mean site spacing
    private float _strength = 5f;            // push strength; higher = firmer spacing
    private float _damping = 4f;             // repulsion-velocity decay per second
    private float _maxRepelSpeed = 0.6f;     // clamp on repulsion speed (NDC/s)

    public string Name => "Tension field";

    private int _siteCount;
    private Vector2[] _positions = [];
    private Vector2[] _drift = [];    // constant wander velocity (keeps the field moving)
    private Vector2[] _repel = [];    // accumulated repulsion velocity (damped each frame)
    private float _repelRadius;

    // Uniform grid, rebuilt every frame as a linked list: _cellHead[c] is the first site index
    // in cell c, and _next[i] chains to the next site in the same cell (-1 ends the chain).
    private int _gridDim;
    private float _cellSize;
    private int[] _cellHead = [];
    private int[] _next = [];

    public ReadOnlySpan<Vector2> Positions => _positions;

    public void Init(int siteCount)
    {
        _siteCount = siteCount;
        _positions = new Vector2[siteCount];
        _drift = new Vector2[siteCount];
        _repel = new Vector2[siteCount];
        _next = new int[siteCount];

        ConfigureGrid();   // sizes _repelRadius and the grid from the current radius factor

        var rng = Random.Shared;
        for (int i = 0; i < siteCount; i++)
        {
            // NextSingle() is [0,1); "* 2 - 1" remaps to NDC [-1, 1).
            _positions[i] = new Vector2(rng.NextSingle() * 2f - 1f, rng.NextSingle() * 2f - 1f);

            // Random heading over a full circle at a gentle speed.
            double angle = rng.NextDouble() * Math.PI * 2;
            float speed = DriftSpeedMin + rng.NextSingle() * (DriftSpeedMax - DriftSpeedMin);
            _drift[i] = new Vector2((float)Math.Cos(angle) * speed, (float)Math.Sin(angle) * speed);
        }
    }

    /// <summary>Recompute the repel radius and grid dimensions from the current radius factor.</summary>
    private void ConfigureGrid()
    {
        // Mean gap between sites in the 2x2 NDC square; the repel range is a few of these so
        // the constraint is "just active" and produces a taut, evenly-spaced look.
        float meanSpacing = 2f / MathF.Sqrt(_siteCount);
        _repelRadius = _repelRadiusFactor * meanSpacing;
        _cellSize = _repelRadius;                                   // cell == radius => neighbours fit in 3x3 cells
        _gridDim = Math.Max(1, (int)MathF.Ceiling(2f / _cellSize)); // grid covers NDC [-1, 1] on each axis
        _cellHead = new int[_gridDim * _gridDim];
    }

    public void Update(double dt)
    {
        float step = (float)dt;   // seconds since last frame (already scaled by the global speed)

        BuildGrid();
        AccumulateRepulsion(step);

        // exp(-rate*dt) is a frame-rate-independent decay (vs. a fixed per-frame factor).
        float damp = MathF.Exp(-_damping * step);
        for (int i = 0; i < _positions.Length; i++)
        {
            // Damp then clamp the repulsion velocity so close encounters can't fling sites away.
            Vector2 rv = _repel[i] * damp;
            float sp = rv.Length();
            if (sp > _maxRepelSpeed) rv *= _maxRepelSpeed / sp;
            _repel[i] = rv;

            // Move by drift + repulsion.
            Vector2 p = _positions[i] + (_drift[i] + rv) * step;

            // Bounce off the [-1, 1] edges: flip both velocity components and clamp back inside.
            if (p.X < -1f || p.X > 1f) { _drift[i].X *= -1f; _repel[i].X *= -1f; p.X = Math.Clamp(p.X, -1f, 1f); }
            if (p.Y < -1f || p.Y > 1f) { _drift[i].Y *= -1f; _repel[i].Y *= -1f; p.Y = Math.Clamp(p.Y, -1f, 1f); }
            _positions[i] = p;
        }
    }

    public void DrawUi()
    {
        ImGui.SliderFloat("Strength", ref _strength, 0f, 20f);
        ImGui.SliderFloat("Damping", ref _damping, 0f, 20f);
        ImGui.SliderFloat("Max speed", ref _maxRepelSpeed, 0.05f, 2f);
        // Changing the radius resizes the grid, so rebuild its sizing when the slider moves.
        if (ImGui.SliderFloat("Radius x spacing", ref _repelRadiusFactor, 1f, 8f))
            ConfigureGrid();
    }

    /// <summary>Bucket every site into its grid cell (linked-list build, O(N), no allocation).</summary>
    private void BuildGrid()
    {
        Array.Fill(_cellHead, -1);
        for (int i = 0; i < _positions.Length; i++)
        {
            int c = CellIndex(_positions[i]);
            _next[i] = _cellHead[c];
            _cellHead[c] = i;
        }
    }

    private int CellIndex(Vector2 p)
    {
        // NDC [-1,1] -> [0, _gridDim); clamp guards the exact edge (p == 1).
        int cx = Math.Clamp((int)((p.X + 1f) * 0.5f * _gridDim), 0, _gridDim - 1);
        int cy = Math.Clamp((int)((p.Y + 1f) * 0.5f * _gridDim), 0, _gridDim - 1);
        return cy * _gridDim + cx;
    }

    /// <summary>Add the pairwise repulsion of all neighbours within the repel radius.</summary>
    private void AccumulateRepulsion(float step)
    {
        float r = _repelRadius;
        for (int cy = 0; cy < _gridDim; cy++)
        for (int cx = 0; cx < _gridDim; cx++)
        {
            for (int i = _cellHead[cy * _gridDim + cx]; i != -1; i = _next[i])
            {
                // Visit this cell plus its 8 neighbours.
                for (int oy = -1; oy <= 1; oy++)
                for (int ox = -1; ox <= 1; ox++)
                {
                    int nx = cx + ox, ny = cy + oy;
                    if (nx < 0 || ny < 0 || nx >= _gridDim || ny >= _gridDim) continue;

                    for (int j = _cellHead[ny * _gridDim + nx]; j != -1; j = _next[j])
                    {
                        if (j <= i) continue;   // handle each unordered pair exactly once
                        Repel(i, j, r, step);
                    }
                }
            }
        }
    }

    private void Repel(int i, int j, float r, float step)
    {
        Vector2 delta = _positions[i] - _positions[j];   // points from j toward i
        float dist = delta.Length();
        if (dist <= 1e-6f || dist >= r) return;          // ignore coincident or out-of-range pairs

        // Generative Design profile: s in (0,1); the bracket is ~0.25 when touching and 0 at
        // the radius, so the push is firm up close and fades smoothly to nothing at range.
        float s = dist / r;
        float profile = 1f / (s + 1f) + (s - 3f) / 4f;
        float accel = s * 9f * _strength * profile / dist;   // scalar acceleration magnitude

        // Push the two sites apart (equal and opposite). delta * accel is the per-site
        // acceleration vector (its magnitude is accel * dist); scale by the timestep.
        Vector2 push = delta * (accel * step);
        _repel[i] += push;
        _repel[j] -= push;
    }
}
