using System.Numerics;
using ImGuiNET;

namespace SilkNetVoronoi;

/// <summary>
/// Scene 2 — a blood vein. A horizontal tube (centerline y=0, half-height <see cref="H"/>)
/// carries ~60% of the sites as "flow cells" streaming left→right with a Poiseuille profile
/// (fastest mid-vein, zero at the walls); a flow cell that exits the right edge re-enters at
/// the left at a fresh random height. The remaining ~40% are "body cells" scattered in the
/// surrounding tissue (|y| &gt; H) and stay put. The result is a band of moving Voronoi cells
/// framed by static ones.
/// </summary>
internal sealed class BloodVeinScene : IScene
{
    private float _h = 0.32f;               // vein half-height in NDC: the tube spans y in [-H, H]
    private float _baseSpeed = 0.6f;        // peak flow speed at the centerline (NDC units/second)
    private const float FlowFraction = 0.6f; // fraction of sites that are flowing cells (fixed: re-partitioning needs a re-Init)

    public string Name => "Blood vein";

    private Vector2[] _positions = [];
    private int _flowCount;                  // first _flowCount entries of _positions are flow cells
    private Random _rng = Random.Shared;

    public ReadOnlySpan<Vector2> Positions => _positions;

    public void Init(int siteCount)
    {
        _positions = new Vector2[siteCount];
        _flowCount = (int)(siteCount * FlowFraction);
        _rng = Random.Shared;

        // Flow cells: anywhere inside the tube. x in [-1, 1] (full width), y in [-H, H]
        // (inside the vein). "* 2 - 1" maps the [0,1) random to [-1,1); "* _h" narrows y to the tube.
        for (int i = 0; i < _flowCount; i++)
            _positions[i] = new Vector2(
                (float)_rng.NextDouble() * 2f - 1f,
                ((float)_rng.NextDouble() * 2f - 1f) * _h);

        // Body cells: static tissue outside the tube, in the two bands |y| in [H, 1].
        for (int i = _flowCount; i < siteCount; i++)
        {
            float x = (float)_rng.NextDouble() * 2f - 1f;
            float t = (float)_rng.NextDouble();                  // 0..1 position within one band
            float y = _h + t * (1f - _h);                        // map into the upper band [H, 1]
            if (_rng.Next(2) == 0) y = -y;                       // half the time, mirror to the lower band
            _positions[i] = new Vector2(x, y);
        }
    }

    public void Update(double dt)
    {
        float step = (float)dt;   // seconds since last frame
        for (int i = 0; i < _flowCount; i++)
        {
            float y = _positions[i].Y;
            // Poiseuille (laminar) profile: speed peaks at the centerline and is 0 at the walls.
            // (y / _h) is the normalized distance to the wall; 1 - (y/_h)^2 is the parabola.
            // Max(0, ...) guards against negatives if _h is lowered below an existing cell.
            float profile = MathF.Max(0f, 1f - (y / _h) * (y / _h));
            float x = _positions[i].X + _baseSpeed * profile * step;

            if (x > 1f)   // exited the right edge: re-enter at the left inlet at a fresh height
            {
                x = -1f;
                y = ((float)_rng.NextDouble() * 2f - 1f) * _h;
            }
            _positions[i] = new Vector2(x, y);
        }
        // Body cells are static — left untouched.
    }

    public void DrawUi()
    {
        ImGui.SliderFloat("Flow speed", ref _baseSpeed, 0f, 3f);
        ImGui.SliderFloat("Vein half-height", ref _h, 0.05f, 0.95f);
    }
}
