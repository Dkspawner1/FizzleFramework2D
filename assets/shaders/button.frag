﻿#version 450

layout(location = 0) in  vec2 frag_uv;
layout(location = 1) in  vec4 frag_color;
layout(location = 0) out vec4 out_color;

/*  SDL 3 binds “fragment samplers” with
    SDL_BindGPUFragmentSamplers().  Slot 0 maps to binding 0.          */
layout(binding = 0) uniform sampler2D uTex;

void main()
{
    vec4 tex = texture(uTex, frag_uv);
    out_color = tex * frag_color;        // tint
}
