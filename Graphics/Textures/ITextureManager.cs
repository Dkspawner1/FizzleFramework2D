using System;
using System.Threading.Tasks;
using FizzleFramework2D.Content.Assets;
using Hexa.NET.SDL3;

namespace FizzleFramework2D.Graphics.Textures;

public interface ITextureManager : IDisposable
{
    Task<ITexture2D> LoadTextureAsync(string path);
    ITexture2D? GetTexture(string name);
    void EnableHotReload(bool enable);
    event EventHandler<TextureReloadedEventArgs>? TextureReloaded;
}

public interface ITexture2D : IAsset
{
    unsafe SDLGPUTexture* Handle { get; }
    int Width { get; }
    int Height { get; }
    SDLGPUTextureFormat Format { get; }
    bool AlphaBlendEnable { get; }
    bool IsLoaded { get; }
}

public class TextureReloadedEventArgs(string textureName) : EventArgs
{
    public string TextureName { get; } = textureName;
}

public class TextureLoadException : Exception
{
    public TextureLoadException(string message) : base(message) { }
    public TextureLoadException(string message, Exception innerException) : base(message, innerException) { }
}