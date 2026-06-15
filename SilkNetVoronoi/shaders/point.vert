#version 330 core
// Cone technique - pass 3/3 (site dots), vertex stage. Draws one GL point per site at the
// site position; the renderer enables GL_PROGRAM_POINT_SIZE so gl_PointSize sets the dot
// diameter in pixels. The square point sprite is clipped to a disc in point.frag.

layout(location = 0) in vec2 aPos;   // site position, NDC (shared instance buffer)
uniform float uPointSize;             // dot diameter, in pixels

void main()
{
    gl_Position = vec4(aPos, 0.0, 1.0);
    gl_PointSize = uPointSize;
}
