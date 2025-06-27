using Hexa.NET.SDL3;

namespace FizzleFramework2D.Configuration;

public sealed class GameSettings
{
    public WindowSettings Window { get; init; } = new();
    public RenderSettings Rendering { get; init; } = new();
    public LoggingSettings Logging { get; init; } = new();
    public ContentSettings Content { get; init; } = new();
    public DevelopmentSettings Development { get; init; } = new();
}

public sealed class WindowSettings
{
    public string Title { get; init; } = "FizzleFramework2D";
    public int Width { get; init; } = 1600;
    public int Height { get; init; } = 900;
    public bool Resizable { get; init; } = true;
    public bool HighDPI { get; init; } = true;
}

public sealed class RenderSettings
{
    public int TargetFPS { get; init; } = 60;
    public bool VSync { get; init; } = true;

    public SDLGPUShaderFormat ShaderFormats { get; init; } =
        SDLGPUShaderFormat.Spirv | SDLGPUShaderFormat.Dxil | SDLGPUShaderFormat.Metallib;
}

public sealed class ContentSettings
{
    public string AssetsDirectory { get; init; } = "assets";
    public string ShadersDirectory { get; init; } = "assets/shaders";
    public string TexturesDirectory { get; init; } = "assets/textures";
    public bool EnableAsyncLoading { get; init; } = true;
}

public sealed class DevelopmentSettings
{
    public bool EnableHotReload { get; init; } = true;
    public bool EnableShaderDebugInfo { get; init; } = true;
    public bool EnableVerboseLogging { get; init; } = false;
}

public sealed class LoggingSettings
{
    public bool EnableFileLogging { get; init; } = true;
    public bool EnableConsoleLogging { get; init; } = true;
    public string LogDirectory { get; init; } = "logs";
    public int RetainedFileCount { get; init; } = 7;
    public LogLevel MinimumLevel { get; init; } = LogLevel.Debug;
}

public enum LogLevel
{
    Verbose,
    Debug,
    Information,
    Warning,
    Error,
    Critical
}