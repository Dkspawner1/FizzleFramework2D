#nullable enable
using System;
using Hexa.NET.SDL3;
using Serilog;
using static Hexa.NET.SDL3.SDL;
using Log = Serilog.Log;

namespace FizzleFramework2D.Graphics.Textures;

public class Texture2D : ITexture2D
{
    private static readonly ILogger logger = Log.ForContext<Texture2D>();
    private readonly unsafe SDLGPUDevice* device;
    private bool disposed;

    public string Name { get; }
    public unsafe SDLGPUTexture* Handle { get; private set; }
    public int Width { get; }
    public int Height { get; }
    public SDLGPUTextureFormat Format { get; }

    public bool AlphaBlendEnable { get; set; }
    public unsafe bool IsLoaded => Handle != null && !disposed;
    public bool IsDisposed => disposed;

    public unsafe Texture2D(string name, SDLGPUDevice* device, SDLGPUTexture* handle, int width, int height,
        SDLGPUTextureFormat format)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        this.device = device;
        Handle = handle;
        Width = width;
        Height = height;
        Format = format;
        logger.Debug("Created texture: {Name} ({Width}x{Height}, {Format})", Name, Width, Height, Format);
    }

    public unsafe void Dispose()
    {
        if (!disposed)
        {
            logger.Debug("Disposing texture: {Name}", Name);

            if (Handle != null)
            {
                ReleaseGPUTexture(device, Handle);
                Handle = null;
            }

            disposed = true;
            logger.Verbose("Texture disposed: {Name}", Name);
        }
    }
}