#nullable enable
using System;
using System.Buffers;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using FizzleFramework2D.Configuration;
using FizzleFramework2D.Graphics.Shaders;
using FizzleFramework2D.Graphics.Textures;
using Hexa.NET.SDL3;
using Serilog;
using static Hexa.NET.SDL3.SDL;
using Log = Serilog.Log;

namespace FizzleFramework2D.Core;

    /// <summary>
    /// Main game/application entry-point.
    /// </summary>
    public sealed class Application : IApplication
    {
        private static readonly ILogger logger = Log.ForContext<Application>();

        private readonly GameSettings settings;

        // SDL objects
        private unsafe SDLWindow*          window;
        private unsafe SDLGPUDevice*       device;
        private unsafe SDLGPUSampler*      defaultSampler;

        // Content managers
        private IShaderManager?  shaderManager;
        private ITextureManager? textureManager;

        // Runtime resources
        private ITexture2D[]?     buttonTextures;
        private IShaderProgram?   buttonProgram;
        private unsafe SDLGPUBuffer*     vertexBuffer;

        // State
        private volatile bool running;
        private bool initialized;
        private bool contentLoaded;
        private readonly SemaphoreSlim disposalSem = new(1, 1);
        private readonly object renderLock = new();

        #region Compile-time constants for button tinting
        private static readonly Vector4 NormalTint  = new(1f, 1f, 1f, 1f);  // white
        private static readonly Vector4 HoverTint   = new(0.60f, 0.60f, 0.60f, 1f);
        private static readonly Vector4 PressedTint = new(0.80f, 0.80f, 0.80f, 1f);
        #endregion

        public bool IsInitialized   => initialized;
        public bool IsContentLoaded => contentLoaded;
        public bool IsRunning       => running;

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
            shaderManager  = new ShaderManager(device, settings);
            textureManager = new TextureManager(device, settings);
        }

        private unsafe void CreateDefaultSampler()
        {
            var ci = new SDLGPUSamplerCreateInfo
            {
                MinFilter     = SDLGPUFilter.Linear,
                MagFilter     = SDLGPUFilter.Linear,
                MipmapMode    = SDLGPUSamplerMipmapMode.Linear,
                AddressModeU  = SDLGPUSamplerAddressMode.ClampToEdge,
                AddressModeV  = SDLGPUSamplerAddressMode.ClampToEdge,
                AddressModeW  = SDLGPUSamplerAddressMode.ClampToEdge,
                MipLodBias    = 0,
                EnableAnisotropy = 0,
                MaxAnisotropy = 1,
                EnableCompare = 0,
                CompareOp     = SDLGPUCompareOp.Always,
                MinLod        = 0,
                MaxLod        = 1000
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

            await shaderManager.LoadShaderAsync("button", SDLGPUShaderStage.Vertex);
            await shaderManager.LoadShaderAsync("button", SDLGPUShaderStage.Fragment);
            buttonProgram = await shaderManager.CreateProgramAsync("button", "button");

            buttonTextures = new[]
            {
                await textureManager.LoadTextureAsync("btn0.png"),
                await textureManager.LoadTextureAsync("btn1.png"),
                await textureManager.LoadTextureAsync("btn2.png")
            };
        }
        #endregion

        #region GPU Resources
        private unsafe void CreateRenderingResources()
        {
            logger.Information("Creating vertex buffer …");

            // FIXED: Corrected texture coordinates for SDL3 GPU (top-left origin)
            float[] vertices =
            {
                // position            uv      color (WHITE for normal tinting)
                // Button 0 - CORRECTED UV coordinates
                -0.8f,-0.3f,0, 0,0, 1,1,1,1,  // top-left: (0,0)
                -0.2f,-0.3f,0, 1,0, 1,1,1,1,  // top-right: (1,0)  
                -0.2f, 0.3f,0, 1,1, 1,1,1,1,  // bottom-right: (1,1)
                -0.8f,-0.3f,0, 0,0, 1,1,1,1,  // top-left: (0,0)
                -0.2f, 0.3f,0, 1,1, 1,1,1,1,  // bottom-right: (1,1)
                -0.8f, 0.3f,0, 0,1, 1,1,1,1,  // bottom-left: (0,1)

                // Button 1 - CORRECTED UV coordinates  
                -0.3f,-0.3f,0, 0,0, 1,1,1,1,  // top-left: (0,0)
                0.3f,-0.3f,0, 1,0, 1,1,1,1,  // top-right: (1,0)
                0.3f, 0.3f,0, 1,1, 1,1,1,1,  // bottom-right: (1,1)
                -0.3f,-0.3f,0, 0,0, 1,1,1,1,  // top-left: (0,0)
                0.3f, 0.3f,0, 1,1, 1,1,1,1,  // bottom-right: (1,1)
                -0.3f, 0.3f,0, 0,1, 1,1,1,1,  // bottom-left: (0,1)

                // Button 2 - CORRECTED UV coordinates
                0.2f,-0.3f,0, 0,0, 1,1,1,1,  // top-left: (0,0)
                0.8f,-0.3f,0, 1,0, 1,1,1,1,  // top-right: (1,0)
                0.8f, 0.3f,0, 1,1, 1,1,1,1,  // bottom-right: (1,1)
                0.2f,-0.3f,0, 0,0, 1,1,1,1,  // top-left: (0,0)
                0.8f, 0.3f,0, 1,1, 1,1,1,1,  // bottom-right: (1,1)
                0.2f, 0.3f,0, 0,1, 1,1,1,1,  // bottom-left: (0,1)
            };


            uint size = (uint)(vertices.Length * sizeof(float));

            var bc = new SDLGPUBufferCreateInfo
            {
                Usage = SDLGPUBufferUsageFlags.Vertex,
                Size  = size
            };
            vertexBuffer = CreateGPUBuffer(device, &bc);
            if (vertexBuffer == null)
                throw new InvalidOperationException("Vertex buffer creation failed.");

            // Upload
            var transferInfo = new SDLGPUTransferBufferCreateInfo
            {
                Usage = SDLGPUTransferBufferUsage.Upload,
                Size  = size
            };
            var staging = CreateGPUTransferBuffer(device, &transferInfo);
            float* dst  = (float*)MapGPUTransferBuffer(device, staging, false);
            fixed (float* src = vertices) Buffer.MemoryCopy(src, dst, size, size);
            UnmapGPUTransferBuffer(device, staging);

            var cmd   = AcquireGPUCommandBuffer(device);
            var copy  = BeginGPUCopyPass(cmd);

            var srcLoc = new SDLGPUTransferBufferLocation { TransferBuffer = staging, Offset = 0 };
            var dstReg = new SDLGPUBufferRegion            { Buffer = vertexBuffer, Offset = 0, Size = size };
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
                Thread.Sleep(16);
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

        #region Rendering
        private unsafe void Render()
        {
            if (buttonProgram == null || buttonTextures == null || vertexBuffer == null)
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
                    Texture     = backbuffer,
                    LoadOp      = SDLGPULoadOp.Clear,
                    StoreOp     = SDLGPUStoreOp.Store,
                    ClearColor  = new SDLFColor { R = 0.1f, G = 0.1f, B = 0.1f, A = 1f }
                };

                var pass = BeginGPURenderPass(cmd, &target, 1, null);
                if (pass != null)
                {
                    DrawButtons(pass);
                    EndGPURenderPass(pass);
                }

                SubmitGPUCommandBuffer(cmd);
            }
        }

        private unsafe void DrawButtons(SDLGPURenderPass* pass)
        {
            BindGPUGraphicsPipeline(pass, buttonProgram!.Pipeline);

            var vb = new SDLGPUBufferBinding { Buffer = vertexBuffer, Offset = 0 };
            BindGPUVertexBuffers(pass, 0, &vb, 1);

            for (int i = 0; i < buttonTextures!.Length; i++)
            {
                var ts = new SDLGPUTextureSamplerBinding
                {
                    Texture = buttonTextures[i].Handle,
                    Sampler = defaultSampler
                };
                BindGPUFragmentSamplers(pass, 0, &ts, 1); // set 0, slot 0[10][19]

                uint first = (uint)(i * 6);
                DrawGPUPrimitives(pass, 6, 1, first, 0);
            }
        }
        #endregion

        #region Cleanup / Disposal
        public unsafe void UnloadContent()
        {
            lock (renderLock)
            {
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
                shaderManager  = null;

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
    }

