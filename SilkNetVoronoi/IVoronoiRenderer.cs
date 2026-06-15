using Silk.NET.OpenGL;

namespace SilkNetVoronoi;

/// <summary>
/// A swappable Voronoi rendering technique. The host (<see cref="Program"/>) owns the
/// window, the site simulation and the shared per-instance position buffer; each renderer
/// turns those site positions into the black-and-white frame (white cell borders + white
/// site dots on black) using its own GPU technique.
/// </summary>
internal interface IVoronoiRenderer
{
    /// <summary>Human-readable technique name, shown in the window title.</summary>
    string Name { get; }

    /// <summary>
    /// One-time GPU setup. <paramref name="instanceVbo"/> is the shared buffer of
    /// <c>vec2</c> site positions (NDC), refilled by the host every frame.
    /// </summary>
    void Load(GL gl, uint instanceVbo, int siteCount, int fbWidth, int fbHeight);

    /// <summary>Recreate any framebuffer-sized resources for a new viewport.</summary>
    void Resize(int fbWidth, int fbHeight);

    /// <summary>Draw one frame to the default framebuffer.</summary>
    void Render();

    void Dispose();
}
