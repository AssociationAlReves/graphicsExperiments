using System.Numerics;
using SDL3;
using Vortice.ShaderCompiler;

namespace Sdl3Voronoi;

/// <summary>
/// Random moving Voronoi field using the SDL3 GPU API (backend-agnostic over
/// Vulkan / D3D12 / Metal), rendered black-and-white: black background, white
/// cell borders, a small white dot at each site.
///
/// Three passes per frame:
///   1) Cone pass — each site is an instanced cone whose apex sits at the site;
///      the depth buffer resolves the Voronoi partition. Each cone writes its
///      site id (encoded as an RGBA8 colour) into an offscreen id texture.
///   2) Edge pass — a fullscreen triangle samples the id texture; where the id
///      differs from the right/down neighbour we have a cell border → white.
///   3) Dot pass — instanced quads draw a round white dot at each site.
///
/// The GLSL shaders are embedded below and compiled to SPIR-V at startup with
/// shaderc (Vortice.ShaderCompiler) — no offline shader toolchain required.
/// SPIR-V targets SDL's Vulkan backend; on D3D12/Metal you would transpile with
/// SDL_shadercross. Note the SDL3 resource-binding rule for SPIR-V: a fragment
/// sampler lives in descriptor set 2 (see the edge shader's layout qualifier).
/// </summary>
internal static class Program
{
    private const int Width = 1000;
    private const int Height = 1000;
    private const int SiteCount = 2048;
    private const int ConeSegments = 64;

    private const SDL.GPUTextureFormat IdFormat = SDL.GPUTextureFormat.R8G8B8A8Unorm;

    private static readonly Vector2[] _positions = new Vector2[SiteCount];
    private static readonly Vector2[] _velocities = new Vector2[SiteCount];

    // Interleaved per-instance data: just the site position (vec2).
    private static readonly float[] _instanceData = new float[SiteCount * 2];
    private static int _coneVertexCount;

    // ----- GLSL shaders (compiled to SPIR-V at startup) -------------------

    private const string ConeVert = @"#version 450
layout(location = 0) in vec3 aConeVertex;
layout(location = 1) in vec2 aSitePos;
layout(location = 0) flat out vec3 vId;
void main()
{
    vec3 p = aConeVertex;
    p.xy += aSitePos;
    gl_Position = vec4(p.xy, p.z * 0.3, 1.0);
    int id = gl_InstanceIndex + 1;   // +1 so id 0 stays reserved for 'background'
    vId = vec3(
        float(id & 0xFF) / 255.0,
        float((id >> 8) & 0xFF) / 255.0,
        float((id >> 16) & 0xFF) / 255.0);
}";

    private const string IdFrag = @"#version 450
layout(location = 0) flat in vec3 vId;
layout(location = 0) out vec4 o;
void main() { o = vec4(vId, 1.0); }";

    private const string FullscreenVert = @"#version 450
void main()
{
    float x = -1.0 + float((gl_VertexIndex & 1) << 2);
    float y = -1.0 + float((gl_VertexIndex & 2) << 1);
    gl_Position = vec4(x, y, 0.0, 1.0);
}";

    private const string EdgeFrag = @"#version 450
layout(set = 2, binding = 0) uniform sampler2D uId;   // SDL3: fragment samplers are in set 2
layout(location = 0) out vec4 o;
void main()
{
    ivec2 sz = textureSize(uId, 0);
    ivec2 c = ivec2(gl_FragCoord.xy);
    vec3 me = texelFetch(uId, c, 0).rgb;
    vec3 r  = texelFetch(uId, ivec2(min(c.x + 1, sz.x - 1), c.y), 0).rgb;
    vec3 d  = texelFetch(uId, ivec2(c.x, min(c.y + 1, sz.y - 1)), 0).rgb;
    bool edge = (me != r) || (me != d);
    o = edge ? vec4(1.0) : vec4(0.0, 0.0, 0.0, 1.0);
}";

    private const string DotVert = @"#version 450
layout(location = 0) in vec2 aQuad;     // unit quad in [-1, 1]
layout(location = 1) in vec2 aSitePos;   // per-instance NDC centre
layout(location = 0) out vec2 vLocal;
void main()
{
    const float HALF = 0.005;            // ~2.5 px at 1000 px wide
    vLocal = aQuad;
    gl_Position = vec4(aSitePos + aQuad * HALF, 0.0, 1.0);
}";

    private const string DotFrag = @"#version 450
layout(location = 0) in vec2 vLocal;
layout(location = 0) out vec4 o;
void main()
{
    if (dot(vLocal, vLocal) > 1.0) discard;   // clip the quad to a disc
    o = vec4(1.0);
}";

    private static unsafe void Main()
    {
        if (!SDL.Init(SDL.InitFlags.Video))
            throw new Exception("SDL_Init failed: " + SDL.GetError());

        IntPtr window = SDL.CreateWindow(
            "SDL3 GPU — Voronoi Moving Field (B&W)", Width, Height, 0);
        if (window == IntPtr.Zero)
            throw new Exception("CreateWindow failed: " + SDL.GetError());

        IntPtr device = SDL.CreateGPUDevice(SDL.GPUShaderFormat.SPIRV, true, null!);
        if (device == IntPtr.Zero)
            throw new Exception("CreateGPUDevice failed: " + SDL.GetError());

        if (!SDL.ClaimWindowForGPUDevice(device, window))
            throw new Exception("ClaimWindowForGPUDevice failed: " + SDL.GetError());

        InitSites();
        float[] coneMesh = BuildConeMesh();
        float[] quadMesh =
        {
            -1f, -1f,  1f, -1f,  1f, 1f,
            -1f, -1f,  1f,  1f, -1f, 1f,
        };

        // Compile and create the six shaders.
        IntPtr coneVs = CreateShader(device, Spirv(ConeVert, ShaderKind.VertexShader, "cone.vert"), SDL.GPUShaderStage.Vertex, 0);
        IntPtr idFs = CreateShader(device, Spirv(IdFrag, ShaderKind.FragmentShader, "id.frag"), SDL.GPUShaderStage.Fragment, 0);
        IntPtr fsVs = CreateShader(device, Spirv(FullscreenVert, ShaderKind.VertexShader, "fs.vert"), SDL.GPUShaderStage.Vertex, 0);
        IntPtr edgeFs = CreateShader(device, Spirv(EdgeFrag, ShaderKind.FragmentShader, "edge.frag"), SDL.GPUShaderStage.Fragment, 1);
        IntPtr dotVs = CreateShader(device, Spirv(DotVert, ShaderKind.VertexShader, "dot.vert"), SDL.GPUShaderStage.Vertex, 0);
        IntPtr dotFs = CreateShader(device, Spirv(DotFrag, ShaderKind.FragmentShader, "dot.frag"), SDL.GPUShaderStage.Fragment, 0);

        SDL.GPUTextureFormat swapFormat = SDL.GetGPUSwapchainTextureFormat(device, window);
        IntPtr conePipeline = CreateConePipeline(device, coneVs, idFs);
        IntPtr edgePipeline = CreateEdgePipeline(device, fsVs, edgeFs, swapFormat);
        IntPtr dotPipeline = CreateDotPipeline(device, dotVs, dotFs, swapFormat);

        foreach (IntPtr sh in new[] { coneVs, idFs, fsVs, edgeFs, dotVs, dotFs })
            SDL.ReleaseGPUShader(device, sh);

        // Static geometry.
        uint coneBytes = (uint)(coneMesh.Length * sizeof(float));
        IntPtr coneBuffer = CreateBuffer(device, SDL.GPUBufferUsageFlags.Vertex, coneBytes);
        UploadStatic(device, coneBuffer, coneMesh, coneBytes);

        uint quadBytes = (uint)(quadMesh.Length * sizeof(float));
        IntPtr quadBuffer = CreateBuffer(device, SDL.GPUBufferUsageFlags.Vertex, quadBytes);
        UploadStatic(device, quadBuffer, quadMesh, quadBytes);

        // Dynamic per-instance buffer (re-uploaded each frame).
        uint instBytes = (uint)(_instanceData.Length * sizeof(float));
        IntPtr instanceBuffer = CreateBuffer(device, SDL.GPUBufferUsageFlags.Vertex, instBytes);
        IntPtr instanceTransfer = CreateTransferBuffer(device, instBytes);

        IntPtr idTexture = CreateIdTexture(device);
        IntPtr depthTexture = CreateDepthTexture(device);
        IntPtr sampler = CreateSampler(device);

        ulong last = SDL.GetPerformanceCounter();
        double freq = SDL.GetPerformanceFrequency();
        bool running = true;

        while (running)
        {
            while (SDL.PollEvent(out SDL.Event e))
            {
                if (e.Type == (uint)SDL.EventType.Quit) running = false;
                if (e.Type == (uint)SDL.EventType.KeyDown && e.Key.Key == SDL.Keycode.Escape)
                    running = false;
            }

            ulong now = SDL.GetPerformanceCounter();
            float dt = (float)((now - last) / freq);
            last = now;

            UpdateSites(dt);
            UploadInstances(device, instanceTransfer, instanceBuffer, instBytes);

            RenderFrame(device, window, conePipeline, edgePipeline, dotPipeline,
                coneBuffer, quadBuffer, instanceBuffer, idTexture, depthTexture, sampler);
        }

        SDL.ReleaseGPUSampler(device, sampler);
        SDL.ReleaseGPUTexture(device, depthTexture);
        SDL.ReleaseGPUTexture(device, idTexture);
        SDL.ReleaseGPUTransferBuffer(device, instanceTransfer);
        SDL.ReleaseGPUBuffer(device, instanceBuffer);
        SDL.ReleaseGPUBuffer(device, quadBuffer);
        SDL.ReleaseGPUBuffer(device, coneBuffer);
        SDL.ReleaseGPUGraphicsPipeline(device, dotPipeline);
        SDL.ReleaseGPUGraphicsPipeline(device, edgePipeline);
        SDL.ReleaseGPUGraphicsPipeline(device, conePipeline);
        SDL.DestroyGPUDevice(device);
        SDL.DestroyWindow(window);
        SDL.Quit();
    }

    // ----- shader compilation --------------------------------------------

    private static byte[] Spirv(string source, ShaderKind kind, string name)
    {
        using var compiler = new Compiler();
        CompileResult result = compiler.Compile(source, name, new CompilerOptions { ShaderStage = kind });
        if (result.Status != CompilationStatus.Success || result.Bytecode is null)
            throw new Exception($"Shader '{name}' failed to compile: {result.ErrorMessage}");
        return result.Bytecode;
    }

    private static unsafe IntPtr CreateShader(
        IntPtr device, byte[] code, SDL.GPUShaderStage stage, uint numSamplers)
    {
        byte[] entry = "main\0"u8.ToArray();
        fixed (byte* codePtr = code)
        fixed (byte* entryPtr = entry)
        {
            var info = new SDL.GPUShaderCreateInfo
            {
                CodeSize = (UIntPtr)code.Length,
                Code = (IntPtr)codePtr,
                _entrypoint = (IntPtr)entryPtr,
                Format = SDL.GPUShaderFormat.SPIRV,
                Stage = stage,
                NumSamplers = numSamplers,
                NumStorageTextures = 0,
                NumStorageBuffers = 0,
                NumUniformBuffers = 0,
            };
            IntPtr shader = SDL.CreateGPUShader(device, ref info);
            if (shader == IntPtr.Zero)
                throw new Exception("CreateGPUShader failed: " + SDL.GetError());
            return shader;
        }
    }

    // ----- pipelines ------------------------------------------------------

    private static unsafe IntPtr CreateConePipeline(IntPtr device, IntPtr vs, IntPtr fs)
    {
        var bufs = stackalloc SDL.GPUVertexBufferDescription[2];
        bufs[0] = new SDL.GPUVertexBufferDescription
        { Slot = 0, Pitch = (uint)(3 * sizeof(float)), InputRate = SDL.GPUVertexInputRate.Vertex };
        bufs[1] = new SDL.GPUVertexBufferDescription
        { Slot = 1, Pitch = (uint)(2 * sizeof(float)), InputRate = SDL.GPUVertexInputRate.Instance };

        var attrs = stackalloc SDL.GPUVertexAttribute[2];
        attrs[0] = new SDL.GPUVertexAttribute
        { Location = 0, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Float3, Offset = 0 };
        attrs[1] = new SDL.GPUVertexAttribute
        { Location = 1, BufferSlot = 1, Format = SDL.GPUVertexElementFormat.Float2, Offset = 0 };

        var color = new SDL.GPUColorTargetDescription { Format = IdFormat, BlendState = default };

        var info = new SDL.GPUGraphicsPipelineCreateInfo
        {
            VertexShader = vs,
            FragmentShader = fs,
            PrimitiveType = SDL.GPUPrimitiveType.TriangleList,
            VertexInputState = new SDL.GPUVertexInputState
            {
                VertexBufferDescriptions = (IntPtr)bufs,
                NumVertexBuffers = 2,
                VertexAttributes = (IntPtr)attrs,
                NumVertexAttributes = 2,
            },
            RasterizerState = new SDL.GPURasterizerState
            {
                FillMode = SDL.GPUFillMode.Fill,
                CullMode = SDL.GPUCullMode.None,
                FrontFace = SDL.GPUFrontFace.CounterClockwise,
            },
            DepthStencilState = new SDL.GPUDepthStencilState
            {
                EnableDepthTest = true,
                EnableDepthWrite = true,
                CompareOp = SDL.GPUCompareOp.Less,
            },
            TargetInfo = new SDL.GPUGraphicsPipelineTargetInfo
            {
                ColorTargetDescriptions = (IntPtr)(&color),
                NumColorTargets = 1,
                DepthStencilFormat = SDL.GPUTextureFormat.D32Float,
            },
        };
        return CreatePipeline(device, ref info);
    }

    private static unsafe IntPtr CreateEdgePipeline(
        IntPtr device, IntPtr vs, IntPtr fs, SDL.GPUTextureFormat swapFormat)
    {
        var color = new SDL.GPUColorTargetDescription { Format = swapFormat, BlendState = default };

        // No vertex input (the fullscreen triangle is generated from gl_VertexIndex),
        // no depth target (DepthStencilFormat left as the default 'Invalid').
        var info = new SDL.GPUGraphicsPipelineCreateInfo
        {
            VertexShader = vs,
            FragmentShader = fs,
            PrimitiveType = SDL.GPUPrimitiveType.TriangleList,
            VertexInputState = default,
            RasterizerState = new SDL.GPURasterizerState
            {
                FillMode = SDL.GPUFillMode.Fill,
                CullMode = SDL.GPUCullMode.None,
                FrontFace = SDL.GPUFrontFace.CounterClockwise,
            },
            DepthStencilState = default,
            TargetInfo = new SDL.GPUGraphicsPipelineTargetInfo
            {
                ColorTargetDescriptions = (IntPtr)(&color),
                NumColorTargets = 1,
            },
        };
        return CreatePipeline(device, ref info);
    }

    private static unsafe IntPtr CreateDotPipeline(
        IntPtr device, IntPtr vs, IntPtr fs, SDL.GPUTextureFormat swapFormat)
    {
        var bufs = stackalloc SDL.GPUVertexBufferDescription[2];
        bufs[0] = new SDL.GPUVertexBufferDescription
        { Slot = 0, Pitch = (uint)(2 * sizeof(float)), InputRate = SDL.GPUVertexInputRate.Vertex };
        bufs[1] = new SDL.GPUVertexBufferDescription
        { Slot = 1, Pitch = (uint)(2 * sizeof(float)), InputRate = SDL.GPUVertexInputRate.Instance };

        var attrs = stackalloc SDL.GPUVertexAttribute[2];
        attrs[0] = new SDL.GPUVertexAttribute
        { Location = 0, BufferSlot = 0, Format = SDL.GPUVertexElementFormat.Float2, Offset = 0 };
        attrs[1] = new SDL.GPUVertexAttribute
        { Location = 1, BufferSlot = 1, Format = SDL.GPUVertexElementFormat.Float2, Offset = 0 };

        var color = new SDL.GPUColorTargetDescription { Format = swapFormat, BlendState = default };

        var info = new SDL.GPUGraphicsPipelineCreateInfo
        {
            VertexShader = vs,
            FragmentShader = fs,
            PrimitiveType = SDL.GPUPrimitiveType.TriangleList,
            VertexInputState = new SDL.GPUVertexInputState
            {
                VertexBufferDescriptions = (IntPtr)bufs,
                NumVertexBuffers = 2,
                VertexAttributes = (IntPtr)attrs,
                NumVertexAttributes = 2,
            },
            RasterizerState = new SDL.GPURasterizerState
            {
                FillMode = SDL.GPUFillMode.Fill,
                CullMode = SDL.GPUCullMode.None,
                FrontFace = SDL.GPUFrontFace.CounterClockwise,
            },
            DepthStencilState = default,
            TargetInfo = new SDL.GPUGraphicsPipelineTargetInfo
            {
                ColorTargetDescriptions = (IntPtr)(&color),
                NumColorTargets = 1,
            },
        };
        return CreatePipeline(device, ref info);
    }

    private static IntPtr CreatePipeline(IntPtr device, ref SDL.GPUGraphicsPipelineCreateInfo info)
    {
        IntPtr pipeline = SDL.CreateGPUGraphicsPipeline(device, ref info);
        if (pipeline == IntPtr.Zero)
            throw new Exception("CreateGPUGraphicsPipeline failed: " + SDL.GetError());
        return pipeline;
    }

    // ----- resources ------------------------------------------------------

    private static IntPtr CreateBuffer(IntPtr device, SDL.GPUBufferUsageFlags usage, uint size)
    {
        var info = new SDL.GPUBufferCreateInfo { Usage = usage, Size = size };
        IntPtr buf = SDL.CreateGPUBuffer(device, ref info);
        if (buf == IntPtr.Zero) throw new Exception("CreateGPUBuffer failed: " + SDL.GetError());
        return buf;
    }

    private static IntPtr CreateTransferBuffer(IntPtr device, uint size)
    {
        var info = new SDL.GPUTransferBufferCreateInfo
        { Usage = SDL.GPUTransferBufferUsage.Upload, Size = size };
        IntPtr tb = SDL.CreateGPUTransferBuffer(device, ref info);
        if (tb == IntPtr.Zero) throw new Exception("CreateGPUTransferBuffer failed: " + SDL.GetError());
        return tb;
    }

    private static IntPtr CreateIdTexture(IntPtr device)
    {
        var info = new SDL.GPUTextureCreateInfo
        {
            Type = SDL.GPUTextureType.TextureType2D,
            Format = IdFormat,
            Usage = SDL.GPUTextureUsageFlags.ColorTarget | SDL.GPUTextureUsageFlags.Sampler,
            Width = Width,
            Height = Height,
            LayerCountOrDepth = 1,
            NumLevels = 1,
            SampleCount = SDL.GPUSampleCount.SampleCount1,
        };
        IntPtr tex = SDL.CreateGPUTexture(device, ref info);
        if (tex == IntPtr.Zero) throw new Exception("CreateGPUTexture(id) failed: " + SDL.GetError());
        return tex;
    }

    private static IntPtr CreateDepthTexture(IntPtr device)
    {
        var info = new SDL.GPUTextureCreateInfo
        {
            Type = SDL.GPUTextureType.TextureType2D,
            Format = SDL.GPUTextureFormat.D32Float,
            Usage = SDL.GPUTextureUsageFlags.DepthStencilTarget,
            Width = Width,
            Height = Height,
            LayerCountOrDepth = 1,
            NumLevels = 1,
            SampleCount = SDL.GPUSampleCount.SampleCount1,
        };
        IntPtr tex = SDL.CreateGPUTexture(device, ref info);
        if (tex == IntPtr.Zero) throw new Exception("CreateGPUTexture(depth) failed: " + SDL.GetError());
        return tex;
    }

    private static IntPtr CreateSampler(IntPtr device)
    {
        var info = new SDL.GPUSamplerCreateInfo
        {
            MinFilter = SDL.GPUFilter.Nearest,
            MagFilter = SDL.GPUFilter.Nearest,
            MipmapMode = SDL.GPUSamplerMipmapMode.Nearest,
            AddressModeU = SDL.GPUSamplerAddressMode.ClampToEdge,
            AddressModeV = SDL.GPUSamplerAddressMode.ClampToEdge,
            AddressModeW = SDL.GPUSamplerAddressMode.ClampToEdge,
        };
        IntPtr s = SDL.CreateGPUSampler(device, ref info);
        if (s == IntPtr.Zero) throw new Exception("CreateGPUSampler failed: " + SDL.GetError());
        return s;
    }

    // ----- simulation -----------------------------------------------------

    private static void InitSites()
    {
        var rng = new Random(1234);
        for (int i = 0; i < SiteCount; i++)
        {
            _positions[i] = new Vector2(
                (float)rng.NextDouble() * 2f - 1f,
                (float)rng.NextDouble() * 2f - 1f);
            double a = rng.NextDouble() * Math.PI * 2;
            float speed = 0.05f + (float)rng.NextDouble() * 0.15f;
            _velocities[i] = new Vector2((float)Math.Cos(a) * speed, (float)Math.Sin(a) * speed);
        }
    }

    private static float[] BuildConeMesh()
    {
        var verts = new List<float>();
        const float radius = 3.0f;
        const float height = 3.0f;
        for (int s = 0; s < ConeSegments; s++)
        {
            float a0 = (float)(s / (double)ConeSegments * Math.PI * 2);
            float a1 = (float)((s + 1) / (double)ConeSegments * Math.PI * 2);
            verts.AddRange([0f, 0f, 0f]);
            verts.AddRange([MathF.Cos(a0) * radius, MathF.Sin(a0) * radius, height]);
            verts.AddRange([MathF.Cos(a1) * radius, MathF.Sin(a1) * radius, height]);
        }
        _coneVertexCount = verts.Count / 3;
        return verts.ToArray();
    }

    private static void UpdateSites(float dt)
    {
        for (int i = 0; i < SiteCount; i++)
        {
            Vector2 p = _positions[i] + _velocities[i] * dt;
            if (p.X < -1f || p.X > 1f) { _velocities[i].X *= -1f; p.X = Math.Clamp(p.X, -1f, 1f); }
            if (p.Y < -1f || p.Y > 1f) { _velocities[i].Y *= -1f; p.Y = Math.Clamp(p.Y, -1f, 1f); }
            _positions[i] = p;
            int o = i * 2;
            _instanceData[o + 0] = p.X;
            _instanceData[o + 1] = p.Y;
        }
    }

    // ----- per-frame uploads / draw --------------------------------------

    private static unsafe void UploadStatic(IntPtr device, IntPtr buffer, float[] data, uint bytes)
    {
        IntPtr transfer = CreateTransferBuffer(device, bytes);
        IntPtr map = SDL.MapGPUTransferBuffer(device, transfer, false);
        fixed (float* src = data)
            Buffer.MemoryCopy(src, (void*)map, bytes, bytes);
        SDL.UnmapGPUTransferBuffer(device, transfer);

        IntPtr cmd = SDL.AcquireGPUCommandBuffer(device);
        IntPtr copyPass = SDL.BeginGPUCopyPass(cmd);
        var loc = new SDL.GPUTransferBufferLocation { TransferBuffer = transfer, Offset = 0 };
        var region = new SDL.GPUBufferRegion { Buffer = buffer, Offset = 0, Size = bytes };
        SDL.UploadToGPUBuffer(copyPass, ref loc, ref region, false);
        SDL.EndGPUCopyPass(copyPass);
        SDL.SubmitGPUCommandBuffer(cmd);
        SDL.ReleaseGPUTransferBuffer(device, transfer);
    }

    private static unsafe void UploadInstances(
        IntPtr device, IntPtr transfer, IntPtr buffer, uint bytes)
    {
        IntPtr map = SDL.MapGPUTransferBuffer(device, transfer, true);
        fixed (float* src = _instanceData)
            Buffer.MemoryCopy(src, (void*)map, bytes, bytes);
        SDL.UnmapGPUTransferBuffer(device, transfer);

        IntPtr cmd = SDL.AcquireGPUCommandBuffer(device);
        IntPtr copyPass = SDL.BeginGPUCopyPass(cmd);
        var loc = new SDL.GPUTransferBufferLocation { TransferBuffer = transfer, Offset = 0 };
        var region = new SDL.GPUBufferRegion { Buffer = buffer, Offset = 0, Size = bytes };
        SDL.UploadToGPUBuffer(copyPass, ref loc, ref region, true);
        SDL.EndGPUCopyPass(copyPass);
        SDL.SubmitGPUCommandBuffer(cmd);
    }

    private static unsafe void RenderFrame(
        IntPtr device, IntPtr window,
        IntPtr conePipeline, IntPtr edgePipeline, IntPtr dotPipeline,
        IntPtr coneBuffer, IntPtr quadBuffer, IntPtr instanceBuffer,
        IntPtr idTexture, IntPtr depthTexture, IntPtr sampler)
    {
        IntPtr cmd = SDL.AcquireGPUCommandBuffer(device);
        if (cmd == IntPtr.Zero) return;

        if (!SDL.AcquireGPUSwapchainTexture(cmd, window, out IntPtr swapchain, out _, out _)
            || swapchain == IntPtr.Zero)
        {
            SDL.SubmitGPUCommandBuffer(cmd);
            return;
        }

        // --- Pass 1: cones -> id texture (with depth) ---
        var idTarget = new SDL.GPUColorTargetInfo
        {
            Texture = idTexture,
            ClearColor = new SDL.FColor { R = 0, G = 0, B = 0, A = 0 },
            LoadOp = SDL.GPULoadOp.Clear,
            StoreOp = SDL.GPUStoreOp.Store,
        };
        var depthTarget = new SDL.GPUDepthStencilTargetInfo
        {
            Texture = depthTexture,
            ClearDepth = 1.0f,
            LoadOp = SDL.GPULoadOp.Clear,
            StoreOp = SDL.GPUStoreOp.DontCare,
            StencilLoadOp = SDL.GPULoadOp.DontCare,
            StencilStoreOp = SDL.GPUStoreOp.DontCare,
            Cycle = 1,
        };

        var idTargets = new[] { idTarget };
        IntPtr conePass = SDL.BeginGPURenderPass(cmd, idTargets, 1, ref depthTarget);
        SDL.BindGPUGraphicsPipeline(conePass, conePipeline);
        var coneBindings = stackalloc SDL.GPUBufferBinding[2];
        coneBindings[0] = new SDL.GPUBufferBinding { Buffer = coneBuffer, Offset = 0 };
        coneBindings[1] = new SDL.GPUBufferBinding { Buffer = instanceBuffer, Offset = 0 };
        SDL.BindGPUVertexBuffers(conePass, 0, (IntPtr)coneBindings, 2);
        SDL.DrawGPUPrimitives(conePass, (uint)_coneVertexCount, SiteCount, 0, 0);
        SDL.EndGPURenderPass(conePass);

        // --- Pass 2 + 3: edge detection then site dots -> swapchain ---
        var screenTarget = new SDL.GPUColorTargetInfo
        {
            Texture = swapchain,
            ClearColor = new SDL.FColor { R = 0, G = 0, B = 0, A = 1 },
            LoadOp = SDL.GPULoadOp.Clear,
            StoreOp = SDL.GPUStoreOp.Store,
        };
        var screenTargets = new[] { screenTarget };
        IntPtr pass = SDL.BeginGPURenderPass(cmd, screenTargets, 1, IntPtr.Zero);

        // Edge fullscreen pass.
        SDL.BindGPUGraphicsPipeline(pass, edgePipeline);
        var samplerBindings = new[]
        {
            new SDL.GPUTextureSamplerBinding { Texture = idTexture, Sampler = sampler },
        };
        SDL.BindGPUFragmentSamplers(pass, 0, samplerBindings, 1);
        SDL.DrawGPUPrimitives(pass, 3, 1, 0, 0);

        // Site dots.
        SDL.BindGPUGraphicsPipeline(pass, dotPipeline);
        var dotBindings = stackalloc SDL.GPUBufferBinding[2];
        dotBindings[0] = new SDL.GPUBufferBinding { Buffer = quadBuffer, Offset = 0 };
        dotBindings[1] = new SDL.GPUBufferBinding { Buffer = instanceBuffer, Offset = 0 };
        SDL.BindGPUVertexBuffers(pass, 0, (IntPtr)dotBindings, 2);
        SDL.DrawGPUPrimitives(pass, 6, SiteCount, 0, 0);

        SDL.EndGPURenderPass(pass);
        SDL.SubmitGPUCommandBuffer(cmd);
    }
}
