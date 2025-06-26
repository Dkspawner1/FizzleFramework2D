using System;
using System.Threading;
using System.Threading.Tasks;
using Hexa.NET.SDL3;
using Serilog;
using static Hexa.NET.SDL3.SDL;
using Log = Serilog.Log;

namespace FizzleFramework2D.Core;

public sealed class Application : IApplication
{
    private static readonly ILogger logger = Log.ForContext<Application>();

    // SDL3 resources
    private unsafe SDLWindow* window;
    private unsafe SDLGPUDevice* device;

    // State management
    private volatile bool running;
    private bool initialized;
    private bool contentLoaded;
    private readonly SemaphoreSlim disposalSemaphore = new(1, 1);
    private readonly object resourceLock = new();
    
    public bool IsInitialized => initialized;
    public bool IsContentLoaded => contentLoaded;
    public bool IsRunning => running;
    
    public bool Initialize()
    {
        unsafe
        {
            logger.Information("Starting FizzleFramework2D initialization");
            try
            {
                // SDL3 initialization
                if (!Init(SDLInitFlags.Video))
                {
                    logger.Error("SDL_Init failed: {Error}", GetError()->ToString());
                    return false;
                }

                // Window creation
                window = CreateWindow("FizzleFramework2D", 1600, 900,
                    SDLWindowFlags.Resizable);

                if (window == null)
                {
                    logger.Error("Window creation failed: {Error}", GetError()->ToString());
                    return false;
                }

                
                device = CreateGPUDevice(
                    SDLGPUShaderFormat.Spirv | SDLGPUShaderFormat.Dxil | SDLGPUShaderFormat.Metallib,
                    true, (byte*)null);
                
                if (device == null)
                {
                    logger.Error("Device creation failed: {Error}", GetError()->ToString());
                    return false;
                }
                
                if(!ClaimWindowForGPUDevice(device, window))
                    return false;
                
                SetGPUSwapchainParameters(device, window, 
                    SDLGPUSwapchainComposition.Sdr,SDLGPUPresentMode.Vsync);

                var swapchainFormat = GetGPUSwapchainTextureFormat(device, window);
                logger.Information("Swapchain format: {Format}", swapchainFormat);

                initialized = true;
                logger.Information("FizzleFramework2D initialization complete");
                return true;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Initialization failed");
                return false;
            }
        }
    }

    public void LoadContent()
    {
        if (!initialized) 
        {
            logger.Warning("LoadContent called before initialization");
            return;
        }
        if (contentLoaded)
        {
            logger.Warning("LoadContent called when content already loaded");
            return;
        }
        logger.Information("Starting content loading");
        try
        {
            // For now, just basic setup - your sophisticated content system will go here later
            contentLoaded = true;
            logger.Information("Content loading complete");
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Content loading failed");
            throw;
        }
    }

    public void Run()
    {
        if (!initialized || !contentLoaded)
        {
            logger.Error("Run called before proper initialization/content loading");
            return;
        }

        running = true;
        logger.Information("Entering main render loop");

        while (running)
        {
            HandleEvents();
            Update();
            Render();

            Thread.Sleep(16); // ~60 FPS
        }

        logger.Information("Exited main render loop");
    }

    private unsafe void HandleEvents()
    {
        SDLEvent e;
        while (PollEvent(&e))
        {
            switch (e.Type)
            {
                case (uint)SDLEventType.Quit:
                    logger.Information("Quit event received");
                    running = false;
                    break;
                case (uint)SDLEventType.KeyDown:
                    logger.Information($"KeyDown event received");
                    if (e.Key.Key == SDLK_ESCAPE)
                        running = false;
                    break;
            }

        }
    }

    private unsafe void Update()
    {
        // Your update logic here
    }

    private unsafe void Render()
    {
        lock (resourceLock)
        {
            var cmd = AcquireGPUCommandBuffer(device);
            if (cmd == null) return;

            SDLGPUTexture* backbuffer;
            uint w, h;
            if (!WaitAndAcquireGPUSwapchainTexture(cmd, window, &backbuffer, &w, &h))
                return;

            var colorTarget = new SDLGPUColorTargetInfo
            {
                Texture = backbuffer,
                LoadOp = SDLGPULoadOp.Clear,
                StoreOp = SDLGPUStoreOp.Store,
                ClearColor = new SDLFColor { R = 0.1f, G = 0.1f, B = 0.1f, A = 1.0f }
            };

            var pass = BeginGPURenderPass(cmd, &colorTarget, 1, null);
            if (pass != null)
            {
                EndGPURenderPass(pass);
            }
            SubmitGPUCommandBuffer(cmd);
        }
    }
 

    public void UnloadContent()
    {
    }

    private unsafe void Dispose(bool disposing)
    {
        if (disposing)
        {
            logger.Information("Disposing application resources");
        
            running = false;
            UnloadContent();
        
            lock (resourceLock)
            {
                if (device != null)
                {
                    DestroyGPUDevice(device);
                    device = null;
                }
            
                if (window != null)
                {
                    DestroyWindow(window);
                    window = null;
                }
            }
        
            Quit();
            disposalSemaphore?.Dispose();
            Log.CloseAndFlush();
        }
    }
    /// <summary>
    ///  DISPOSAL
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore().ConfigureAwait(false);
        Dispose(false);
        GC.SuppressFinalize(this);
    }

    private async ValueTask DisposeAsyncCore()
    {
        await disposalSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            // Future: async cleanup for your advanced content system
        }
        finally
        {
            disposalSemaphore.Release();
        }
    }
}