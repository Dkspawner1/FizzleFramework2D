#version 450

layout(location = 0) in vec3 position;
layout(location = 1) in vec2 texcoord;
layout(location = 2) in vec4 color;

layout(location = 0) out vec2 frag_uv;
layout(location = 1) out vec4 frag_color;

void main() {
    gl_Position = vec4(position, 1.0);
    // FLIP here: CPU-provided (0,0)=bottom-left → sample bottom of image
    frag_uv     = vec2(texcoord.x, 1.0 - texcoord.y);
    frag_color  = color;
    gl_PointSize = 1.0;
}
