#version 330 core
// JFA technique - seed pass, vertex stage. Draws one 1px GL point per site so each site
// lands on exactly one texel of the seed texture. gl_PointSize is set explicitly to 1.0
// because the renderer leaves GL_PROGRAM_POINT_SIZE enabled (from the cone path) and an
// unwritten point size would be undefined.

layout(location = 0) in vec2 aPos;   // site position, NDC (shared instance buffer)

void main()
{
    gl_Position = vec4(aPos, 0.0, 1.0);
    gl_PointSize = 1.0;
}
