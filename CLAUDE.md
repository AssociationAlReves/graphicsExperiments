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

- **Shared CPU pattern**: a fixed-size site array advanced each frame and uploaded as the
  per-instance/seed positions. ComputeSharp and SDL3 keep this inline in `Program.cs` (random
  drift + edge bounce, seeded `new Random(1234)`). **SilkNetVoronoi has generalized it into
  swappable `IScene`s** (see below) which use `Random.Shared` (non-deterministic per run).
  Cone-raster projects work in NDC `[-1,1]`; ComputeSharp works in `[0,1]` texture space.
  `SiteCount` is a `const` per `Program.cs` (ComputeSharp/SDL3 2048; Silk.NET 8192).

- **ComputeSharpVoronoi** writes its compute shaders *in C#* as `partial struct`s
  implementing `IComputeShader`, annotated `[GeneratedComputeShaderDescriptor]` +
  `[ThreadGroupSize(...)]`; a source generator transpiles them to HLSL. A tiny WinForms
  host ([VoronoiForm.cs](ComputeSharpVoronoi/VoronoiForm.cs)) drives the render loop off
  `Application.Idle` and blits the bitmap. `UseWindowsForms=true`, targets `net9.0-windows`, x64.

- **SilkNetVoronoi** has **two orthogonal axes**, both runtime-switchable, with the host
  ([Program.cs](SilkNetVoronoi/Program.cs)) owning the window, input, shared instance VBO and
  the live `scene | technique` ms/FPS title (**VSync off** so it's meaningful). Source is
  grouped into folders: scenes in [scenes/](SilkNetVoronoi/scenes/), renderers in
  [voronoi/](SilkNetVoronoi/voronoi/), shaders in [shaders/](SilkNetVoronoi/shaders/), images
  in `assets/`; `Program.cs`, `IScene.cs`, `GlHelpers.cs` sit at the root.
  - **Scene** (`IScene`, F1/F2/F3/F4) = the site simulation, producing NDC positions:
    [RandomFieldScene](SilkNetVoronoi/scenes/RandomFieldScene.cs) (drift + bounce, the original),
    [BloodVeinScene](SilkNetVoronoi/scenes/BloodVeinScene.cs) (Poiseuille-profile flow band + static
    body cells), [ImageDivergeScene](SilkNetVoronoi/scenes/ImageDivergeScene.cs) (darkness-weighted
    rejection-sampling of an image into home positions; **D** eases sites to/from a scattered
    cloud), [TensionFieldScene](SilkNetVoronoi/scenes/TensionFieldScene.cs) (drift + neighbour
    repulsion for even spacing, ported from Generative Design M_6_1_03; uses a uniform spatial
    grid for O(N) neighbour search). Image loaded via **StbImageSharp** from `assets/`
    (copy-to-output, CLI-overridable, procedural heart fallback). Scene keys forward via `IScene.OnKey`.
  - **Technique** (`IVoronoiRenderer`, Space/Tab) = how it's drawn, independent of scene:
    [ConeRenderer.cs](SilkNetVoronoi/voronoi/ConeRenderer.cs) (cone rasterization, **bounded cone
    radius** auto-sized as `6·2/√SiteCount` — the key perf fix vs the old screen-spanning
    `radius = 3.0`) and [JfaRenderer.cs](SilkNetVoronoi/voronoi/JfaRenderer.cs) (fragment-shader JFA).
  - Startup args are order-free tokens: `cone`/`jfa`, `f1`/`f2`/`f3` (or `random`/`vein`/`image`),
    and any other token is an image path. At 2048 sites bounded cones (~2.7 ms) beat JFA
    (~3.5 ms); JFA's flat cost wins higher (8192 sites: JFA ~3.8 ms < cones ~4.7 ms).
  - **Runtime tweak overlay**: a Dear ImGui panel (`Silk.NET.OpenGL.Extensions.ImGui`, pulls in
    ImGui.NET + native cimgui) created in `OnLoad` and drawn each frame in `Program.DrawOverlay`
    after the scene render. Both `IScene` and `IVoronoiRenderer` have an optional `DrawUi()`
    default method; each active scene/renderer renders its own sliders (tension forces, flow
    speed, dot size, cone segments, JFA dot radius), so their tunables are mutable instance
    fields rather than consts. Key events still reach `OnKeyDown` even over the UI (not gated on
    `ImGui.GetIO().WantCaptureKeyboard`).
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
