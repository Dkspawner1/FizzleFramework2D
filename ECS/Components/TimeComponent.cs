using FizzleFramework2D.Core;

namespace FizzleFramework2D.ECS.Components;

public readonly struct TimeComponent(
    double deltaTime,
    double totalTime,
    float currentFps,
    float averageFps,
    long frameCount,
    FrameRateMode frameRateMode)
{
    public double DeltaTime { get; init; } = deltaTime;
    public double TotalTime { get; init; } = totalTime;
    public float CurrentFPS { get; init; } = currentFps;
    public float AverageFPS { get; init; } = averageFps;
    public long FrameCount { get; init; } = frameCount;
    public FrameRateMode FrameRateMode { get; init; } = frameRateMode;

    public TimeComponent(GameTime gameTime) : this(gameTime.DeltaTime, gameTime.TotalTime, gameTime.CurrentFPS, gameTime.AverageFPS, gameTime.FrameCount, gameTime.FrameRateMode)
    {
    }

    public static TimeComponent Default => new TimeComponent(0.0, 0.0, 0f, 0f, 0L, FrameRateMode.VSync);
}