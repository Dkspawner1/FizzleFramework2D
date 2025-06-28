#version 450

layout(location = 0) in vec3 position;      // Button vertex position
layout(location = 1) in vec2 texcoord;      // UV coordinates
layout(location = 2) in vec4 color;         // Tint color

layout(location = 0) out vec2 frag_texcoord;
layout(location = 1) out vec4 frag_color;

void main() {
    gl_Position = vec4(position, 1.0);
    frag_texcoord = vec2(texcoord.x, 1.0 - texcoord.y);  // Flip Y for Vulkan
    frag_color = color;
}
