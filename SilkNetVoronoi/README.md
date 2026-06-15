# SilkNetVoronoi

Animated Voronoi diagram, rendered black-and-white on plain OpenGL 3.3 via
Silk.NET: black background, white cell borders, a small white dot at each site.
The most portable of the three samples — no extra toolchain, no precompiled
shaders: the GLSL lives in `shaders/*.vert|*.frag` and is compiled at runtime by
the driver.

It has two independent, runtime-switchable axes:

**Scenes** (the site simulation — pick with F1/F2/F3):

| Key | Scene | What it does |
|---|---|---|
| **F1** | Random field | Sites drift randomly and bounce off the edges |
| **F2** | Blood vein | A central band of cells flows left→right (parabolic profile, recycled at the inlet) framed by static "body" cells |
| **F3** | Image diverge | Sites are sampled from an image (darkness-weighted) so the cells reproduce the picture; **D** scatters them into a cloud and back |
| **F4** | Tension field | Like F1 but with inter-particle repulsion (after Generative Design M_6_1_03): each site pushes near neighbours away, holding a roughly even minimum spacing — grid-accelerated so it stays real-time |

**Techniques** (how the Voronoi is drawn — toggle with Space/Tab):

| Technique | How | Cost |
|---|---|---|
| **Cone** (default) | Cone rasterization with a bounded cone radius | Scales with overdraw; fastest at low/mid counts |
| **JFA** | Jump Flooding via ping-pong FBOs | O(log N) passes, **independent of site count** |

## Run

```sh
dotnet run -c Release                  # Random field, Cone
dotnet run -c Release -- jfa           # start in JFA mode
dotnet run -c Release -- f2            # start in the blood-vein scene
dotnet run -c Release -- f3 path.png   # image scene with your own picture
```

Requires a GPU/driver with OpenGL 3.3 (essentially anything since ~2010). Startup
args are order-free tokens: `cone`/`jfa`, `f1`/`f2`/`f3` (or `random`/`vein`/`image`),
and any other token is treated as an image path for the F3 scene.

- **F1 / F2 / F3 / F4** — select scene
- **Space** / **Tab** — toggle Cone ⇄ JFA technique
- **D** — (image scene) scatter / reassemble the picture
- **V** — toggle VSync on/off
- **Esc** — quit

## Tweak overlay

A **Dear ImGui** "Tweaks" panel (via `Silk.NET.OpenGL.Extensions.ImGui`) is drawn on top of
the scene for live parameter editing — no rebuild needed. It always shows FPS plus global
**Speed**/**VSync**, then the **active scene's** own controls and the **active technique's**
own controls, which change as you switch with the F-keys / Space:

- Tension field: strength, damping, max speed, repel radius.
- Blood vein: flow speed, vein half-height. · Image: scatter/assemble button, ease time.
- Cone: dot size, cone segment count. · JFA: dot radius.

Each scene/renderer renders its own widgets via an optional `DrawUi()` on `IScene` /
`IVoronoiRenderer`, so the panel auto-adapts to whatever is active.

The window title (and stdout) shows the active `scene | technique` plus a live `ms/FPS`
readout. **VSync starts disabled** so the readout reflects real GPU cost rather than being
capped at the monitor refresh; press **V** to cap it for smooth playback.

## Architecture

Two orthogonal abstractions, both swapped at runtime by the host (`Program.cs`, which
owns the window, input, the shared per-instance position buffer and the FPS readout):

- **`IScene`** — a simulation that produces NDC site positions each frame. Implementations:
  `RandomFieldScene`, `BloodVeinScene`, `ImageDivergeScene`. The host uploads the active
  scene's positions (a `Vector2` span, no copy) to the instance buffer.
- **`IVoronoiRenderer`** — visualizes whatever positions exist, so it's independent of the
  scene:
  - `ConeRenderer.cs` — each site is a 3D cone (apex at the site, shared slope); the depth
    test keeps the nearest apex per pixel. Three passes: cones → offscreen **site-id**
    texture (RGBA8) with depth → fullscreen edge-detect → round white site points.
  - `JfaRenderer.cs` — two `RG32F` textures store each pixel's nearest-seed coordinate; a
    seed pass plants the sites, ~log₂(N) flood passes propagate the nearest seed at halving
    step sizes, and a final pass draws borders + dots from the seed field.

The `ImageDivergeScene` loads its picture with **StbImageSharp** (pure-managed) from
`assets/sample.png` (copied next to the binary, overridable via the CLI arg); a missing
file falls back to a built-in procedural target.

## Shaders

All GLSL is in [shaders/](shaders/), one file per stage, each with a header comment
describing where it sits in its technique's pipeline. The fullscreen-triangle vertex
shader (`fullscreen.vert`) is shared by the cone edge pass and both JFA fullscreen
passes. The files are copied next to the binary (`CopyToOutputDirectory`) and loaded at
runtime via `GlHelpers.LinkFromFiles` (resolved against the executable's directory), so a
shader tweak takes effect on the next `dotnet run` without recompiling any C#.

> GLSL source must be ASCII — keep comments plain (no “—”, “•”, etc.), or the driver's
> compiler reports a spurious `pre-mature EOF` error.

## The performance fix (cone path)

The original cone used `radius = 3.0` NDC — a **1500px** screen radius, so one cone
covered ~7× the viewport and 2048 cones cost ~1.4 **billion** fragments/frame. The
radius is now bounded to the realistic max cell size, auto-sized from the site count
(`≈ 6·2/√N`, ~132px at 2048 sites). Because every cone still shares one radius, the
depth-resolved partition is unchanged, but overdraw drops ~100×.

Measured on this machine (1000×1000, 2048 sites, VSync off):

| | ms/frame | FPS |
|---|---|---|
| Cone, naive `radius = 3.0` | ~19.1 | ~52 |
| **Cone, bounded radius** | **~2.7** | **~374** |
| JFA | ~3.5 | ~283 |

So at 2048 sites the bounded cone wins; JFA's flat, site-count-independent cost
makes it the better choice as the site count grows much larger.

> Bounded cones leave any pixel farther than the cone radius from every site as
> background. With 2048 bouncing sites that effectively never happens, and the
> radius formula scales with `SiteCount`.

## Verified

Builds clean with `dotnet build -c Release` on .NET 9 (0 warnings, 0 errors); both
techniques verified to run (render loop live, ms/FPS captured). Silk.NET 2.21.0.
