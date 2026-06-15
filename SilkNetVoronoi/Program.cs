using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace SilkNetVoronoi;

/// <summary>
/// Random moving field of Voronoi sites on plain OpenGL 3.3 via Silk.NET, rendered
/// black-and-white (black background, white cell borders, a white dot at each site).
///
/// This host owns the window, input, the CPU site simulation and the shared per-instance
/// position buffer, plus a live ms/FPS readout. The actual Voronoi rendering is delegated
/// to a swappable <see cref="IVoronoiRenderer"/>; two techniques are provided and toggled
/// at runtime with Space/Tab:
///   • <see cref="ConeRenderer"/> — cone rasterization with a bounded cone radius.
///   • <see cref="JfaRenderer"/>  — Jump Flooding (ping-pong FBOs), site-count independent.
/// </summary>
internal static class Program
{
    private const int SiteCount = 8192;

    private static IWindow _window = null!;
    private static GL _gl = null!;

    private static uint _instanceVbo;

    private static IVoronoiRenderer[] _renderers = [];
    private static int _active;

    // Per-site CPU state.
    private static readonly Vector2[] _positions = new Vector2[SiteCount];
    private static readonly Vector2[] _velocities = new Vector2[SiteCount];

    // Per-instance data uploaded each frame: site position (vec2).
    private static readonly float[] _instanceData = new float[SiteCount * 2];

    // Frame-time readout.
    private static double _accumTime;
    private static int _accumFrames;

    // Initial technique index; overridable via the first CLI arg ("jfa" / "cone").
    private static int _startTechnique;

    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("jfa", StringComparison.OrdinalIgnoreCase))
            _startTechnique = 1;

        var options = WindowOptions.Default with
        {
            Size = new Vector2D<int>(1000, 1000),
            Title = "Silk.NET — Voronoi Moving Field (B&W)",
            API = new GraphicsAPI(
                ContextAPI.OpenGL,
                ContextProfile.Core,
                ContextFlags.Default,
                new APIVersion(3, 3)),
        };

        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.Update += OnUpdate;
        _window.FramebufferResize += OnResize;
        _window.Closing += OnClosing;
        _window.Run();
    }

    private static void OnLoad()
    {
        var input = _window.CreateInput();
        foreach (var kb in input.Keyboards)
            kb.KeyDown += OnKeyDown;

        // VSync starts off so the ms/FPS readout reflects real GPU cost (this sample exists
        // to compare techniques); the frame rate is otherwise uncapped. Toggle with the V key.
        _window.VSync = false;

        _gl = GL.GetApi(_window);
        _gl.ClearColor(0f, 0f, 0f, 1f);
        _gl.Enable(EnableCap.ProgramPointSize);

        int fbW = _window.FramebufferSize.X;
        int fbH = _window.FramebufferSize.Y;

        InitSites();
        BuildInstanceBuffer();

        _renderers = [new ConeRenderer(), new JfaRenderer()];
        foreach (var r in _renderers)
            r.Load(_gl, _instanceVbo, SiteCount, fbW, fbH);
        _active = _startTechnique;

        Console.WriteLine($"Sites: {SiteCount}. Space/Tab toggles technique, Esc quits.");
    }

    private static void OnKeyDown(IKeyboard kb, Key key, int _)
    {
        switch (key)
        {
            case Key.Escape:
                _window.Close();
                break;
            case Key.Space:
            case Key.Tab:
                _active = (_active + 1) % _renderers.Length;
                ResetFrameTimer();
                break;
            case Key.V:
                _window.VSync = !_window.VSync;
                Console.WriteLine($"VSync: {(_window.VSync ? "on" : "off")}");
                ResetFrameTimer();
                break;
        }
    }

    private static void InitSites()
    {
        var rng = new Random(1234);
        for (int i = 0; i < SiteCount; i++)
        {
            _positions[i] = new Vector2(
                (float)rng.NextDouble() * 2f - 1f,
                (float)rng.NextDouble() * 2f - 1f);

            double angle = rng.NextDouble() * Math.PI * 2;
            float speed = 0.05f + (float)rng.NextDouble() * 0.15f;
            _velocities[i] = new Vector2(
                (float)Math.Cos(angle) * speed,
                (float)Math.Sin(angle) * speed);
        }
    }

    private static unsafe void BuildInstanceBuffer()
    {
        _instanceVbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
        _gl.BufferData(BufferTargetARB.ArrayBuffer,
            (nuint)(_instanceData.Length * sizeof(float)), null, BufferUsageARB.DynamicDraw);
    }

    private static void OnResize(Vector2D<int> size)
    {
        if (size.X == 0 || size.Y == 0) return;
        foreach (var r in _renderers)
            r.Resize(size.X, size.Y);
    }

    private static void OnUpdate(double dt)
    {
        float step = (float)dt;
        for (int i = 0; i < SiteCount; i++)
        {
            Vector2 p = _positions[i] + _velocities[i] * step;

            if (p.X < -1f || p.X > 1f) { _velocities[i].X *= -1f; p.X = Math.Clamp(p.X, -1f, 1f); }
            if (p.Y < -1f || p.Y > 1f) { _velocities[i].Y *= -1f; p.Y = Math.Clamp(p.Y, -1f, 1f); }
            _positions[i] = p;

            int o = i * 2;
            _instanceData[o + 0] = p.X;
            _instanceData[o + 1] = p.Y;
        }
    }

    private static unsafe void OnRender(double dt)
    {
        // Stream updated site positions into the shared instance buffer.
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
        fixed (float* p = _instanceData)
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0,
                (nuint)(_instanceData.Length * sizeof(float)), p);

        _renderers[_active].Render();

        UpdateFrameTimer(dt);
    }

    private static void UpdateFrameTimer(double dt)
    {
        _accumTime += dt;
        _accumFrames++;
        if (_accumTime < 0.5) return;

        double msPerFrame = _accumTime / _accumFrames * 1000.0;
        double fps = _accumFrames / _accumTime;
        string line = $"Silk.NET Voronoi — {_renderers[_active].Name}  {msPerFrame:F2} ms  {fps:F0} FPS  ({SiteCount} sites)";
        _window.Title = line;
        Console.WriteLine(line);
        ResetFrameTimer();
    }

    private static void ResetFrameTimer()
    {
        _accumTime = 0;
        _accumFrames = 0;
    }

    private static void OnClosing()
    {
        foreach (var r in _renderers)
            r.Dispose();
        _gl.DeleteBuffer(_instanceVbo);
    }
}
