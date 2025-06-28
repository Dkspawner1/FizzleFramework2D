#nullable enable
using Hexa.NET.SDL3;

namespace FizzleFramework2D.Configuration;
    public sealed class GameSettings
    {
        public WindowSettings Window { get; init; } = new();
        public RenderSettings Rendering { get; init; } = new();
        public ContentSettings Content { get; init; } = new();
        public DevelopmentSettings Development { get; init; } = new();
    }

    public sealed class WindowSettings
    {
        public string Title { get; init; } = "FizzleFramework2D";
        public int Width { get; init; } = 1600;
        public int Height { get; init; } = 900;
        public bool Resizable { get; init; } = true;
    }

    public sealed class RenderSettings
    {
        public bool VSync { get; init; } = true;
        public SDLGPUShaderFormat ShaderFormats { get; init; } = 
            SDLGPUShaderFormat.Spirv | SDLGPUShaderFormat.Dxil | SDLGPUShaderFormat.Metallib;
    }

    public sealed class ContentSettings
    {
        public string ShadersDirectory { get; init; } = "assets/shaders";
    }

    public sealed class DevelopmentSettings
    {
        public bool EnableHotReload { get; init; } = true;
    }
