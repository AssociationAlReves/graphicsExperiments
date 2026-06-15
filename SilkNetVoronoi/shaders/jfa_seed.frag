#version 330 core
// JFA technique - seed pass, fragment stage. Each seeded texel stores the pixel
// coordinate of the site that landed on it; the flood passes then propagate these
// nearest-seed coordinates outwards. The texture is pre-cleared to the sentinel (-1,-1)
// ("no seed"), so only the site texels get a valid coordinate here.

out vec4 o;   // RG = seed pixel coordinate (only .xy are stored; target is RG32F)

void main() { o = vec4(gl_FragCoord.xy, 0.0, 0.0); }
