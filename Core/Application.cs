#nullable enable
using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using FizzleFramework2D.Configuration;
using FizzleFramework2D.Graphics.Rendering;
using FizzleFramework2D.Graphics.Shaders;
using FizzleFramework2D.Graphics.Shapes;
using FizzleFramework2D.Graphics.Textures;
using Hexa.NET.SDL3;
using Serilog;
using static Hexa.NET.SDL3.SDL;
using Log = Serilog.Log;

namespace FizzleFramework2D.Core
{
    /// <summary>
    /// Main game/application entry-point using SpriteBatch rendering system.
    /// </summary>
    public sealed class Application : IApplication
    {
        private static readonly ILogger logger = Log.ForContext<Application>();

        private readonly GameSettings settings;

        // SDL objects
        private unsafe SDLWindow* window;
        private unsafe SDLGPUDevice* device;
        private unsafe SDLGPUSampler* defaultSampler;

        // Content managers
        private IShaderManager? shaderManager;
        private ITextureManager? textureManager;

        // Runtime resources
        private ITexture2D[]? buttonTextures;
        private IShaderProgram? buttonProgram;
        private SpriteBatch? spriteBatch;

        // Legacy static vertex buffer (can be removed once fully migrated to SpriteBatch)
        private unsafe SDLGPUBuffer* vertexBuffer;

        // State
        private volatile bool running;
        private bool initialized;
        private bool contentLoaded;
        private readonly SemaphoreSlim disposalSem = new(1, 1);
        private readonly object renderLock = new();

        #region Button Tinting Colors

        private static readonly Vector4 NormalTint = new(1f, 1f, 1f, 1f); // White (normal)
        private static readonly Vector4 HoverTint = new(0.8f, 0.8f, 0.8f, 1f); // Light gray (hover)
        private static readonly Vector4 PressedTint = new(0.6f, 0.6f, 0.6f, 1f); // Dark gray (pressed)

        #endregion

        public bool IsInitialized => initialized;
        public bool IsContentLoaded => contentLoaded;
        public bool IsRunning => running;

        public Application(GameSettings settings)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            logger.Debug("Application instance constructed.");
        }

        #region Initialization / Shutdown

        public unsafe bool Initialize()
        {
            logger.Information("Initializing SDL and GPU device …");
            try
            {
                if (!Init(SDLInitFlags.Video))
                {
                    logger.Error("SDL_Init failed: {Error}", GetError()->ToString());
                    return false;
                }

                window = CreateWindow(settings.Window.Title,
                    settings.Window.Width,
                    settings.Window.Height,
                    settings.Window.Resizable ? SDLWindowFlags.Resizable : 0);
                if (window == null)
                {
                    logger.Error("SDL_CreateWindow failed: {Error}", GetError()->ToString());
                    return false;
                }

                device = CreateGPUDevice(settings.Rendering.ShaderFormats, true, (byte*)null);
                if (device == null)
                {
                    logger.Error("SDL_CreateGPUDevice failed: {Error}", GetError()->ToString());
                    return false;
                }

                if (!ClaimWindowForGPUDevice(device, window))
                {
                    logger.Error("SDL_ClaimWindowForGPUDevice failed.");
                    return false;
                }

                SetGPUSwapchainParameters(device, window,
                    SDLGPUSwapchainComposition.Sdr,
                    settings.Rendering.VSync ? SDLGPUPresentMode.Vsync : SDLGPUPresentMode.Immediate);

                CreateDefaultSampler();
                CreateManagers();
                CreateRenderingResources();

                initialized = true;
                logger.Information("Initialization complete.");
                return true;
            }
            catch (Exception ex)
            {
                logger.Fatal(ex, "Initialization exception.");
                return false;
            }
        }

        private unsafe void CreateManagers()
        {
            shaderManager = new ShaderManager(device, settings);
            textureManager = new TextureManager(device, settings);
        }

        private unsafe void CreateDefaultSampler()
        {
            var ci = new SDLGPUSamplerCreateInfo
            {
                MinFilter = SDLGPUFilter.Linear,
                MagFilter = SDLGPUFilter.Linear,
                MipmapMode = SDLGPUSamplerMipmapMode.Linear,
                AddressModeU = SDLGPUSamplerAddressMode.ClampToEdge,
                AddressModeV = SDLGPUSamplerAddressMode.ClampToEdge,
                AddressModeW = SDLGPUSamplerAddressMode.ClampToEdge,
                MipLodBias = 0,
                EnableAnisotropy = 0,
                MaxAnisotropy = 1,
                EnableCompare = 0,
                CompareOp = SDLGPUCompareOp.Always,
                MinLod = 0,
                MaxLod = 1000
            };

            defaultSampler = CreateGPUSampler(device, &ci);
            if (defaultSampler == null)
                throw new InvalidOperationException($"Failed creating default sampler: {GetError()->ToString()}");
        }

        #endregion

        #region Content Loading

        public void LoadContent()
        {
            if (!initialized)
            {
                logger.Warning("LoadContent called before Initialize.");
                return;
            }

            logger.Information("Loading game content …");
            try
            {
                LoadContentAsync().Wait();
                contentLoaded = true;
                logger.Information("Content loaded.");
            }
            catch (Exception ex)
            {
                logger.Fatal(ex, "Content loading failed.");
                throw;
            }
        }

        private async Task LoadContentAsync()
        {
            if (shaderManager == null || textureManager == null)
                throw new InvalidOperationException("Managers not ready.");

            // Load shaders
            await shaderManager.LoadShaderAsync("button", SDLGPUShaderStage.Vertex);
            await shaderManager.LoadShaderAsync("button", SDLGPUShaderStage.Fragment);
            buttonProgram = await shaderManager.CreateProgramAsync("button", "button");

            // Load textures
            buttonTextures = new[]
            {
                await textureManager.LoadTextureAsync("btn0.png"),
                await textureManager.LoadTextureAsync("btn1.png"),
                await textureManager.LoadTextureAsync("btn2.png")
            };
            unsafe
            {
                // Initialize SpriteBatch after shaders and textures are loaded
                spriteBatch = new SpriteBatch(device, buttonProgram, defaultSampler, settings);
                logger.Information("SpriteBatch initialized successfully");
            }
        }

        #endregion

        #region GPU Resources (Legacy - Can be removed once fully migrated to SpriteBatch)

        private unsafe void CreateRenderingResources()
        {
            logger.Information("Creating legacy vertex buffer for static buttons…");

            // Keep only for legacy compatibility - can be removed once fully migrated to SpriteBatch
            float[] vertices =
            {
                // Button 0
                -0.8f, -0.3f, 0f, 0f, 0f, 1f, 1f, 1f, 1f,
                -0.2f, -0.3f, 0f, 1f, 0f, 1f, 1f, 1f, 1f,
                -0.2f, 0.3f, 0f, 1f, 1f, 1f, 1f, 1f, 1f,
                -0.8f, -0.3f, 0f, 0f, 0f, 1f, 1f, 1f, 1f,
                -0.2f, 0.3f, 0f, 1f, 1f, 1f, 1f, 1f, 1f,
                -0.8f, 0.3f, 0f, 0f, 1f, 1f, 1f, 1f, 1f,

                // Button 1
                -0.3f, -0.3f, 0f, 0f, 0f, 1f, 1f, 1f, 1f,
                0.3f, -0.3f, 0f, 1f, 0f, 1f, 1f, 1f, 1f,
                0.3f, 0.3f, 0f, 1f, 1f, 1f, 1f, 1f, 1f,
                -0.3f, -0.3f, 0f, 0f, 0f, 1f, 1f, 1f, 1f,
                0.3f, 0.3f, 0f, 1f, 1f, 1f, 1f, 1f, 1f,
                -0.3f, 0.3f, 0f, 0f, 1f, 1f, 1f, 1f, 1f,

                // Button 2
                0.2f, -0.3f, 0f, 0f, 0f, 1f, 1f, 1f, 1f,
                0.8f, -0.3f, 0f, 1f, 0f, 1f, 1f, 1f, 1f,
                0.8f, 0.3f, 0f, 1f, 1f, 1f, 1f, 1f, 1f,
                0.2f, -0.3f, 0f, 0f, 0f, 1f, 1f, 1f, 1f,
                0.8f, 0.3f, 0f, 1f, 1f, 1f, 1f, 1f, 1f,
                0.2f, 0.3f, 0f, 0f, 1f, 1f, 1f, 1f, 1f,
            };

            uint vertexDataSize = (uint)(vertices.Length * sizeof(float));
            var vbCreateInfo = new SDLGPUBufferCreateInfo
            {
                Usage = SDLGPUBufferUsageFlags.Vertex,
                Size = vertexDataSize
            };

            vertexBuffer = CreateGPUBuffer(device, &vbCreateInfo);
            if (vertexBuffer == null)
                throw new InvalidOperationException("Failed to create button vertex buffer.");

            // Upload vertex data
            var stagingCreateInfo = new SDLGPUTransferBufferCreateInfo
            {
                Usage = SDLGPUTransferBufferUsage.Upload,
                Size = vertexDataSize
            };

            var staging = CreateGPUTransferBuffer(device, &stagingCreateInfo);
            if (staging == null)
                throw new InvalidOperationException("Failed to create staging buffer for button vertices.");

            float* dst = (float*)MapGPUTransferBuffer(device, staging, false);
            fixed (float* src = vertices)
                Buffer.MemoryCopy(src, dst, vertexDataSize, vertexDataSize);
            UnmapGPUTransferBuffer(device, staging);

            var cmd = AcquireGPUCommandBuffer(device);
            var copy = BeginGPUCopyPass(cmd);
            var srcLoc = new SDLGPUTransferBufferLocation { TransferBuffer = staging, Offset = 0 };
            var dstReg = new SDLGPUBufferRegion { Buffer = vertexBuffer, Offset = 0, Size = vertexDataSize };
            UploadToGPUBuffer(copy, &srcLoc, &dstReg, false);
            EndGPUCopyPass(copy);
            SubmitGPUCommandBuffer(cmd);
            WaitForGPUIdle(device);
            ReleaseGPUTransferBuffer(device, staging);
        }

        #endregion

        #region Main Loop

        public void Run()
        {
            if (!initialized || !contentLoaded)
            {
                logger.Error("Run called without initialization or content.");
                return;
            }

            running = true;
            logger.Information("Entering main loop.");
            while (running)
            {
                PollEvents();
                Render();
                Thread.Sleep(16); // ~60 FPS
            }
        }

        private unsafe void PollEvents()
        {
            SDLEvent e;
            while (PollEvent(&e))
            {
                switch ((SDLEventType)e.Type)
                {
                    case SDLEventType.Quit:
                        running = false;
                        break;
                    case SDLEventType.KeyDown when e.Key.Key == SDLK_ESCAPE:
                        running = false;
                        break;
                }
            }
        }

        #endregion

        #region Rendering - New SpriteBatch System

        private unsafe void Render()
        {
            if (buttonProgram == null || buttonTextures == null || spriteBatch == null)
                return;

            lock (renderLock)
            {
                SDLGPUTexture* backbuffer;
                uint w, h;
                var cmd = AcquireGPUCommandBuffer(device);
                if (!WaitAndAcquireGPUSwapchainTexture(cmd, window, &backbuffer, &w, &h))
                    return;

                var target = new SDLGPUColorTargetInfo
                {
                    Texture = backbuffer,
                    LoadOp = SDLGPULoadOp.Clear,
                    StoreOp = SDLGPUStoreOp.Store,
                    ClearColor = new SDLFColor { R = 0.1f, G = 0.1f, B = 0.1f, A = 1f }
                };

                var pass = BeginGPURenderPass(cmd, &target, 1, null);
                if (pass != null)
                {
                    // Use SpriteBatch for all rendering
                    spriteBatch.Begin();

                    // Draw your UI elements using the new MonoGame-style API
                    DrawUIElements();

                    spriteBatch.End(pass);
                    EndGPURenderPass(pass);
                }

                SubmitGPUCommandBuffer(cmd);
            }
        }

        /// <summary>
        /// MonoGame-style UI drawing using SpriteBatch - Easy texture positioning with Rectangle!
        /// </summary>
        private void DrawUIElements()
        {
            if (buttonTextures == null) return;

            // Calculate button layout
            int buttonWidth = buttonTextures[0].Width / 4;   // Quarter size
            int buttonHeight = buttonTextures[0].Height / 4;
            int buttonSpacing = 50;
            int startX = 100;
            int startY = 200;

            // FIXED: Draw all buttons within the same Begin/End block
            // Each Draw() call adds to the batch - they'll all be rendered when End() is called
            for (int i = 0; i < buttonTextures.Length; i++)
            {
                var buttonRect = new Rectangle(
                    startX + i * (buttonWidth + buttonSpacing), 
                    startY, 
                    buttonWidth, 
                    buttonHeight
                );
        
                Vector4 tint = i switch
                {
                    0 => NormalTint,   // Normal state
                    1 => HoverTint,    // Hover state  
                    2 => PressedTint,  // Pressed state
                    _ => NormalTint
                };
        
                // This adds each sprite to the batch
                spriteBatch!.Draw(buttonTextures[i], buttonRect, tint);
            }
    
            // All sprites will be rendered when spriteBatch.End() is called in Render()
        }

        #endregion

        #region Cleanup / Disposal

        public unsafe void UnloadContent()
        {
            lock (renderLock)
            {
                // Dispose SpriteBatch first
                spriteBatch?.Dispose();
                spriteBatch = null;

                // Clean up legacy vertex buffer (can be removed once fully migrated)
                if (vertexBuffer != null)
                {
                    ReleaseGPUBuffer(device, vertexBuffer);
                    vertexBuffer = null;
                }

                if (defaultSampler != null)
                {
                    ReleaseGPUSampler(device, defaultSampler);
                    defaultSampler = null;
                }

                if (buttonTextures != null)
                {
                    foreach (var tex in buttonTextures)
                        tex.Dispose();
                    buttonTextures = null;
                }

                buttonProgram?.Dispose();
                buttonProgram = null;

                textureManager?.Dispose();
                shaderManager?.Dispose();
                textureManager = null;
                shaderManager = null;

                contentLoaded = false;
            }
        }

        private unsafe void DestroyDeviceAndWindow()
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

        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                running = false;
                UnloadContent();
                DestroyDeviceAndWindow();
                Quit();
                disposalSem.Dispose();
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
            await disposalSem.WaitAsync().ConfigureAwait(false);
            try
            {
                Dispose(false);
            }
            finally
            {
                disposalSem.Release();
            }

            GC.SuppressFinalize(this);
        }

        #endregion

        #region Public API - For accessing textures and easy drawing

        /// <summary>
        /// Get a button texture by index for external use
        /// </summary>
        public ITexture2D? GetButtonTexture(int index)
        {
            return buttonTextures != null && index >= 0 && index < buttonTextures.Length
                ? buttonTextures[index]
                : null;
        }

        /// <summary>
        /// Example method showing how to use SpriteBatch externally
        /// </summary>
        public void DrawTextureAt(ITexture2D texture, int x, int y, int? width = null, int? height = null,
            Vector4? tint = null)
        {
            if (spriteBatch == null) return;

            var rect = new Rectangle(x, y, width ?? texture.Width, height ?? texture.Height);
            spriteBatch.Draw(texture, rect, tint ?? Vector4.One);
        }

        #endregion
    }
}