# SilkNetVoronoi

Random moving Voronoi field, rendered black-and-white on plain OpenGL 3.3 via
Silk.NET: black background, white cell borders, a small white dot at each site.
The most portable of the three samples — no extra toolchain, no precompiled
shaders: the GLSL lives in `shaders/*.vert|*.frag` and is compiled at runtime by
the driver.

It ships **two switchable rendering techniques** so you can compare them live:

| Technique | How | Cost |
|---|---|---|
| **Cone** (default) | Cone rasterization with a bounded cone radius | Scales with overdraw; fastest here |
| **JFA** | Jump Flooding via ping-pong FBOs | O(log N) passes, **independent of site count** |

## Run

```sh
dotnet run -c Release          # starts in Cone mode
dotnet run -c Release -- jfa   # starts in JFA mode
```

Requires a GPU/driver with OpenGL 3.3 (essentially anything since ~2010).

- **Space** / **Tab** — toggle Cone ⇄ JFA
- **V** — toggle VSync on/off
- **Esc** — quit

The window title (and stdout) shows a live `ms/FPS` readout for the active
technique. **VSync starts disabled** so the readout reflects real GPU cost rather
than being capped at the monitor refresh; press **V** to cap it at the monitor
refresh for smooth playback.

## How the techniques work

The code is split into a host plus two `IVoronoiRenderer` implementations:

- `Program.cs` — window, input, the CPU site simulation, the shared per-instance
  position buffer (`vec2` per site, streamed once per frame), the toggle and the
  FPS readout.
- `ConeRenderer.cs` — each site is a 3D cone (apex at the site, shared slope); the
  depth test keeps the nearest apex per pixel, so the partition falls out for free.
  Three passes: cones → offscreen **site-id** texture (RGBA8) with depth → fullscreen
  edge-detect (white where the id changes) → round white site points.
- `JfaRenderer.cs` — two `RG32F` textures store each pixel's nearest-seed coordinate;
  a seed pass plants the sites, ~log₂(N) flood passes propagate the nearest seed at
  halving step sizes, and a final pass draws borders + dots from the seed field.

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
