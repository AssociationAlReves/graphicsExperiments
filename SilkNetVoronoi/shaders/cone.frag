#version 330 core
// Cone technique - pass 1/3 (id render), fragment stage.
// Writes the encoded site id straight into the offscreen RGBA8 id texture. The depth
// test (configured by the renderer) decides which cone wins each pixel, so the texture
// ends up holding, per pixel, the id of the nearest site = the Voronoi partition.

flat in vec3 vId;                 // site id as RGB, from the vertex stage (not interpolated)
out vec4 FragColor;

void main() { FragColor = vec4(vId, 1.0); }
