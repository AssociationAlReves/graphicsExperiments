using ComputeSharp;

namespace ComputeSharpVoronoi;

/// <summary>
/// Seeds the jump-flood buffer. Every pixel starts "unowned" (-1); pixels
/// that coincide with a site store that site's index. We encode the seed as
/// the nearest-site index per texel inside an int buffer.
/// </summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct SeedShader(
    ReadWriteBuffer<int> seedBuffer,
    ReadOnlyBuffer<float2> sites,
    int width,
    int height,
    int siteCount) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= width || y >= height) return;

        int idx = y * width + x;
        seedBuffer[idx] = -1;

        // Mark sites whose rounded pixel lands on this texel.
        for (int i = 0; i < siteCount; i++)
        {
            int sx = (int)(sites[i].X * width);
            int sy = (int)(sites[i].Y * height);
            if (sx == x && sy == y)
            {
                seedBuffer[idx] = i;
                return;
            }
        }
    }
}

/// <summary>
/// One Jump Flooding pass at a given step size. Each pixel looks at 8
/// neighbours offset by <paramref name="step"/> and keeps whichever owning
/// site is closest. Running this with step = N/2, N/4, ... 1 yields a full
/// Voronoi partition in O(log N) passes.
/// </summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct JumpFloodShader(
    ReadWriteBuffer<int> source,
    ReadWriteBuffer<int> dest,
    ReadOnlyBuffer<float2> sites,
    int width,
    int height,
    int step) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= width || y >= height) return;

        int idx = y * width + x;
        int best = source[idx];
        float bestDist = 1e20f;

        if (best >= 0)
        {
            float2 sp = sites[best];
            float dx = sp.X * width - x;
            float dy = sp.Y * height - y;
            bestDist = dx * dx + dy * dy;
        }

        for (int oy = -1; oy <= 1; oy++)
        {
            for (int ox = -1; ox <= 1; ox++)
            {
                int nx = x + ox * step;
                int ny = y + oy * step;
                if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;

                int candidate = source[ny * width + nx];
                if (candidate < 0) continue;

                float2 sp = sites[candidate];
                float dx = sp.X * width - x;
                float dy = sp.Y * height - y;
                float d = dx * dx + dy * dy;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = candidate;
                }
            }
        }

        dest[idx] = best;
    }
}

/// <summary>
/// Black-and-white render of the partition: black background, white Voronoi
/// borders (where the owner differs from the right/down neighbour) and a small
/// white disc at each site. The disc is found cheaply — a pixel within
/// <paramref name="dotRadius"/> of its own owning site is part of that site's dot.
/// </summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct ColorShader(
    ReadWriteBuffer<int> owner,
    ReadWriteBuffer<float4> output,
    ReadOnlyBuffer<float2> sites,
    int width,
    int height,
    float dotRadius) : IComputeShader
{
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        if (x >= width || y >= height) return;

        int idx = y * width + x;
        int me = owner[idx];

        // Border: owner differs from the right/down neighbour.
        bool white = false;
        if (x + 1 < width && owner[idx + 1] != me) white = true;
        if (y + 1 < height && owner[idx + width] != me) white = true;

        // Site dot: distance from this pixel to its owning site, in pixels.
        if (!white && me >= 0)
        {
            float dx = sites[me].X * width - x;
            float dy = sites[me].Y * height - y;
            if (dx * dx + dy * dy <= dotRadius * dotRadius) white = true;
        }

        output[idx] = white ? new float4(1, 1, 1, 1) : new float4(0, 0, 0, 1);
    }
}
