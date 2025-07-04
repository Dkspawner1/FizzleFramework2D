using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Linq;

namespace FizzleFramework2D.Core;

    public enum FrameRateMode
    {
        VSync,
        Fps30,
        Fps60,
        Fps120,
        Fps144,
        Unlimited
    }

    public sealed class GameTime : IDisposable
    {
        // High-resolution timing using Stopwatch for maximum precision
        private static readonly double TickFrequency = 1.0 / Stopwatch.Frequency;
        private readonly Stopwatch gameStopwatch;
        private readonly object lockObject = new object();

        // Thread-safe atomic operations for critical timing data
        // NOTE: long fields cannot be volatile in C#, use Interlocked operations instead
        private long lastFrameTimestamp;
        private long totalElapsedTicks;
        private volatile bool isRunning;

        // Frame rate management
        private FrameRateMode frameRateMode;
        private double targetFrameTime;
        private bool useFrameLimiting;

        // Performance tracking with thread-safe collections
        private readonly ConcurrentQueue<double> frameTimeHistory;
        private const int HistorySize = 60; // Store last 60 frame times

        // Async timing support
        private readonly TaskCompletionSource<bool> initializationTcs;
        private CancellationTokenSource internalCts;

        // Convert FrameCount to a private field for Interlocked operations
        private long frameCount;

        // Properties with thread-safe access
        public double DeltaTime { get; private set; }
        public double TotalTime { get; private set; }
        public float CurrentFPS { get; private set; }
        public float AverageFPS { get; private set; }
        
        // Expose FrameCount as a property that reads the field
        public long FrameCount => Interlocked.Read(ref frameCount);
        
        public FrameRateMode FrameRateMode => frameRateMode;
        public bool IsRunning => isRunning;

        public GameTime(FrameRateMode frameRateMode = FrameRateMode.VSync)
        {
            gameStopwatch = new Stopwatch();
            frameTimeHistory = new ConcurrentQueue<double>();
            initializationTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internalCts = new CancellationTokenSource();

            SetFrameRateMode(frameRateMode);

            // Verify high-resolution timer availability
            if (!Stopwatch.IsHighResolution)
            {
                throw new NotSupportedException("High-resolution timing not available on this system");
            }
        }

        public void SetFrameRateMode(FrameRateMode mode)
        {
            lock (lockObject)
            {
                frameRateMode = mode;
                targetFrameTime = mode switch
                {
                    FrameRateMode.Fps30 => 1.0 / 30.0,
                    FrameRateMode.Fps60 => 1.0 / 60.0,
                    FrameRateMode.Fps120 => 1.0 / 120.0,
                    FrameRateMode.Fps144 => 1.0 / 144.0,
                    _ => 0.0
                };
                useFrameLimiting = mode != FrameRateMode.VSync && mode != FrameRateMode.Unlimited;
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, internalCts.Token);

            lock (lockObject)
            {
                if (isRunning)
                    throw new InvalidOperationException("GameTime is already running");

                gameStopwatch.Start();
                // Use Interlocked for thread-safe long operations
                Interlocked.Exchange(ref lastFrameTimestamp, gameStopwatch.ElapsedTicks);
                isRunning = true;
            }

            initializationTcs.SetResult(true);

            // Optional: Start background FPS calculation task
            _ = Task.Run(() => UpdateAverageFpsAsync(linkedCts.Token), linkedCts.Token);
        }

        public void Update()
        {
            if (!isRunning)
                return;

            long currentTicks;
            double deltaTime;

            lock (lockObject)
            {
                currentTicks = gameStopwatch.ElapsedTicks;
                var lastTicks = Interlocked.Read(ref lastFrameTimestamp);
                var deltaTicks = currentTicks - lastTicks;
                deltaTime = deltaTicks * TickFrequency;

                // Clamp delta time to prevent large jumps (useful for debugging/pausing)
                deltaTime = Math.Min(deltaTime, 0.1); // Max 100ms delta

                DeltaTime = deltaTime;
                TotalTime += deltaTime;
                
                // Thread-safe update of lastFrameTimestamp
                Interlocked.Exchange(ref lastFrameTimestamp, currentTicks);
                
                // Now this works! Using the private field with Interlocked.Increment
                Interlocked.Increment(ref frameCount);
            }

            // Update current FPS
            CurrentFPS = deltaTime > 0 ? (float)(1.0 / deltaTime) : 0f;

            // Store frame time for averaging (thread-safe)
            frameTimeHistory.Enqueue(deltaTime);

            // Maintain history size
            while (frameTimeHistory.Count > HistorySize)
            {
                frameTimeHistory.TryDequeue(out _);
            }
        }

        public async Task<bool> ShouldLimitFrameAsync(CancellationToken cancellationToken = default)
        {
            if (!useFrameLimiting)
                return false;

            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        public async Task LimitFrameAsync(CancellationToken cancellationToken = default)
        {
            if (!useFrameLimiting)
                return;

            var currentTime = gameStopwatch.ElapsedTicks * TickFrequency;
            var targetTime = TotalTime + targetFrameTime;
            var remainingTime = targetTime - currentTime;

            if (remainingTime > 0)
            {
                // Use Task.Delay for smaller delays to be thread-friendly
                if (remainingTime > 0.001) // 1ms threshold
                {
                    var delayMs = (int)Math.Max(1, (remainingTime - 0.0005) * 1000);
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                }

                // Spin-wait for the remainder for precision
                var targetTicks = (long)(targetTime / TickFrequency);
                while (gameStopwatch.ElapsedTicks < targetTicks && !cancellationToken.IsCancellationRequested)
                {
                    Thread.SpinWait(1);
                }
            }
        }

        private async Task UpdateAverageFpsAsync(CancellationToken cancellationToken)
        {
            const int updateIntervalMs = 1000; // Update every second

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(updateIntervalMs, cancellationToken).ConfigureAwait(false);

                    if (frameTimeHistory.IsEmpty)
                        continue;

                    var frames = frameTimeHistory.ToArray();
                    if (frames.Length > 0)
                    {
                        var avgFrameTime = frames.Sum() / frames.Length;
                        AverageFPS = avgFrameTime > 0 ? (float)(1.0 / avgFrameTime) : 0f;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
        {
            await initializationTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Stop()
        {
            lock (lockObject)
            {
                if (!isRunning)
                    return;

                gameStopwatch.Stop();
                isRunning = false;
            }
        }

        public void Dispose()
        {
            Stop();
            internalCts?.Cancel();
            internalCts?.Dispose();
            initializationTcs?.TrySetCanceled();
        }
    }

