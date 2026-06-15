# ComputeSharpVoronoi

Random moving Voronoi field computed on the GPU with the **Jump Flooding
Algorithm**, using ComputeSharp — where the compute shaders are written *in C#*
(see `Shaders.cs`) and transpiled to HLSL by a source generator. Rendered
black-and-white: black background, white cell borders, a small white dot at
each site.

## Requirements

- **Windows only.** ComputeSharp targets DirectX 12, so this project targets
  `net9.0-windows` and needs a DX12-capable GPU.
- The compute shaders are colored/blitted to a small WinForms window.

## Run (on Windows)

```sh
dotnet run -c Release
```

Press **Esc** or close the window to quit.

## How it works

Three compute shaders run per frame (`Program.cs` render loop):

1. **SeedShader** — marks each site's pixel with its index in an int buffer.
2. **JumpFloodShader** — ping-pongs between two buffers at halving step sizes
   (`N/2, N/4, … 1`); each pixel keeps the nearest owning site. O(log N) passes.
3. **ColorShader** — emits white where the owner differs from the right/down
   neighbour (cell border) or where the pixel is within the dot radius of its
   owning site, and black everywhere else.

The owner buffer is read back, blitted into a `Bitmap`, and presented.

## Build note (important)

This project **only builds on Windows.** ComputeSharp's source generator invokes
the native DXC/FXC compiler (via `kernel32`) to compile the shaders at build
time. On non-Windows machines the generator throws `DllNotFoundException:
kernel32` and the generated `IComputeShaderDescriptor` partials are never
emitted — which surfaces as `CS0315: ... no boxing conversion ... to
IComputeShaderDescriptor`. Those errors are an artifact of building off-Windows,
not a defect in the C#. On a Windows box with the .NET 9 SDK the project builds
and runs as-is.

If you only want to *restore/compile-check* on another OS, add
`-p:EnableWindowsTargeting=true` — but the shader generator still needs Windows
to fully succeed.

ComputeSharp 3.1.0.
