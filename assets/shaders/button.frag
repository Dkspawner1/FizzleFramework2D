#version 450

layout(location = 0) in vec2 frag_texcoord;
layout(location = 1) in vec4 frag_color;
layout(location = 0) out vec4 out_color;

void main() {
    // Simple solid color output (no texture sampling)
    out_color = frag_color;

    // Optional: Add some UV-based gradient effect
    float gradient = (frag_texcoord.x + frag_texcoord.y) * 0.5;
    out_color.rgb *= (0.8 + 0.2 * gradient);
}
