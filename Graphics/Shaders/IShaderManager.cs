using System;
using System.Threading.Tasks;
using FizzleFramework2D.Content.Assets;
using Hexa.NET.SDL3;

namespace FizzleFramework2D.Graphics.Shaders;
    public interface IShaderManager : IDisposable
    {
        Task<IShader> LoadShaderAsync(string name, SDLGPUShaderStage stage);
        Task<IShaderProgram> CreateProgramAsync(string vertexShader, string fragmentShader);
        IShader? GetShader(string name);
        IShaderProgram? GetProgram(string name);
        void EnableHotReload(bool enable);
        event EventHandler<ShaderReloadedEventArgs>? ShaderReloaded;
    }

    public interface IShader : IAsset
    {
        SDLGPUShaderStage Stage { get; }
        unsafe SDLGPUShader* Handle { get; }
        ReadOnlySpan<byte> GetBytecode();
        bool IsCompiled { get; }
    }

    public interface IShaderProgram : IAsset
    {
        unsafe SDLGPUGraphicsPipeline* Pipeline { get; }
        IShader VertexShader { get; }
        IShader FragmentShader { get; }
        bool IsLinked { get; }
    }

    public class ShaderReloadedEventArgs(string shaderName) : EventArgs
    {
        public string ShaderName { get; } = shaderName;
    }

    public class ShaderCompilationException : Exception
    {
        public ShaderCompilationException(string message) : base(message) { }
        public ShaderCompilationException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class ShaderLinkException : Exception
    {
        public ShaderLinkException(string message) : base(message) { }
        public ShaderLinkException(string message, Exception innerException) : base(message, innerException) { }
}