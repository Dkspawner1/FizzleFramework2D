using System;

namespace FizzleFramework2D.Core;

public interface IApplication : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Function Templates
    /// </summary>
    bool Initialize();
    void LoadContent();
    void Run();
    void UnloadContent();
    /// <summary>
    ///  Properties 
    /// </summary>
    bool IsInitialized { get; }
    bool IsContentLoaded { get; }
    bool IsRunning { get; }

}