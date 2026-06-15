using ImGuiNET;
using Silk.NET.OpenGL;

namespace SilkNetVoronoi;

/// <summary>
/// Jump Flooding Algorithm (Rong &amp; Tan 2006) on plain OpenGL 3.3 — fragment shaders +
/// ping-pong FBOs, no compute. The Voronoi partition is built in O(log N) fullscreen
/// passes whose cost is *independent of the site count*, so this scales to far more sites
/// than cone rasterization (and yields a distance field for free).
///
/// Each texel of an RG32F texture stores the pixel coordinates of its nearest seed
/// (sentinel (-1,-1) = none). Passes per frame:
///   1) Seed   — clear to sentinel, draw the sites as points; each writes its own pixel.
///   2) Flood  — for step = N/2, N/4, … 1, sample the 8 neighbours at ±step and keep the
///               nearest stored seed, ping-ponging between two textures.
///   3) Final  — emit white where a neighbour's seed differs (cell border) or near a site
///               (dot), black otherwise — the same black-and-white look as the cone path.
///
/// Standard JFA is approximate (a small fraction of pixels can pick a wrong nearest seed);
/// good enough for this visual. A trailing extra step=1 pass ("JFA+1") would tighten it.
/// </summary>
internal sealed class JfaRenderer : IVoronoiRenderer
{
    private float _dotRadius = 2f; // site marker radius, in pixels (drawn by the final pass)

    public string Name => "JFA";

    private GL _gl = null!;
    private uint _instanceVbo;
    private int _siteCount;
    private int _fbWidth;
    private int _fbHeight;

    private uint _seedProgram;
    private uint _floodProgram;
    private uint _finalProgram;

    private int _uFloodStep;

    private uint _pointVao;
    private uint _emptyVao;

    private uint _texA, _texB;
    private uint _fboA, _fboB;

    public void Load(GL gl, uint instanceVbo, int siteCount, int fbWidth, int fbHeight)
    {
        _gl = gl;
        _instanceVbo = instanceVbo;
        _siteCount = siteCount;
        _fbWidth = fbWidth;
        _fbHeight = fbHeight;

        BuildPrograms();
        BuildVaos();
        BuildTargets();
    }

    private void BuildPrograms()
    {
        _seedProgram = GlHelpers.LinkFromFiles(_gl, "jfa_seed.vert", "jfa_seed.frag");
        _floodProgram = GlHelpers.LinkFromFiles(_gl, "fullscreen.vert", "jfa_flood.frag");
        _finalProgram = GlHelpers.LinkFromFiles(_gl, "fullscreen.vert", "jfa_final.frag");
        _uFloodStep = _gl.GetUniformLocation(_floodProgram, "uStep");
    }

    private unsafe void BuildVaos()
    {
        // Seed pass VAO: location 0 = site pos (per-vertex) from the shared instance buffer.
        _pointVao = _gl.GenVertexArray();
        _gl.BindVertexArray(_pointVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
        _gl.EnableVertexAttribArray(0);
        // attrib 0: site position, 2 floats (x,y); drawn as GL_POINTS to seed one texel each.
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);

        // Attribute-less VAO for the fullscreen triangle used by the flood/final passes.
        _emptyVao = _gl.GenVertexArray();
        _gl.BindVertexArray(0);
    }

    private unsafe void BuildTargets()
    {
        (_texA, _fboA) = CreateTarget();
        (_texB, _fboB) = CreateTarget();
    }

    private unsafe (uint tex, uint fbo) CreateTarget()
    {
        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        // RG32F: two 32-bit floats per texel, holding the nearest seed's (x,y) pixel coordinate.
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.RG32f,
            (uint)_fbWidth, (uint)_fbHeight, 0, PixelFormat.RG, PixelType.Float, null);
        // Nearest filtering: we read exact texel values (coordinates), never interpolate them.
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        // Clamp so edge lookups during flooding don't wrap to the opposite side.
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        uint fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, tex, 0);
        if (_gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
            throw new Exception("JFA framebuffer incomplete");

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return (tex, fbo);
    }

    public void Resize(int fbWidth, int fbHeight)
    {
        _fbWidth = fbWidth;
        _fbHeight = fbHeight;
        _gl.DeleteTexture(_texA); _gl.DeleteFramebuffer(_fboA);
        _gl.DeleteTexture(_texB); _gl.DeleteFramebuffer(_fboB);
        BuildTargets();
    }

    public void Render()
    {
        _gl.Disable(EnableCap.DepthTest);
        _gl.Viewport(0, 0, (uint)_fbWidth, (uint)_fbHeight);

        // Pass 1: seed texture A with each site's own pixel, sentinel elsewhere.
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fboA);
        _gl.ClearColor(-1f, -1f, 0f, 0f);   // (-1,-1) = "no seed here" sentinel (real coords are >= 0)
        _gl.Clear(ClearBufferMask.ColorBufferBit);
        _gl.UseProgram(_seedProgram);
        _gl.BindVertexArray(_pointVao);
        _gl.DrawArrays(PrimitiveType.Points, 0, (uint)_siteCount);

        // Pass 2: jump flooding. Ping-pong A<->B at halving step sizes.
        _gl.UseProgram(_floodProgram);
        _gl.BindVertexArray(_emptyVao);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.Uniform1(_gl.GetUniformLocation(_floodProgram, "uTex"), 0);

        uint srcTex = _texA, dstFbo = _fboB, dstTex = _texB, srcFbo = _fboA;
        // First step = largest power of two below the longer side, then halve each pass down to
        // 1 (the classic JFA schedule N/2, N/4, ... 1). That's ~log2(size) passes, e.g. ~10 at 1500px.
        int startStep = 1;
        while (startStep < Math.Max(_fbWidth, _fbHeight)) startStep <<= 1;
        startStep >>= 1;

        for (int step = startStep; step >= 1; step >>= 1)
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, dstFbo);   // write into the other texture
            _gl.BindTexture(TextureTarget.Texture2D, srcTex);             // read from the current one
            _gl.Uniform1(_uFloodStep, step);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);                // one fullscreen triangle

            // Ping-pong: this pass's output becomes next pass's input.
            (srcTex, dstTex) = (dstTex, srcTex);
            (srcFbo, dstFbo) = (dstFbo, srcFbo);
        }

        // Pass 3: colour the final partition (in srcTex) to the default framebuffer.
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.UseProgram(_finalProgram);
        _gl.BindTexture(TextureTarget.Texture2D, srcTex);
        _gl.Uniform1(_gl.GetUniformLocation(_finalProgram, "uTex"), 0);
        _gl.Uniform1(_gl.GetUniformLocation(_finalProgram, "uDotRadius"), _dotRadius);
        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        _gl.BindVertexArray(0);
    }

    public void DrawUi()
    {
        ImGui.SliderFloat("Dot radius (px)", ref _dotRadius, 0f, 10f);
    }

    public void Dispose()
    {
        _gl.DeleteTexture(_texA); _gl.DeleteFramebuffer(_fboA);
        _gl.DeleteTexture(_texB); _gl.DeleteFramebuffer(_fboB);
        _gl.DeleteVertexArray(_pointVao);
        _gl.DeleteVertexArray(_emptyVao);
        _gl.DeleteProgram(_seedProgram);
        _gl.DeleteProgram(_floodProgram);
        _gl.DeleteProgram(_finalProgram);
    }
}
