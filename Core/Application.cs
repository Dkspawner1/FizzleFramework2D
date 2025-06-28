using System;
using System.Threading;
using System.Threading.Tasks;
using FizzleFramework2D.Configuration;
using FizzleFramework2D.Graphics.Shaders;
using Hexa.NET.SDL3;
using Serilog;
using static Hexa.NET.SDL3.SDL;
using Log = Serilog.Log;

namespace FizzleFramework2D.Core;
    public sealed class Application : IApplication
    {
        private static readonly ILogger logger = Log.ForContext<Application>();

        private readonly GameSettings settings;

        // SDL3 resources
        private unsafe SDLWindow* window;
        private unsafe SDLGPUDevice* device;

        // Managers
        private IShaderManager? shaderManager;

        // State management
        private volatile bool running;
        private bool initialized;
        private bool contentLoaded;
        private readonly SemaphoreSlim disposalSemaphore = new(1, 1);
        private readonly object resourceLock = new();

        public bool IsInitialized => initialized;
        public bool IsContentLoaded => contentLoaded;
        public bool IsRunning => running;

        // ✅ Fixed: Remove 'ref' from constructor parameter
        public Application(GameSettings settings)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            logger.Debug("Application created with configuration");
        }

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
                    window = CreateWindow(settings.Window.Title, settings.Window.Width, settings.Window.Height,
                        settings.Window.Resizable ? SDLWindowFlags.Resizable : 0);

                    if (window == null)
                    {
                        logger.Error("Window creation failed: {Error}", GetError()->ToString());
                        return false;
                    }

                    device = CreateGPUDevice(settings.Rendering.ShaderFormats, true, (byte*)null);
                    if (device == null)
                    {
                        logger.Error("Device creation failed: {Error}", GetError()->ToString());
                        return false;
                    }

                    if (!ClaimWindowForGPUDevice(device, window))
                    {
                        logger.Error("Failed to claim window for GPU device");
                        return false;
                    }

                    SetGPUSwapchainParameters(device, window, 
                        SDLGPUSwapchainComposition.Sdr,
                        settings.Rendering.VSync ? SDLGPUPresentMode.Vsync : SDLGPUPresentMode.Immediate);
                    
                    var swapchainFormat = GetGPUSwapchainTextureFormat(device, window);
                    logger.Information("Swapchain format: {Format}", swapchainFormat);
                    
                    shaderManager = new ShaderManager(device, settings);
                    logger.Information("ShaderManager initialized");
                    
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

        // ✅ Fixed: Changed from 'async void' to 'async Task' and make it synchronous for now
        public void LoadContent()
        {
            if (!initialized || shaderManager == null) 
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
                // ✅ Fixed: Use synchronous loading to avoid threading issues
                var loadTask = LoadContentAsync();
                loadTask.Wait(); // Wait for completion before continuing
                
                contentLoaded = true;
                logger.Information("Content loading complete");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Content loading failed");
                throw;
            }
        }

        // ✅ New: Separate async method for actual loading
        private async Task LoadContentAsync()
        {
            if (shaderManager == null) return;

            // Load basic shaders - these files should exist in assets/shaders/
            logger.Information("Loading vertex shader...");
            await shaderManager.LoadShaderAsync("button", SDLGPUShaderStage.Vertex);
                
            logger.Information("Loading fragment shader...");
            await shaderManager.LoadShaderAsync("button", SDLGPUShaderStage.Fragment);
                
            // Create a shader program
            logger.Information("Creating shader program...");
            var buttonProgram = await shaderManager.CreateProgramAsync("button", "button");
                
            logger.Information("Shader program created: {ProgramName}", buttonProgram.Name);
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
                        logger.Information("KeyDown event received");
                        if (e.Key.Key == SDLK_ESCAPE)
                        {
                            logger.Information("Escape pressed - exiting");
                            running = false;
                        }
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
                    // TODO: Use shader programs for actual rendering here
                    // Example: var program = shaderManager?.GetProgram("button_button");
                    // if (program != null) { /* bind and use shader program */ }
                        
                    EndGPURenderPass(pass);
                }
                SubmitGPUCommandBuffer(cmd);
            }
        }

        public void UnloadContent()
        {
            // ✅ Fixed: Proper disposal order - dispose shaders before device
            if (shaderManager != null)
            {
                logger.Information("Disposing shader manager");
                shaderManager.Dispose();
                shaderManager = null;
            }
            contentLoaded = false;
        }

        private unsafe void Dispose(bool disposing)
        {
            if (disposing)
            {
                logger.Information("Disposing application resources");
                
                running = false;
                
                // ✅ Fixed: Ensure UnloadContent is called first to dispose shaders
                UnloadContent();
                
                lock (resourceLock)
                {
                    // ✅ Fixed: Device disposal order is correct (after shaders are disposed)
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
