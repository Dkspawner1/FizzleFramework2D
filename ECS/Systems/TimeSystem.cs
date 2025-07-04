using Arch.Core;
using FizzleFramework2D.Core;
using FizzleFramework2D.ECS.Components;

namespace FizzleFramework2D.ECS.Systems;

public class TimeSystem : SystemBase
{
    private readonly GameTime gameTime;
    private Entity timeEntity;

    public TimeSystem(World world, GameTime gameTime) : base(world)
    {
        this.gameTime = gameTime;

        // Create a singleton time entity
        timeEntity = World.Create(new TimeComponent(gameTime));
    }

    protected override void Update(in TimeComponent timeComponent)
    {
        // Update GameTime
        gameTime.Update();

        // Create updated time component with current values
        var updatedTimeComponent = new TimeComponent(gameTime);

        // Update the singleton time entity
        World.Set(timeEntity, updatedTimeComponent);
    }

    public TimeComponent GetCurrentTime()
    {
        return World.Get<TimeComponent>(timeEntity);
    }
}