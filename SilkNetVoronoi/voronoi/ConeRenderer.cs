using ImGuiNET;
using Silk.NET.OpenGL;

namespace SilkNetVoronoi;

/// <summary>
/// Cone-rasterization Voronoi (Hoff et al. 1999), optimized for fill rate.
///
/// Each site is a 3D cone whose apex sits at the site; every cone shares the same slope,
/// so the depth test keeps the nearest apex per pixel — exactly the Voronoi partition.
/// Three passes per frame: (1) draw the cones instanced into an offscreen id texture
/// (RGBA8-encoded site id) with depth; (2) a fullscreen pass emits white where the id
/// differs from the right/down neighbour (a cell border); (3) white round site dots.
///
/// Performance note: the cone radius is bounded to the realistic maximum cell size rather
/// than spanning the whole screen. A radius-3 cone (the naive choice) covers ~7x the
/// viewport, so 2048 cones cost ~1.4B fragments/frame; the bounded radius below covers
/// only the area a cell can plausibly occupy, cutting overdraw ~100x. Because every cone
/// still shares one radius, interpolated depth stays proportional to distance-from-site,
/// so the partition is unchanged.
/// </summary>
internal sealed class ConeRenderer : IVoronoiRenderer
{
    private int _coneSegments = 64;   // triangle-fan slices approximating each cone's circular base; more = rounder cell edges
    private float _pointSize = 2f;    // site dot diameter, in pixels
    private float _coneRadius;         // bounded cell radius in NDC (computed from the site count)

    public string Name => "Cone";

    private GL _gl = null!;
    private uint _instanceVbo;
    private int _siteCount;
    private int _fbWidth;
    private int _fbHeight;

    private uint _coneProgram;
    private uint _edgeProgram;
    private uint _pointProgram;

    private uint _coneVao;
    private uint _pointVao;
    private uint _emptyVao;
    private uint _coneVbo;

    private uint _fbo;
    private uint _idTex;
    private uint _depthRbo;

    private int _coneVertexCount;

    public void Load(GL gl, uint instanceVbo, int siteCount, int fbWidth, int fbHeight)
    {
        _gl = gl;
        _instanceVbo = instanceVbo;
        _siteCount = siteCount;
        _fbWidth = fbWidth;
        _fbHeight = fbHeight;

        // Bound the cone radius to the realistic max cell size. Mean site spacing in the
        // 2x2 NDC square is (2 / sqrt(N)); ~6x that safely covers a cell with margin. Clamp
        // keeps it sane at extremes: never tinier than 0.05, never larger than 3.0 (the old
        // whole-screen radius) for very small N.
        _coneRadius = Math.Clamp(6f * 2f / MathF.Sqrt(siteCount), 0.05f, 3.0f);

        BuildConeMesh();
        BuildPrograms();
        BuildBuffers();
        BuildFramebuffer();
    }

    /// <summary>
    /// A unit-height cone (apex at z=0, base ring at z=1) baked into a triangle list, with
    /// the base radius bounded to <paramref name="coneRadius"/>. Since the height is fixed
    /// at 1 for every cone, all cones share the same slope and the depth test reduces to a
    /// distance comparison.
    /// </summary>
    private void BuildConeMesh()
    {
        var verts = new List<float>();
        const float height = 1.0f;   // apex at z=0, base ring at z=1; identical for every cone => shared slope

        for (int s = 0; s < _coneSegments; s++)
        {
            // The two ring angles bounding this slice (full circle = 2*PI split into _coneSegments).
            float a0 = (float)(s / (double)_coneSegments * Math.PI * 2);
            float a1 = (float)((s + 1) / (double)_coneSegments * Math.PI * 2);

            // One triangle per slice: apex at the origin + two points on the base ring.
            verts.AddRange([0f, 0f, 0f]);
            verts.AddRange([MathF.Cos(a0) * _coneRadius, MathF.Sin(a0) * _coneRadius, height]);
            verts.AddRange([MathF.Cos(a1) * _coneRadius, MathF.Sin(a1) * _coneRadius, height]);
        }

        _coneVertexCount = verts.Count / 3;
        _coneMesh = verts.ToArray();
    }

    private float[] _coneMesh = [];

    private void BuildPrograms()
    {
        _coneProgram = GlHelpers.LinkFromFiles(_gl, "cone.vert", "cone.frag");
        _edgeProgram = GlHelpers.LinkFromFiles(_gl, "fullscreen.vert", "edge.frag");
        _pointProgram = GlHelpers.LinkFromFiles(_gl, "point.vert", "point.frag");
    }

    private unsafe void BuildBuffers()
    {
        // Static cone geometry.
        _coneVbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _coneVbo);
        fixed (float* p = _coneMesh)
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(_coneMesh.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        // Cone pass VAO: location 0 = cone vertex, location 1 = site pos (per-instance).
        _coneVao = _gl.GenVertexArray();
        _gl.BindVertexArray(_coneVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _coneVbo);
        _gl.EnableVertexAttribArray(0);
        // attrib 0: cone vertex, 3 floats (x,y,z), tightly packed (stride = 3 floats).
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
        _gl.EnableVertexAttribArray(1);
        // attrib 1: site position, 2 floats (x,y), stride = 2 floats.
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        _gl.VertexAttribDivisor(1, 1);   // divisor 1 => advances once per instance (per site), not per vertex

        // Point pass VAO: location 0 = site pos (per-vertex).
        _pointVao = _gl.GenVertexArray();
        _gl.BindVertexArray(_pointVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);

        // Attribute-less VAO for the fullscreen triangle.
        _emptyVao = _gl.GenVertexArray();

        _gl.BindVertexArray(0);
    }

    private unsafe void BuildFramebuffer()
    {
        _fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

        _idTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _idTex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
            (uint)_fbWidth, (uint)_fbHeight, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _idTex, 0);

        _depthRbo = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthRbo);
        _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24,
            (uint)_fbWidth, (uint)_fbHeight);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, _depthRbo);

        if (_gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
            throw new Exception("Offscreen framebuffer incomplete");

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void Resize(int fbWidth, int fbHeight)
    {
        _fbWidth = fbWidth;
        _fbHeight = fbHeight;
        _gl.DeleteFramebuffer(_fbo);
        _gl.DeleteTexture(_idTex);
        _gl.DeleteRenderbuffer(_depthRbo);
        BuildFramebuffer();
    }

    public void Render()
    {
        // Pass 1: render the partition's site ids into the offscreen texture.
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.Viewport(0, 0, (uint)_fbWidth, (uint)_fbHeight);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        _gl.UseProgram(_coneProgram);
        _gl.BindVertexArray(_coneVao);
        _gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, (uint)_coneVertexCount, (uint)_siteCount);

        // Pass 2: edge detection to the default framebuffer.
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)_fbWidth, (uint)_fbHeight);
        _gl.Disable(EnableCap.DepthTest);
        _gl.UseProgram(_edgeProgram);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _idTex);
        _gl.Uniform1(_gl.GetUniformLocation(_edgeProgram, "uId"), 0);   // sampler reads texture unit 0
        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);   // 3 vertices = one screen-covering triangle (positions generated in the shader)

        // Pass 3: white site dots on top.
        _gl.UseProgram(_pointProgram);
        _gl.Uniform1(_gl.GetUniformLocation(_pointProgram, "uPointSize"), _pointSize);
        _gl.BindVertexArray(_pointVao);
        _gl.DrawArrays(PrimitiveType.Points, 0, (uint)_siteCount);

        _gl.BindVertexArray(0);
    }

    public void DrawUi()
    {
        ImGui.SliderFloat("Dot size (px)", ref _pointSize, 0f, 20f);
        // Changing the segment count rebuilds the cone mesh and re-uploads the static VBO.
        if (ImGui.SliderInt("Cone segments", ref _coneSegments, 3, 128))
        {
            BuildConeMesh();
            ReuploadConeMesh();
        }
    }

    private unsafe void ReuploadConeMesh()
    {
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _coneVbo);
        fixed (float* p = _coneMesh)
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(_coneMesh.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_coneVbo);
        _gl.DeleteVertexArray(_coneVao);
        _gl.DeleteVertexArray(_pointVao);
        _gl.DeleteVertexArray(_emptyVao);
        _gl.DeleteFramebuffer(_fbo);
        _gl.DeleteTexture(_idTex);
        _gl.DeleteRenderbuffer(_depthRbo);
        _gl.DeleteProgram(_coneProgram);
        _gl.DeleteProgram(_edgeProgram);
        _gl.DeleteProgram(_pointProgram);
    }
}
