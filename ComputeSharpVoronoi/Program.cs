using System.Numerics;
using ComputeSharp;

namespace ComputeSharpVoronoi;

/// <summary>
/// Random moving Voronoi field computed entirely on the GPU via ComputeSharp
/// (DX12). The Voronoi partition is built with the Jump Flooding Algorithm
/// in a handful of compute passes per frame, then colored and blitted to a
/// WinForms window.
///
/// ComputeSharp is Windows + DX12 only — the shaders live in Shaders.cs and
/// are written in C#, transpiled to HLSL by the ComputeSharp source generator.
/// </summary>
internal static class Program
{
    private const int Width = 1000;
    private const int Height = 1000;
    private const int SiteCount = 2048;
    private const float DotRadius = 3f; // site marker radius, in pixels

    private static readonly Vector2[] _positions = new Vector2[SiteCount];
    private static readonly Vector2[] _velocities = new Vector2[SiteCount];
    private static readonly float2[] _sitesGpu = new float2[SiteCount];

    [STAThread]
    private static void Main()
    {
        InitSites();

        GraphicsDevice device = GraphicsDevice.GetDefault();

        // Persistent GPU buffers.
        using ReadWriteBuffer<int> bufferA = device.AllocateReadWriteBuffer<int>(Width * Height);
        using ReadWriteBuffer<int> bufferB = device.AllocateReadWriteBuffer<int>(Width * Height);
        using ReadWriteBuffer<float4> outputBuffer = device.AllocateReadWriteBuffer<float4>(Width * Height);
        using ReadOnlyBuffer<float2> siteBuffer = device.AllocateReadOnlyBuffer<float2>(SiteCount);

        var pixels = new float4[Width * Height];
        var bitmap = new System.Drawing.Bitmap(Width, Height,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using var form = new VoronoiForm(Width, Height);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        double last = 0;

        form.RenderFrame += () =>
        {
            double now = clock.Elapsed.TotalSeconds;
            float dt = (float)(now - last);
            last = now;

            UpdateSites(dt);
            siteBuffer.CopyFrom(_sitesGpu);

            // 1) Seed.
            device.For(Width, Height, new SeedShader(bufferA, siteBuffer, Width, Height, SiteCount));

            // 2) Jump flooding: ping-pong between buffers at halving step sizes.
            ReadWriteBuffer<int> src = bufferA, dst = bufferB;
            int step = Math.Max(Width, Height) / 2;
            while (step >= 1)
            {
                device.For(Width, Height, new JumpFloodShader(src, dst, siteBuffer, Width, Height, step));
                (src, dst) = (dst, src);
                step /= 2;
            }

            // 3) Color (owner partition is in src after the final swap).
            device.For(Width, Height, new ColorShader(src, outputBuffer, siteBuffer, Width, Height, DotRadius));

            outputBuffer.CopyTo(pixels);
            BlitToBitmap(pixels, bitmap);
            form.Present(bitmap);
        };

        System.Windows.Forms.Application.Run(form);
    }

    private static void InitSites()
    {
        var rng = new Random(1234);
        for (int i = 0; i < SiteCount; i++)
        {
            _positions[i] = new Vector2((float)rng.NextDouble(), (float)rng.NextDouble());
            double a = rng.NextDouble() * Math.PI * 2;
            float speed = 0.02f + (float)rng.NextDouble() * 0.06f;
            _velocities[i] = new Vector2((float)Math.Cos(a) * speed, (float)Math.Sin(a) * speed);
        }
    }

    private static void UpdateSites(float dt)
    {
        for (int i = 0; i < SiteCount; i++)
        {
            Vector2 p = _positions[i] + _velocities[i] * dt;
            if (p.X < 0f || p.X > 1f) { _velocities[i].X *= -1f; p.X = Math.Clamp(p.X, 0f, 1f); }
            if (p.Y < 0f || p.Y > 1f) { _velocities[i].Y *= -1f; p.Y = Math.Clamp(p.Y, 0f, 1f); }
            _positions[i] = p;
            _sitesGpu[i] = new float2(p.X, p.Y);
        }
    }

    private static void BlitToBitmap(float4[] pixels, System.Drawing.Bitmap bmp)
    {
        var data = bmp.LockBits(
            new System.Drawing.Rectangle(0, 0, Width, Height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        unsafe
        {
            byte* ptr = (byte*)data.Scan0;
            for (int i = 0; i < pixels.Length; i++)
            {
                float4 c = pixels[i];
                int o = i * 4;
                ptr[o + 0] = (byte)(Math.Clamp(c.Z, 0f, 1f) * 255); // B
                ptr[o + 1] = (byte)(Math.Clamp(c.Y, 0f, 1f) * 255); // G
                ptr[o + 2] = (byte)(Math.Clamp(c.X, 0f, 1f) * 255); // R
                ptr[o + 3] = 255;                                   // A
            }
        }
        bmp.UnlockBits(data);
    }
}
