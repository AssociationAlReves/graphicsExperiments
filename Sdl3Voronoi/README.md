# Sdl3Voronoi

Random moving Voronoi field using the **SDL3 GPU API** (backend-agnostic over
Vulkan / D3D12 / Metal), with the SDL3-CS C# bindings. Rendered black-and-white:
black background, white cell borders, a small white dot at each site. Same
cone-rasterization technique as the Silk.NET sample, driven through SDL's modern
GPU abstraction.

## Run

```sh
dotnet run -c Release
```

`SDL3-CS` is bindings-only, so the matching `SDL3-CS.Native` package supplies the
native SDL3 library for win/linux/osx. Press **Esc** or close the window to quit.

## Shaders

The six GLSL shaders are **embedded in `Program.cs`** and compiled to SPIR-V at
startup with shaderc (the `Vortice.ShaderCompiler` package) — there is no offline
`glslangValidator` step and nothing is committed as `.spv`.

> SPIR-V targets SDL's **Vulkan** backend. On a machine where SDL selects D3D12
> or Metal, you'd transpile at load time with `SDL_shadercross`. The
> `CreateGPUDevice` call here requests `SPIRV`.
>
> SDL3's SPIR-V resource-binding rule matters: a **fragment sampler lives in
> descriptor set 2** (see the edge shader's `layout(set = 2, binding = 0)`).

## How it works

Three passes per frame:

1. **Cone pass** — a static cone mesh (slot 0) + a dynamic per-instance buffer of
   site positions (slot 1) feed an instanced draw. Each cone emits its **site id**
   (encoded as an RGBA8 colour); the depth buffer (`D32Float`) resolves the
   Voronoi partition. Renders into an offscreen id texture.
2. **Edge pass** — a fullscreen triangle samples the id texture and emits white
   where the id differs from the right/down neighbour (a cell border).
3. **Dot pass** — instanced quads draw a round white dot at each site.

Per frame: update sites on the CPU → map/upload the instance transfer buffer →
acquire the swapchain texture → render the three passes → submit.

## Verified

Builds clean with `dotnet build -c Release` on .NET 9 (0 errors) and runs (render
loop verified live: native SDL3 loads, all shaders compile to SPIR-V, the three
pipelines build and draw). SDL3-CS 3.4.2 / SDL3-CS.Native 3.4.2 /
Vortice.ShaderCompiler 1.9.0.
