#version 330 core
// JFA technique - flood pass, fragment stage (run ~log2(N) times, ping-ponging between
// two RG32F textures). At step sizes N/2, N/4, ... 1, each pixel looks at its 8 neighbours
// (plus itself) offset by the current step and keeps whichever stored seed is closest to
// it. After the last step every pixel holds the coordinate of its nearest seed = Voronoi.

uniform sampler2D uTex;   // current nearest-seed coords (previous ping-pong result)
uniform int uStep;         // neighbour offset for this pass, in pixels
out vec4 o;                // RG = chosen nearest-seed coordinate

void main()
{
    ivec2 sz = textureSize(uTex, 0);
    ivec2 c = ivec2(gl_FragCoord.xy);
    vec2 here = gl_FragCoord.xy;       // this pixel's position

    vec2 best = vec2(-1.0);             // sentinel: no seed found yet
    float bestDist = 1e30;

    // 3x3 neighbourhood at +/- uStep (includes the centre at dx=dy=0).
    for (int dy = -1; dy <= 1; dy++)
    for (int dx = -1; dx <= 1; dx++)
    {
        ivec2 nc = clamp(c + ivec2(dx, dy) * uStep, ivec2(0), sz - 1);
        vec2 seed = texelFetch(uTex, nc, 0).xy;
        if (seed.x < 0.0) continue;                // sentinel: neighbour has no seed yet
        float d = dot(seed - here, seed - here);   // squared distance to that seed
        if (d < bestDist) { bestDist = d; best = seed; }
    }
    o = vec4(best, 0.0, 0.0);
}
