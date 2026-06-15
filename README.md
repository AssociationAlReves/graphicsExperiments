# graphicsExperiments — Animated Voronoi: three C# approaches

Three standalone samples, each rendering the **same scene** — a field of 2048
Voronoi sites drifting randomly and bouncing off the screen edges — with a
different modern C# graphics stack. All render the same **black-and-white** look:
black background, white cell borders, a small white dot at each site.

| Project | Stack | Voronoi technique | Platforms |
|---|---|---|---|
| `SilkNetVoronoi` | Silk.NET + OpenGL 3.3 | Cone rasterization **+** JFA (toggle at runtime) | Win / Linux / macOS |
| `ComputeSharpVoronoi` | ComputeSharp (DX12) | Jump Flooding Algorithm (GPU compute) | Windows only |
| `Sdl3Voronoi` | SDL3 GPU API + SPIR-V | Cone rasterization (depth buffer) | Win / Linux / macOS |

All three build with the .NET 9 SDK and were verified to launch and run. See
per-project READMEs for run instructions and platform caveats.

## Getting the black-and-white look

Cell borders need to compare each pixel's owning site against its neighbours, so
all three first resolve the partition and then detect edges:

- **Cone samples (Silk.NET, SDL3):** render the cones into an offscreen texture
  storing each site's *id* (not a colour), then a fullscreen pass emits white
  where the id differs from the right/down neighbour. Site dots are drawn on top
  (round points in OpenGL; instanced quads in SDL3).
- **JFA sample (ComputeSharp):** the final colour shader already has the per-pixel
  owner, so it emits white on owner mismatches and white discs near each site.

Since the render is monochrome, the per-site instance data is just the position
(no colour) — slightly less per-frame bandwidth.

## The two techniques, briefly

**Cone rasterization** (Silk.NET, SDL3): draw one 3D cone per site with its apex
at the site position. Every cone shares the same slope, so the depth test keeps
the nearest apex per pixel — which is exactly the Voronoi cell. Zero CPU
geometry, scales to thousands of sites, works on any GPU with a depth buffer.
Each cone is drawn via *instancing*: one static cone mesh + a per-instance
buffer of site positions refreshed every frame.

**Jump Flooding Algorithm / JFA** (ComputeSharp): compute the Voronoi diagram as
a texture in O(log n) passes. Seed each site's pixel, then repeatedly let every
pixel sample 8 neighbours at halving step sizes, keeping the nearest site. Ideal
when you already think in texture space (e.g. the image-divergence scene) and
for very large site counts.

## Quick start

```sh
# Silk.NET — most portable, no extra toolchain
cd SilkNetVoronoi && dotnet run -c Release

# ComputeSharp — Windows + DX12 GPU required
cd ComputeSharpVoronoi && dotnet run -c Release

# SDL3 — GLSL shaders compiled to SPIR-V at startup (shaderc), no toolchain
cd Sdl3Voronoi && dotnet run -c Release
```

Press **Esc** (or close the window) to quit any of them.

## Which should you use?

- **Want it to just run everywhere with the least setup?** → Silk.NET.
- **Windows-only and want shaders written in C#?** → ComputeSharp.
- **Want one library for window + input + GPU, cross-platform?** → SDL3.

## Scenes

All of the brief's scenes are implemented in **`SilkNetVoronoi`**, switchable at runtime
with **F1/F2/F3/F4** (independent of the Cone/JFA technique toggle on Space):

- **F1 — random field**: points drifting and bouncing.
- **F2 — blood vein**: a central band of cells flowing with a parabolic profile, framed
  by static surrounding "body" cells.
- **F3 — image diverge**: sites sampled from a picture so the cells reproduce it; **D**
  scatters them into a cloud and reassembles the image.
- **F4 — tension field**: like F1 but with inter-particle repulsion (after Generative
  Design M_6_1_03), so points hold an even minimum spacing.

See [SilkNetVoronoi/README.md](SilkNetVoronoi/README.md) for controls and details.
