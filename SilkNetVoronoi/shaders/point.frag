#version 330 core
// Cone technique - pass 3/3 (site dots), fragment stage. Each point is a square sprite;
// gl_PointCoord runs 0..1 across it, so we discard fragments outside the inscribed circle
// to draw a round white dot, blended on top of the edge image.

out vec4 FragColor;

void main()
{
    vec2 d = gl_PointCoord - vec2(0.5);   // offset from sprite centre
    if (dot(d, d) > 0.25) discard;        // outside radius 0.5 => not part of the disc
    FragColor = vec4(1.0);
}
