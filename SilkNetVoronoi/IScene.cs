using System.Numerics;
using Silk.NET.Input;

namespace SilkNetVoronoi;

/// <summary>
/// A swappable simulation ("scene") that drives the Voronoi sites. Scenes are the second
/// axis of the sample, orthogonal to the rendering technique (<see cref="IVoronoiRenderer"/>):
/// a scene only decides where the sites are each frame, and the active renderer visualizes
/// them. Positions are in NDC <c>[-1, 1]</c> (the same space the renderers consume), so
/// scenes are resolution-independent.
/// </summary>
internal interface IScene
{
    /// <summary>Human-readable scene name, shown in the window title.</summary>
    string Name { get; }

    /// <summary>Place the initial <paramref name="siteCount"/> site positions.</summary>
    void Init(int siteCount);

    /// <summary>Advance the simulation by <paramref name="dt"/> seconds.</summary>
    void Update(double dt);

    /// <summary>Current site positions, NDC, length == <c>siteCount</c>.</summary>
    ReadOnlySpan<Vector2> Positions { get; }

    /// <summary>Scene-specific key input (e.g. the image scene's diverge toggle).</summary>
    void OnKey(Key key) { }

    /// <summary>
    /// Draw this scene's ImGui controls into the current window (called between
    /// <c>ImGui.Begin/End</c> by the host overlay). Default: no controls.
    /// </summary>
    void DrawUi() { }
}
