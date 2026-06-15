#version 330 core
// JFA technique - final pass, fragment stage. Turns the finished nearest-seed-coordinate
// texture into the black-and-white look (matching the cone path): white on a cell border
// (this pixel's seed differs from its right/down neighbour) or inside a site dot (this
// pixel is within uDotRadius of its own seed), black otherwise.

uniform sampler2D uTex;     // final nearest-seed coords (after all flood passes)
uniform float uDotRadius;    // site marker radius, in pixels
out vec4 FragColor;

void main()
{
    ivec2 sz = textureSize(uTex, 0);
    ivec2 c = ivec2(gl_FragCoord.xy);
    vec2 me = texelFetch(uTex, c, 0).xy;    // coordinate of my nearest seed

    bool white = false;
    if (me.x >= 0.0)                         // skip sentinel (should not occur post-flood)
    {
        vec2 r = texelFetch(uTex, ivec2(min(c.x + 1, sz.x - 1), c.y), 0).xy; // right neighbour
        vec2 d = texelFetch(uTex, ivec2(c.x, min(c.y + 1, sz.y - 1)), 0).xy; // down neighbour
        if (me != r || me != d) white = true;                                 // cell border
        else if (distance(gl_FragCoord.xy, me) <= uDotRadius) white = true;   // site dot
    }
    FragColor = white ? vec4(1.0) : vec4(0.0, 0.0, 0.0, 1.0);
}
