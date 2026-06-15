#version 330 core
// Shared fullscreen vertex shader - used by every screen-covering pass:
//   - Cone technique, pass 2/3 (edge.frag)
//   - JFA technique, flood passes (jfa_flood.frag)
//   - JFA technique, final pass  (jfa_final.frag)
// Emits a single oversized triangle that covers the whole screen from gl_VertexID alone,
// so no vertex buffer is bound (draw 3 vertices with an empty VAO). Vertices 0,1,2 map to
// (-1,-1), (3,-1), (-1,3); the part outside [-1,1] is clipped, covering every pixel once.

void main()
{
    float x = -1.0 + float((gl_VertexID & 1) << 2);   // 0->-1, 1->3, 2->-1
    float y = -1.0 + float((gl_VertexID & 2) << 1);    // 0->-1, 1->-1, 2->3
    gl_Position = vec4(x, y, 0.0, 1.0);
}
