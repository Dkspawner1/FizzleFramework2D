using Arch.Core;

namespace FizzleFramework2D.ECS.Systems;

public abstract class SystemBase<T>(World world)
{
    public World World { get; private set; } = world;
    public abstract void Update(in T state);
}