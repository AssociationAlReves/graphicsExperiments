#version 330 core
// Cone technique - pass 1/3 (id render), vertex stage.
// One instanced cone per site: the cone mesh (location 0) is offset to the site
// position (location 1, per-instance). Every cone shares the same slope, so the depth
// test in this pass keeps the nearest apex per pixel - i.e. the Voronoi cell. The site
// id is encoded into a colour here and passed flat to the fragment stage.

layout(location = 0) in vec3 aConeVertex;   // unit cone vertex (apex at z=0, base ring z=1)
layout(location = 1) in vec2 aSitePos;       // per-instance site position, NDC

flat out vec3 vId;                            // site id as RGB (flat: no interpolation)

void main()
{
    vec3 p = aConeVertex;
    p.xy += aSitePos;                         // move the cone so its apex sits on the site
    // Cone height is normalized to 1, so map z (0..1) into [0, 0.9] for good depth range.
    gl_Position = vec4(p.xy, p.z * 0.9, 1.0);

    int id = gl_InstanceID + 1;               // +1 so id 0 stays reserved for 'background'
    vId = vec3(                               // pack the 24-bit id into RGB bytes
        float(id & 0xFF) / 255.0,
        float((id >> 8) & 0xFF) / 255.0,
        float((id >> 16) & 0xFF) / 255.0);
}
