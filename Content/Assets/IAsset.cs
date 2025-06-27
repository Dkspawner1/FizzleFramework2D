using System;

namespace FizzleFramework2D.Content.Assets;

public interface IAsset : IDisposable
{
    string Name { get; }
    bool IsDisposed { get; }
}