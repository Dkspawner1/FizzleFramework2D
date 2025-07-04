using FizzleFramework2D.Graphics.Shapes;
using FizzleFramework2D.Graphics.Textures;
using Schedulers;

namespace FizzleFramework2D.ECS.Components;

public record struct ButtonComponent
{
    public ITexture2D? Teture { get; }
    public Rectangle Rectangle { get; }
    
}