using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using StbImageSharp;

namespace SilkNetVoronoi;

/// <summary>
/// Scene 3 — a picture turned into a Voronoi diagram. Site "home" positions are
/// rejection-sampled from an image, weighted by darkness, so cells cluster onto the
/// picture's features and the partition reproduces it. Pressing <c>D</c> toggles between
/// the assembled image and a scattered cloud; the sites ease between the two, so the
/// picture explodes apart and reassembles.
///
/// The image defaults to <c>assets/sample.png</c> (copied next to the binary) and can be
/// overridden with a path. If it can't be loaded, a procedural high-contrast target is
/// used so the scene never hard-fails.
/// </summary>
internal sealed class ImageDivergeScene : IScene
{
    private float _easeSeconds = 0.8f;   // time to fully scatter or reassemble (seconds)

    public string Name => "Image diverge";

    private readonly string? _imagePath;

    private Vector2[] _home = [];        // assembled (image) positions
    private Vector2[] _scattered = [];   // exploded positions
    private Vector2[] _current = [];

    private float _t;            // 0 = assembled, 1 = scattered
    private float _target;       // eases _t toward this

    public ImageDivergeScene(string? imagePath) => _imagePath = imagePath;

    public ReadOnlySpan<Vector2> Positions => _current;

    public void Init(int siteCount)
    {
        var (w, h, weight) = LoadWeights();

        _home = new Vector2[siteCount];
        _scattered = new Vector2[siteCount];
        _current = new Vector2[siteCount];

        // Aspect-preserving fit into the [-1, 1] square (letterbox the longer side so the
        // picture isn't stretched). a = image aspect ratio; the wider axis maps to full [-1,1]
        // and the other is scaled down by 1/a (or a) to keep pixels square.
        float a = (float)w / h;
        float sx = a >= 1f ? 1f : a;
        float sy = a >= 1f ? 1f / a : 1f;

        var rng = Random.Shared;
        int placed = 0;
        long attempts = 0;
        long maxAttempts = (long)siteCount * 2000;   // safety cap so a near-blank image can't loop forever
        while (placed < siteCount && attempts < maxAttempts)
        {
            attempts++;
            int px = rng.Next(w);
            int py = rng.Next(h);
            // Rejection sampling: accept this pixel as a site with probability weight^2.
            // Squaring the darkness weight pushes the few near-white pixels' odds toward 0,
            // so sites land overwhelmingly on the picture's dark features.
            float p = weight[py * w + px];
            if (rng.NextDouble() > p * p) continue;

            // Pixel -> NDC. u,v are [0,1] across the image; v is flipped because image row 0
            // is the top while NDC +y is up. Scale by the aspect-fit factors.
            float u = px / (float)(w - 1);
            float v = py / (float)(h - 1);
            _home[placed] = new Vector2((u * 2f - 1f) * sx, (1f - 2f * v) * sy);
            // A fixed random target in [-1,1]^2 that this site flies to when "diverged".
            _scattered[placed] = new Vector2(
                (float)rng.NextDouble() * 2f - 1f,
                (float)rng.NextDouble() * 2f - 1f);
            placed++;
        }
        // If the image was nearly blank, fill any remainder uniformly.
        for (; placed < siteCount; placed++)
        {
            _home[placed] = new Vector2((float)rng.NextDouble() * 2f - 1f, (float)rng.NextDouble() * 2f - 1f);
            _scattered[placed] = new Vector2((float)rng.NextDouble() * 2f - 1f, (float)rng.NextDouble() * 2f - 1f);
        }

        _home.CopyTo(_current, 0);
        _t = 0f;
        _target = 0f;
    }

    public void OnKey(Key key)
    {
        // D flips the goal: if currently assembled-ish (<0.5) go scatter (1), else reassemble (0).
        if (key == Key.D) _target = _target > 0.5f ? 0f : 1f;
    }

    public void DrawUi()
    {
        if (ImGui.Button(_target > 0.5f ? "Assemble" : "Scatter")) _target = _target > 0.5f ? 0f : 1f;
        ImGui.SliderFloat("Ease (s)", ref _easeSeconds, 0.1f, 4f);
    }

    public void Update(double dt)
    {
        // Move _t toward _target at a constant rate (1 / EaseSeconds per second), clamped so
        // it lands exactly on 0 or 1 without overshooting.
        float dir = MathF.Sign(_target - _t);
        if (dir != 0f)
        {
            _t += dir * (float)dt / _easeSeconds;
            _t = Math.Clamp(_t, 0f, 1f);
        }
        // Smoothstep (3t^2 - 2t^3): eases in/out so the scatter/assemble starts and stops gently.
        float s = _t * _t * (3f - 2f * _t);
        for (int i = 0; i < _current.Length; i++)
            _current[i] = Vector2.Lerp(_home[i], _scattered[i], s);   // 0 = home (image), 1 = scattered
    }

    /// <summary>Per-pixel darkness weight (1 = black, 0 = white) plus image dimensions.</summary>
    private (int w, int h, float[] weight) LoadWeights()
    {
        string path = _imagePath ?? Path.Combine(AppContext.BaseDirectory, "assets", "sample.png");
        try
        {
            ImageResult img = ImageResult.FromMemory(File.ReadAllBytes(path), ColorComponents.RedGreenBlueAlpha);
            var weight = new float[img.Width * img.Height];
            for (int i = 0; i < weight.Length; i++)
            {
                // Data is tightly packed RGBA bytes, so pixel i starts at byte i*4.
                byte r = img.Data[i * 4 + 0], g = img.Data[i * 4 + 1], b = img.Data[i * 4 + 2];
                // Rec. 601 luma weights (eye is most sensitive to green); /255 -> [0,1].
                float lum = (0.299f * r + 0.587f * g + 0.114f * b) / 255f;
                weight[i] = 1f - lum;   // invert: dark pixels get the high sampling weight
            }
            Console.WriteLine($"Image scene: loaded {path} ({img.Width}x{img.Height})");
            return (img.Width, img.Height, weight);
        }
        catch (Exception e)
        {
            Console.WriteLine($"Image scene: could not load '{path}' ({e.Message}); using procedural target.");
            return GenerateFallback();
        }
    }

    /// <summary>A built-in high-contrast heart, so F3 works even with no image file.</summary>
    private static (int w, int h, float[] weight) GenerateFallback()
    {
        const int n = 256;   // fallback grid resolution (256x256 is plenty for sampling)
        var weight = new float[n * n];
        for (int py = 0; py < n; py++)
        for (int px = 0; px < n; px++)
        {
            // Map pixel to [-1.5, 1.5] (the *1.5 zooms out so the whole heart fits in frame),
            // y flipped so +y is up.
            float nx = (px / (float)(n - 1) * 2f - 1f) * 1.5f;
            float ny = (1f - py / (float)(n - 1) * 2f) * 1.5f;
            // Classic heart implicit curve: (x^2 + y^2 - 1)^3 - x^2 * y^3 <= 0 is inside.
            float r = nx * nx + ny * ny - 1f;
            bool inside = r * r * r - nx * nx * ny * ny * ny <= 0f;
            weight[py * n + px] = inside ? 0.92f : 0.02f;   // high weight inside the heart, near-zero outside
        }
        return (n, n, weight);
    }
}
