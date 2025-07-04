using Arch.Core;
using FizzleFramework2D.ECS.Components;

namespace FizzleFramework2D.ECS.Systems;
    public abstract class SystemBase(World world)
    {
        protected World World { get; private set; } = world;

        protected abstract void Update(in TimeComponent timeComponent);
    }
