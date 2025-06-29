using System;
using Hexa.NET.SDL3;
using Serilog;
using static Hexa.NET.SDL3.SDL;
using Log = Serilog.Log;

namespace FizzleFramework2D.Graphics.Shaders;

internal sealed class Shader : IShader
{
    private static readonly ILogger logger = Log.ForContext<Shader>();
    private readonly unsafe SDLGPUDevice* device;
    private readonly byte[] bytecodeArray; // Fixed: Store as byte[] instead of ReadOnlySpan
    private bool disposed;

    public string Name { get; }
    public SDLGPUShaderStage Stage { get; }
    public unsafe SDLGPUShader* Handle { get; private set; }

    public ReadOnlySpan<byte> GetBytecode() => bytecodeArray.AsSpan();

    public unsafe bool IsCompiled => Handle != null && !disposed;
    public bool IsDisposed => disposed;

    public unsafe Shader(string name, SDLGPUShaderStage stage, SDLGPUDevice* device, SDLGPUShader* handle,
        byte[] bytecode)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Stage = stage;
        this.device = device;
        Handle = handle;
        bytecodeArray = bytecode ?? throw new ArgumentNullException(nameof(bytecode)); // Fixed: Use bytecodeArray

        logger.Debug("Created shader: {Name} ({Stage})", Name, Stage);
    }

    public unsafe void Dispose()
    {
        if (!disposed)
        {
            logger.Debug("Disposing shader: {Name}", Name);

            if (Handle != null)
            {
                ReleaseGPUShader(device, Handle);
                Handle = null;
            }

            disposed = true;
            logger.Verbose("Shader disposed: {Name}", Name);
        }
    }
}

internal sealed class ShaderProgram : IShaderProgram
{
    private static readonly ILogger logger = Log.ForContext<ShaderProgram>();
    private readonly unsafe SDLGPUDevice* device;
    private bool disposed;

    public string Name { get; }
    public unsafe SDLGPUGraphicsPipeline* Pipeline { get; private set; }
    public IShader VertexShader { get; }
    public IShader FragmentShader { get; }
    public unsafe bool IsLinked => Pipeline != null && !disposed;
    public bool IsDisposed => disposed;

    public unsafe ShaderProgram(string name, IShader vertexShader, IShader fragmentShader,
        SDLGPUDevice* device, SDLGPUGraphicsPipeline* pipeline)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        VertexShader = vertexShader ?? throw new ArgumentNullException(nameof(vertexShader));
        FragmentShader = fragmentShader ?? throw new ArgumentNullException(nameof(fragmentShader));
        this.device = device;
        Pipeline = pipeline;

        logger.Debug("Created shader program: {Name}", Name);
    }

    public unsafe void Dispose()
    {
        if (!disposed)
        {
            logger.Debug("Disposing shader program: {Name}", Name);

            if (Pipeline != null)
            {
                ReleaseGPUGraphicsPipeline(device, Pipeline);
                Pipeline = null;
            }

            disposed = true;
            logger.Verbose("Shader program disposed: {Name}", Name);
        }
    }
}