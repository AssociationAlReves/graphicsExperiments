#version 330 core
// Cone technique - pass 2/3 (edge detect), fragment stage. Runs over the full screen
// (see fullscreen.vert) and turns the id texture from pass 1 into the black-and-white
// look: a pixel is a cell border (white) when its site id differs from its right or down
// neighbour; otherwise it is interior (black). Site dots are added afterwards in pass 3.

uniform sampler2D uId;            // the offscreen id texture from pass 1
out vec4 FragColor;

void main()
{
    ivec2 sz = textureSize(uId, 0);
    ivec2 c = ivec2(gl_FragCoord.xy);                                   // this pixel
    vec3 me = texelFetch(uId, c, 0).rgb;                                // my site id
    vec3 r  = texelFetch(uId, ivec2(min(c.x + 1, sz.x - 1), c.y), 0).rgb; // right neighbour
    vec3 d  = texelFetch(uId, ivec2(c.x, min(c.y + 1, sz.y - 1)), 0).rgb; // down neighbour
    bool edge = (me != r) || (me != d);                                 // id changes => border
    FragColor = edge ? vec4(1.0) : vec4(0.0, 0.0, 0.0, 1.0);
}
