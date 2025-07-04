using System;
using Arch.Core;
using Arch.Core.Extensions;
using FizzleFramework2D.ECS.Components;
using FizzleFramework2D.Core; // Add this using directive

namespace FizzleFramework2D.ECS.Systems
{
    public class RenderSystem(World world) : SystemBase(world)
    {
        protected override void Update(in TimeComponent timeComponent)
        {
            // Query entities that need rendering
            var query = new QueryDescription().WithAll<ButtonComponent>();

            var component = timeComponent;
            World.Query(in query, (Entity entity, ref ButtonComponent button) =>
            {
                // Use timeComponent.DeltaTime for frame-rate independent rendering
                // For example, animate button effects based on time
                
                // Example: Pulsing effect
                var pulseIntensity = Math.Sin(component.TotalTime * 2.0) * 0.1 + 1.0;
                
                // Apply rendering logic here using delta time
                Console.WriteLine($"Rendering button {entity.Id} with pulse {pulseIntensity:F2}");
            });
        }

    }
}