#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using FizzleFramework2D.Configuration;
using FizzleFramework2D.Graphics.Shaders;
using FizzleFramework2D.Graphics.Textures;
using Hexa.NET.SDL3;
using Serilog;
using static Hexa.NET.SDL3.SDL;
using Log = Serilog.Log;

namespace FizzleFramework2D.Core
{
    public sealed class Application : IApplication
    {
        private static readonly ILogger logger = Log.ForContext<Application>();

        private readonly GameSettings settings;

        // SDL3 resources
        private unsafe SDLWindow* window;
        private unsafe SDLGPUDevice* device;

        // Managers
        private IShaderManager? shaderManager;
        private ITextureManager? textureManager;

        private ITexture2D[]? buttonTextures;
        private IShaderProgram? buttonShaderProgram;

        private unsafe SDLGPUBuffer* vertexBuffer;
        private unsafe SDLGPUSampler* textureSampler;

        // State management
        private volatile bool running;
        private bool initialized;
        private bool contentLoaded;
        private readonly SemaphoreSlim disposalSemaphore = new(1, 1);
        private readonly object resourceLock = new();

        public bool IsInitialized => initialized;
        public bool IsContentLoaded => contentLoaded;
        public bool IsRunning => running;

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

                    textureManager = new TextureManager(device, settings);
                    logger.Information("TextureManager initialized");

                    CreateRenderingResources();

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

        private unsafe void BindTexture(SDLGPURenderPass* pass, ITexture2D texture)
        {
            SDLGPUTextureSamplerBinding binding = new()
            {
                Texture =  texture.Handle,
                Sampler = textureSampler
            };
            ;
            BindGPUFragmentSamplers(pass, 0, &binding, 1);
        }

        private unsafe void CreateRenderingResources()
        {
            logger.Information("Creating rendering resources");

            // ✅ SOLUTION: Create vertex data for 3 separate buttons at different positions
            var vertexData = new float[]
            {
                // Button 0 - Left position (-0.8f to -0.2f on X-axis)
                // Position (vec3)           UV (vec2)     Color (vec4)
                -0.8f, -0.3f, 0.0f, 0.0f, 1.0f, 1.0f, 0.0f, 0.0f, 1.0f, // Bottom-left (red tint)
                -0.2f, -0.3f, 0.0f, 1.0f, 1.0f, 1.0f, 0.0f, 0.0f, 1.0f, // Bottom-right
                -0.2f, 0.3f, 0.0f, 1.0f, 0.0f, 1.0f, 0.0f, 0.0f, 1.0f, // Top-right

                -0.8f, -0.3f, 0.0f, 0.0f, 1.0f, 1.0f, 0.0f, 0.0f, 1.0f, // Bottom-left
                -0.2f, 0.3f, 0.0f, 1.0f, 0.0f, 1.0f, 0.0f, 0.0f, 1.0f, // Top-right
                -0.8f, 0.3f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, 1.0f, // Top-left

                // Button 1 - Center position (-0.3f to 0.3f on X-axis)
                -0.3f, -0.3f, 0.0f, 0.0f, 1.0f, 0.0f, 1.0f, 0.0f, 1.0f, // Bottom-left (green tint)
                0.3f, -0.3f, 0.0f, 1.0f, 1.0f, 0.0f, 1.0f, 0.0f, 1.0f, // Bottom-right
                0.3f, 0.3f, 0.0f, 1.0f, 0.0f, 0.0f, 1.0f, 0.0f, 1.0f, // Top-right

                -0.3f, -0.3f, 0.0f, 0.0f, 1.0f, 0.0f, 1.0f, 0.0f, 1.0f, // Bottom-left
                0.3f, 0.3f, 0.0f, 1.0f, 0.0f, 0.0f, 1.0f, 0.0f, 1.0f, // Top-right
                -0.3f, 0.3f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, 1.0f, // Top-left

                // Button 2 - Right position (0.2f to 0.8f on X-axis)
                0.2f, -0.3f, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, 1.0f, 1.0f, // Bottom-left (blue tint)
                0.8f, -0.3f, 0.0f, 1.0f, 1.0f, 0.0f, 0.0f, 1.0f, 1.0f, // Bottom-right
                0.8f, 0.3f, 0.0f, 1.0f, 0.0f, 0.0f, 0.0f, 1.0f, 1.0f, // Top-right

                0.2f, -0.3f, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, 1.0f, 1.0f, // Bottom-left
                0.8f, 0.3f, 0.0f, 1.0f, 0.0f, 0.0f, 0.0f, 1.0f, 1.0f, // Top-right
                0.2f, 0.3f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 1.0f, // Top-left
            };

            var vertexDataSize = vertexData.Length * sizeof(float);

            var bufferCreateInfo = new SDLGPUBufferCreateInfo
            {
                Usage = SDLGPUBufferUsageFlags.Vertex,
                Size = (uint)vertexDataSize
            };

            vertexBuffer = CreateGPUBuffer(device, &bufferCreateInfo);
            if (vertexBuffer == null)
            {
                throw new InvalidOperationException("Failed to create vertex buffer");
            }

            // Upload vertex data to GPU (keep existing upload code)
            var uploadCmd = AcquireGPUCommandBuffer(device);
            var copyPass = BeginGPUCopyPass(uploadCmd);

            var transferBufferCreateInfo = new SDLGPUTransferBufferCreateInfo
            {
                Usage = SDLGPUTransferBufferUsage.Upload,
                Size = (uint)vertexDataSize
            };

            var transferBuffer = CreateGPUTransferBuffer(device, &transferBufferCreateInfo);
            var mappedData = MapGPUTransferBuffer(device, transferBuffer, false);

            fixed (float* verticesPtr = vertexData)
            {
                Buffer.MemoryCopy(verticesPtr, mappedData, vertexDataSize, vertexDataSize);
            }

            UnmapGPUTransferBuffer(device, transferBuffer);

            var bufferTransferInfo = new SDLGPUTransferBufferLocation
            {
                TransferBuffer = transferBuffer,
                Offset = 0
            };

            var bufferRegion = new SDLGPUBufferRegion
            {
                Buffer = vertexBuffer,
                Offset = 0,
                Size = (uint)vertexDataSize
            };

            UploadToGPUBuffer(copyPass, &bufferTransferInfo, &bufferRegion, false);
            EndGPUCopyPass(copyPass);
            SubmitGPUCommandBuffer(uploadCmd);
            WaitForGPUIdle(device);
            ReleaseGPUTransferBuffer(device, transferBuffer);

            // Create texture sampler (keep existing code)
            var samplerCreateInfo = new SDLGPUSamplerCreateInfo
            {
                MinFilter = SDLGPUFilter.Linear,
                MagFilter = SDLGPUFilter.Linear,
                MipmapMode = SDLGPUSamplerMipmapMode.Linear,
                AddressModeU = SDLGPUSamplerAddressMode.ClampToEdge,
                AddressModeV = SDLGPUSamplerAddressMode.ClampToEdge,
                AddressModeW = SDLGPUSamplerAddressMode.ClampToEdge
            };

            textureSampler = CreateGPUSampler(device, &samplerCreateInfo);
            if (textureSampler == null)
            {
                throw new InvalidOperationException("Failed to create texture sampler");
            }

            logger.Information("✅ Rendering resources created successfully");
        }

        public void LoadContent()
        {
            if (!initialized || shaderManager == null || textureManager == null)
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
                // ✅ CRITICAL FIX: Use synchronous loading to avoid race conditions
                var loadTask = LoadContentAsync();
                loadTask.Wait(); // Wait for completion before continuing

                contentLoaded = true;
                logger.Information("✅ Content loading complete - 3 button textures loaded successfully!");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Content loading failed");
                throw;
            }
        }

        private async Task LoadContentAsync()
        {
            if (shaderManager == null || textureManager == null) return;

            // Load shaders
            logger.Information("Loading vertex shader...");
            await shaderManager.LoadShaderAsync("button", SDLGPUShaderStage.Vertex);

            logger.Information("Loading fragment shader...");
            await shaderManager.LoadShaderAsync("button", SDLGPUShaderStage.Fragment);

            // Create shader program
            logger.Information("Creating shader program...");
            buttonShaderProgram = await shaderManager.CreateProgramAsync("button", "button");
            logger.Information("Shader program created: {ProgramName}", buttonShaderProgram.Name);

            // Load 3 individual button textures into array
            logger.Information("Loading button textures...");
            buttonTextures = new ITexture2D[3];

            buttonTextures[0] = await textureManager.LoadTextureAsync("btn0.png");
            logger.Information("✅ Loaded btn0.png: {Width}x{Height}",
                buttonTextures[0].Width, buttonTextures[0].Height);

            buttonTextures[1] = await textureManager.LoadTextureAsync("btn1.png");
            logger.Information("✅ Loaded btn1.png: {Width}x{Height}",
                buttonTextures[1].Width, buttonTextures[1].Height);

            buttonTextures[2] = await textureManager.LoadTextureAsync("btn2.png");
            logger.Information("✅ Loaded btn2.png: {Width}x{Height}",
                buttonTextures[2].Width, buttonTextures[2].Height);
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
            if (buttonShaderProgram == null || buttonTextures == null ||
                vertexBuffer == null || textureSampler == null)
                return;

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
                    // ✅ SOLUTION: Render each button using different vertex ranges
                    RenderButtonAtIndex(pass, 0); // Button 0 (vertices 0-5) - Red tinted
                    RenderButtonAtIndex(pass, 1); // Button 1 (vertices 6-11) - Green tinted
                    RenderButtonAtIndex(pass, 2); // Button 2 (vertices 12-17) - Blue tinted

                    EndGPURenderPass(pass);
                }

                SubmitGPUCommandBuffer(cmd);
            }
        }

        private unsafe void RenderButtonAtIndex(SDLGPURenderPass* renderPass, int buttonIndex)
        {
            // Bind shader program
            BindGPUGraphicsPipeline(renderPass, buttonShaderProgram!.Pipeline);

            // Bind vertex buffer
            var vertexBinding = new SDLGPUBufferBinding
            {
                Buffer = vertexBuffer,
                Offset = 0
            };
            BindGPUVertexBuffers(renderPass, 0, &vertexBinding, 1);
            BindTexture(renderPass, buttonTextures![buttonIndex]);

            // ✅ SOLUTION: Draw specific button using vertex offset
            // Each button uses 6 vertices (2 triangles), starting at buttonIndex * 6
            var firstVertex = (uint)(buttonIndex * 6);
            DrawGPUPrimitives(renderPass, 6, 1, firstVertex, 0);

            logger.Verbose("Rendered button index: {ButtonIndex} starting at vertex {FirstVertex}",
                buttonIndex, firstVertex);
        }

        public void UnloadContent()
        {
            lock (resourceLock)
            {
                unsafe
                {
                    // Cleanup rendering resources
                    if (vertexBuffer != null)
                    {
                        ReleaseGPUBuffer(device, vertexBuffer);
                        vertexBuffer = null;
                    }

                    if (textureSampler != null)
                    {
                        ReleaseGPUSampler(device, textureSampler);
                        textureSampler = null;
                    }
                }

                buttonTextures = null;
                buttonShaderProgram = null;

                // ✅ CRITICAL FIX: Dispose managers in correct order
                if (textureManager != null)
                {
                    logger.Information("Disposing texture manager");
                    textureManager.Dispose();
                    textureManager = null;
                }

                if (shaderManager != null)
                {
                    logger.Information("Disposing shader manager");
                    shaderManager.Dispose();
                    shaderManager = null;
                }

                contentLoaded = false;
            }
        }

        private unsafe void Dispose(bool disposing)
        {
            if (disposing)
            {
                logger.Information("Disposing application resources");

                running = false;

                // ✅ CRITICAL FIX: Ensure all content is unloaded before disposing device
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
}