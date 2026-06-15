# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Three **independent** C# samples that render the *same scene* — a field of 2048 Voronoi
sites drifting randomly and bouncing off the screen edges — each with a different
modern GPU stack. All render the same **black-and-white** look: black background, white
cell borders, a small white dot at each site. The repo is a comparison/spike, not one
cohesive app. There is no top-level solution; each project has its own `.slnx` and is
built/run on its own.

| Project | Stack | Voronoi technique | Platforms |
|---|---|---|---|
| `SilkNetVoronoi` | Silk.NET + OpenGL 3.3 | Cone rasterization (depth buffer) | Win / Linux / macOS |
| `ComputeSharpVoronoi` | ComputeSharp (DX12) | Jump Flooding Algorithm (GPU compute) | **Windows only** |
| `Sdl3Voronoi` | SDL3 GPU API + SPIR-V | Cone rasterization (depth buffer) | Win / Linux / macOS |

All target .NET 9. Press **Esc** (or close the window) to quit any of them.

## Commands

Run each project from its own directory:

```sh
cd SilkNetVoronoi      && dotnet run -c Release   # most portable, no toolchain
cd ComputeSharpVoronoi && dotnet run -c Release   # Windows + DX12 GPU required
cd Sdl3Voronoi         && dotnet run -c Release   # native SDL3 + .spv bundled
```

Build-check a single project: `dotnet build -c Release` from its directory.

There are **no tests** and no lint configuration in this repo.

## The two Voronoi techniques (the core idea to understand)

All three render the same **black-and-white** look (white cell borders + white site
dots on black). Borders need to compare each pixel's owning site to its neighbours, so
every sample resolves the partition first, then detects edges.

**Cone rasterization** (`SilkNetVoronoi`, `Sdl3Voronoi`): draw one 3D cone per site
with its apex at the site position. Every cone shares the same slope, so the depth
test keeps the *nearest apex per pixel* — which is exactly the Voronoi cell. One
static cone mesh is uploaded once; a per-instance buffer of **site positions only**
(`vec2`) is refilled on the CPU and streamed to the GPU every frame, drawn with a
single instanced call. These now run **three passes per frame**: (1) cones render the
*site id* (encoded RGBA8) into an offscreen id texture with depth; (2) a fullscreen
pass emits white where the id differs from the right/down neighbour; (3) site dots are
drawn on top (round `gl_PointSize` points in Silk.NET; instanced quads in SDL3). Both
projects share this structure; they differ only in the graphics API plumbing.

**Jump Flooding Algorithm / JFA** — used in two places:

- `ComputeSharpVoronoi` (compute, DX12): see [ComputeSharpVoronoi/Program.cs](ComputeSharpVoronoi/Program.cs)
  render loop — three compute shaders per frame ([ComputeSharpVoronoi/Shaders.cs](ComputeSharpVoronoi/Shaders.cs)):
  `SeedShader` marks each site's pixel → `JumpFloodShader` ping-pongs between two `int`
  buffers at halving step sizes (N/2, N/4, … 1), each pixel keeping the nearest owning site
  → `ColorShader` emits white on owner mismatches (borders) or near each owning site (dots).
  The owner buffer is read back, blitted into a `Bitmap`, and presented.
- `SilkNetVoronoi` ([JfaRenderer.cs](SilkNetVoronoi/JfaRenderer.cs)): the same algorithm with
  **fragment shaders + ping-pong `RG32F` FBOs** (GL 3.3, no compute), storing each pixel's
  nearest-seed *coordinate*. Demonstrates JFA on the portable GL stack as the alternative to
  cones — its cost is independent of site count.

## Per-project structure notes

- **Shared CPU pattern** across all three: fixed-size `_positions`/`_velocities`
  arrays seeded with `new Random(1234)` in `InitSites`, advanced in an update step that
  bounces sites off the edges. Cone-raster projects work in NDC `[-1,1]`; ComputeSharp
  works in `[0,1]` texture space. `SiteCount` is a `const` at the top of each `Program.cs`,
  now **2048 in all three**.

- **ComputeSharpVoronoi** writes its compute shaders *in C#* as `partial struct`s
  implementing `IComputeShader`, annotated `[GeneratedComputeShaderDescriptor]` +
  `[ThreadGroupSize(...)]`; a source generator transpiles them to HLSL. A tiny WinForms
  host ([VoronoiForm.cs](ComputeSharpVoronoi/VoronoiForm.cs)) drives the render loop off
  `Application.Idle` and blits the bitmap. `UseWindowsForms=true`, targets `net9.0-windows`, x64.

- **SilkNetVoronoi** is split into a host ([Program.cs](SilkNetVoronoi/Program.cs): window,
  input, CPU sim, the shared per-instance VBO, the `Space`/`Tab` technique toggle, and a live
  ms/FPS readout in the title — **VSync is off** so the readout is meaningful) plus two
  swappable `IVoronoiRenderer` implementations: [ConeRenderer.cs](SilkNetVoronoi/ConeRenderer.cs)
  (cone rasterization, **bounded cone radius** auto-sized as `6·2/√SiteCount` — the key perf
  fix vs the old screen-spanning `radius = 3.0`) and [JfaRenderer.cs](SilkNetVoronoi/JfaRenderer.cs)
  (fragment-shader JFA). `dotnet run -- jfa` starts in JFA mode. At 2048 sites bounded cones
  (~2.7 ms) beat JFA (~3.5 ms); JFA's flat cost wins at much higher site counts (e.g. at
  8192 sites JFA ~3.7 ms < cones ~4.7 ms).
  - **Shaders live in [SilkNetVoronoi/shaders/](SilkNetVoronoi/shaders/)** as `.vert`/`.frag`
    files (not C# strings), copied to the output dir and loaded at runtime via
    `GlHelpers.LinkFromFiles` (resolved against `AppContext.BaseDirectory`). `fullscreen.vert`
    is shared by the cone edge pass and both JFA fullscreen passes. **GLSL must be ASCII** —
    non-ASCII comment chars (em dash, bullet) make the driver report a bogus `pre-mature EOF`.

- **Sdl3Voronoi** is raw P/Invoke-style SDL3 (lots of `IntPtr`, `unsafe`, manual
  command-buffer / copy-pass / render-pass lifecycle). Its **six GLSL shaders are
  embedded as strings in `Program.cs`** and compiled to SPIR-V at startup via shaderc
  (`Vortice.ShaderCompiler`) — there is no offline `glslangValidator` step and no
  committed `.spv` (the old `shaders/` folder is gone). `SDL3-CS` is bindings-only, so
  `SDL3-CS.Native` (version-matched) supplies the native SDL3 library.
  - **SDL3 SPIR-V binding rule** (easy to get wrong): a fragment sampler must be in
    descriptor **set 2** — see the edge shader's `layout(set = 2, binding = 0)`. Vertex
    uniform buffers are set 1, fragment uniform buffers set 3.
  - **No "has depth" flag** on `GPUGraphicsPipelineTargetInfo`; leaving `DepthStencilFormat`
    at its default (Invalid) means no depth, and `BeginGPURenderPass(..., IntPtr.Zero)`
    skips the depth target. The cone pass uses depth; the edge/dot passes don't.
  - `CreateGPUDevice` requests `SPIRV` (SDL's **Vulkan** backend); D3D12/Metal would need
    DXIL/MSL or `SDL_shadercross` transpilation at load time.

## Platform gotchas

- **ComputeSharpVoronoi only builds on Windows.** The source generator invokes the native
  DXC/FXC compiler via `kernel32` at build time. Off-Windows it throws
  `DllNotFoundException: kernel32`, the `IComputeShaderDescriptor` partials are never
  emitted, and you get `CS0315` errors — an artifact of the build OS, not a code defect.
  `-p:EnableWindowsTargeting=true` lets you restore/compile-check elsewhere but the shader
  generator still needs Windows to fully succeed.

- All three projects set `AllowUnsafeBlocks=true`. The cone-raster projects stream the
  instance buffer with `fixed`/pointer copies each frame; SDL3 uses `stackalloc` + `unsafe`
  heavily for its interop structs.

## Intended direction

This repo is a spike toward a larger project: additional scenes (a blood-vein flow with
static body cells; an image-to-Voronoi divergence effect). Per the top-level README, the
**JFA / ComputeSharp** approach is the most directly extensible for those, since they are
fundamentally texture-space effects.
