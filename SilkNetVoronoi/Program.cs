using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;

namespace SilkNetVoronoi;

/// <summary>
/// Animated Voronoi sites on plain OpenGL 3.3 via Silk.NET, rendered black-and-white
/// (black background, white cell borders, a white dot at each site).
///
/// The sample has two independent axes, both switchable at runtime:
///   • Scene (the site simulation) — F1/F2/F3 select <see cref="RandomFieldScene"/>,
///     <see cref="BloodVeinScene"/>, <see cref="ImageDivergeScene"/>.
///   • Technique (how the Voronoi is drawn) — Space/Tab toggle <see cref="ConeRenderer"/>
///     and <see cref="JfaRenderer"/>.
/// This host owns the window, input, the shared per-instance position buffer and the live
/// ms/FPS readout; scenes and renderers are orthogonal (a renderer just visualizes whatever
/// NDC positions the active scene produces).
/// </summary>
internal static class Program
{
    // Number of Voronoi sites/cells. Shared by every scene and renderer (the instance buffer
    // and the renderers' resources are sized for this at load). Higher = finer diagram but
    // more work; it also drives the cone renderer's auto cell-radius (see ConeRenderer).
    private const int SiteCount = 8192;

    private static IWindow _window = null!;
    private static GL _gl = null!;
    private static IInputContext _input = null!;
    private static ImGuiController _imgui = null!;

    private static uint _instanceVbo;

    private static IVoronoiRenderer[] _renderers = [];
    private static int _activeRenderer;

    private static IScene[] _scenes = [];
    private static int _activeScene;

    // Global animation-speed multiplier applied to every scene's timestep (+/- to change).
    private static double _speed = 1.0;

    // Frame-time readout.
    private static double _accumTime;
    private static int _accumFrames;

    // Startup overrides from CLI args. Indices match the arrays built in OnLoad:
    // technique 0 = Cone, 1 = JFA; scene 0 = Random, 1 = Vein, 2 = Image.
    private static int _startTechnique;
    private static int _startScene;
    // Default picture for the image scene (next to the binary; overridable by a CLI path arg).
    private static string? _imagePath = Path.Combine(AppContext.BaseDirectory, "assets", "DSC04325.JPG");

    private static void Main(string[] args)
    {
        ParseArgs(args);

        var options = WindowOptions.Default with
        {
            Size = new Vector2D<int>(1500, 1500),   // initial window size in pixels (square)
            Title = "Silk.NET — Voronoi (B&W)",
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

    private static void ParseArgs(string[] args)
    {
        foreach (string a in args)
        {
            switch (a.ToLowerInvariant())
            {
                case "cone": _startTechnique = 0; break;
                case "jfa": _startTechnique = 1; break;
                case "f1": case "random": _startScene = 0; break;
                case "f2": case "vein": _startScene = 1; break;
                case "f3": case "image": _startScene = 2; break;
                case "f4": case "tension": _startScene = 3; break;
                default: _imagePath = a; break;   // anything else is treated as an image path
            }
        }
    }

    private static void OnLoad()
    {
        _input = _window.CreateInput();
        foreach (var kb in _input.Keyboards)
            kb.KeyDown += OnKeyDown;

        // VSync starts off so the ms/FPS readout reflects real GPU cost (this sample exists
        // to compare techniques); the frame rate is otherwise uncapped. Toggle with the V key.
        _window.VSync = false;

        _gl = GL.GetApi(_window);
        _gl.ClearColor(0f, 0f, 0f, 1f);
        _gl.Enable(EnableCap.ProgramPointSize);

        int fbW = _window.FramebufferSize.X;
        int fbH = _window.FramebufferSize.Y;

        BuildInstanceBuffer();

        _scenes = [new RandomFieldScene(), new BloodVeinScene(), new ImageDivergeScene(_imagePath), new TensionFieldScene()];
        foreach (var s in _scenes)
            s.Init(SiteCount);
        _activeScene = _startScene;

        _renderers = [new ConeRenderer(), new JfaRenderer()];
        foreach (var r in _renderers)
            r.Load(_gl, _instanceVbo, SiteCount, fbW, fbH);
        _activeRenderer = _startTechnique;

        // ImGui overlay for tweaking parameters at runtime; uses the same GL + input context.
        _imgui = new ImGuiController(_gl, _window, _input);

        Console.WriteLine($"Sites: {SiteCount}. F1/F2/F3/F4 = scene, Space = technique, " +
                          "+/- = speed, V = vsync, D = image scatter/assemble, Esc = quit.");
    }

    private static void OnKeyDown(IKeyboard kb, Key key, int _)
    {
        // F1..Fn select a scene. Key.F1..F12 are contiguous in the enum, so subtracting F1
        // gives the scene index; this auto-scales as scenes are added (F4 -> index 3, ...).
        if (key >= Key.F1 && key <= Key.F12)
        {
            int i = key - Key.F1;
            if (i < _scenes.Length) { _activeScene = i; ResetFrameTimer(); }
            return;
        }

        switch (key)
        {
            case Key.Escape:
                _window.Close();
                break;
            case Key.Space:
            case Key.Tab:
                // Cycle to the next technique, wrapping around (Cone -> JFA -> Cone).
                _activeRenderer = (_activeRenderer + 1) % _renderers.Length;
                ResetFrameTimer();
                break;
            case Key.V:
                _window.VSync = !_window.VSync;
                Console.WriteLine($"VSync: {(_window.VSync ? "on" : "off")}");
                ResetFrameTimer();
                break;
            // Speed control. "+" is the unshifted '=' key on the main row; also accept the keypad.
            case Key.Equal:
            case Key.KeypadAdd:
                _speed = Math.Min(16.0, _speed * 1.25);   // step up 25%, capped at 16x
                Console.WriteLine($"Speed: {_speed:F2}x");
                break;
            case Key.Minus:
            case Key.KeypadSubtract:
                _speed = Math.Max(0.1, _speed / 1.25);    // step down 25%, floored at 0.1x
                Console.WriteLine($"Speed: {_speed:F2}x");
                break;
            default:
                _scenes[_activeScene].OnKey(key);   // scene-specific input (e.g. D in image scene)
                break;
        }
    }

    private static unsafe void BuildInstanceBuffer()
    {
        _instanceVbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
        _gl.BufferData(BufferTargetARB.ArrayBuffer,
            (nuint)(SiteCount * sizeof(Vector2)), null, BufferUsageARB.DynamicDraw);
    }

    private static void OnResize(Vector2D<int> size)
    {
        if (size.X == 0 || size.Y == 0) return;
        foreach (var r in _renderers)
            r.Resize(size.X, size.Y);
    }

    private static void OnUpdate(double dt)
    {
        _scenes[_activeScene].Update(dt * _speed);   // scale the timestep by the speed multiplier
    }

    private static unsafe void OnRender(double dt)
    {
        // Stream the active scene's site positions into the shared instance buffer. Vector2
        // is two contiguous floats, so the span uploads directly with no intermediate copy.
        ReadOnlySpan<Vector2> positions = _scenes[_activeScene].Positions;
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
        fixed (Vector2* p = positions)
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0,
                (nuint)(positions.Length * sizeof(Vector2)), p);

        _renderers[_activeRenderer].Render();

        DrawOverlay(dt);
        UpdateFrameTimer(dt);
    }

    /// <summary>Dear ImGui overlay, drawn on top of the scene, for live parameter tweaking.</summary>
    private static void DrawOverlay(double dt)
    {
        _imgui.Update((float)dt);

        ImGui.Begin("Tweaks");
        ImGui.Text($"{ImGui.GetIO().Framerate:F0} FPS   ({SiteCount} sites)");
        ImGui.Separator();

        // Global controls.
        float speed = (float)_speed;
        if (ImGui.SliderFloat("Speed", ref speed, 0.1f, 16f)) _speed = speed;
        bool vsync = _window.VSync;
        if (ImGui.Checkbox("VSync", ref vsync)) _window.VSync = vsync;

        // The active scene's own controls.
        ImGui.SeparatorText($"Scene: {_scenes[_activeScene].Name}");
        _scenes[_activeScene].DrawUi();

        // The active renderer's own controls.
        ImGui.SeparatorText($"Technique: {_renderers[_activeRenderer].Name}");
        _renderers[_activeRenderer].DrawUi();
        ImGui.End();

        _imgui.Render();   // issues the overlay's GL draw calls on top of the scene
    }

    private static void UpdateFrameTimer(double dt)
    {
        _accumTime += dt;
        _accumFrames++;
        if (_accumTime < 0.5) return;   // refresh the readout twice a second (averages out jitter)

        double msPerFrame = _accumTime / _accumFrames * 1000.0;   // seconds/frame -> ms/frame
        double fps = _accumFrames / _accumTime;
        string line = $"Silk.NET Voronoi — {_scenes[_activeScene].Name} | {_renderers[_activeRenderer].Name}" +
                      $"  {msPerFrame:F2} ms  {fps:F0} FPS  {_speed:F2}x  ({SiteCount} sites)";
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
        _imgui?.Dispose();
        foreach (var r in _renderers)
            r.Dispose();
        _gl.DeleteBuffer(_instanceVbo);
    }
}
