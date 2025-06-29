#nullable enable
using System;
using System.Buffers;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using FizzleFramework2D.Configuration;
using FizzleFramework2D.Graphics.Shaders;
using FizzleFramework2D.Graphics.Shapes;
using FizzleFramework2D.Graphics.Textures;
using Hexa.NET.SDL3;
using Serilog;
using static Hexa.NET.SDL3.SDL;
using Log = Serilog.Log;

// ReSharper disable All

namespace FizzleFramework2D.Core;

/// <summary>
/// Main game/application entry-point.
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
    private unsafe SDLGPUBuffer* vertexBuffer;
    private unsafe SDLGPUBuffer* quadVertexBuffer;
    private unsafe SDLGPUTransferBuffer* quadStagingBuffer;

    // State
    private volatile bool running;
    private bool initialized;
    private bool contentLoaded;
    private readonly SemaphoreSlim disposalSem = new(1, 1);
    private readonly object renderLock = new();

    #region Compile-time constants for button tinting

    private static readonly Vector4 NormalTint = new(1f, 1f, 1f, 1f); // white
    private static readonly Vector4 HoverTint = new(0.60f, 0.60f, 0.60f, 1f);
    private static readonly Vector4 PressedTint = new(0.80f, 0.80f, 0.80f, 1f);

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

/// <summary>
/// Create both the static button vertex buffer and a dynamic quad buffer for DrawTexture.
/// </summary>
private unsafe void CreateRenderingResources()
{
    logger.Information("Creating vertex buffer …");

    // --- Static button vertex buffer ---

    // Vertex array: position.xyz, uv.xy, color.rgba (6 verts per button × 3 buttons)
    float[] vertices =
    {
        // Button 0
        -0.8f, -0.3f, 0f,   0f, 0f,   1f,1f,1f,1f,
        -0.2f, -0.3f, 0f,   1f, 0f,   1f,1f,1f,1f,
        -0.2f,  0.3f, 0f,   1f, 1f,   1f,1f,1f,1f,
        -0.8f, -0.3f, 0f,   0f, 0f,   1f,1f,1f,1f,
        -0.2f,  0.3f, 0f,   1f, 1f,   1f,1f,1f,1f,
        -0.8f,  0.3f, 0f,   0f, 1f,   1f,1f,1f,1f,

        // Button 1
        -0.3f, -0.3f, 0f,   0f, 0f,   1f,1f,1f,1f,
         0.3f, -0.3f, 0f,   1f, 0f,   1f,1f,1f,1f,
         0.3f,  0.3f, 0f,   1f, 1f,   1f,1f,1f,1f,
        -0.3f, -0.3f, 0f,   0f, 0f,   1f,1f,1f,1f,
         0.3f,  0.3f, 0f,   1f, 1f,   1f,1f,1f,1f,
        -0.3f,  0.3f, 0f,   0f, 1f,   1f,1f,1f,1f,

        // Button 2
         0.2f, -0.3f, 0f,   0f, 0f,   1f,1f,1f,1f,
         0.8f, -0.3f, 0f,   1f, 0f,   1f,1f,1f,1f,
         0.8f,  0.3f, 0f,   1f, 1f,   1f,1f,1f,1f,
         0.2f, -0.3f, 0f,   0f, 0f,   1f,1f,1f,1f,
         0.8f,  0.3f, 0f,   1f, 1f,   1f,1f,1f,1f,
         0.2f,  0.3f, 0f,   0f, 1f,   1f,1f,1f,1f,
    };

    // Compute size in bytes and create GPU buffer
    uint vertexDataSize = (uint)(vertices.Length * sizeof(float));
    var vbCreateInfo = new SDLGPUBufferCreateInfo
    {
        Usage = SDLGPUBufferUsageFlags.Vertex,
        Size  = vertexDataSize
    };

    vertexBuffer = CreateGPUBuffer(device, &vbCreateInfo);
    if (vertexBuffer == null)
        throw new InvalidOperationException("Failed to create button vertex buffer.");

    // Upload static vertex data via staging buffer
    var stagingCreateInfo = new SDLGPUTransferBufferCreateInfo
    {
        Usage = SDLGPUTransferBufferUsage.Upload,
        Size  = vertexDataSize
    };

    var staging = CreateGPUTransferBuffer(device, &stagingCreateInfo);
    if (staging == null)
        throw new InvalidOperationException("Failed to create staging buffer for button vertices.");

    float* dst = (float*)MapGPUTransferBuffer(device, staging, false);
    fixed (float* src = vertices)
        Buffer.MemoryCopy(src, dst, vertexDataSize, vertexDataSize);
    UnmapGPUTransferBuffer(device, staging);

    var cmd     = AcquireGPUCommandBuffer(device);
    var copy    = BeginGPUCopyPass(cmd);
    var srcLoc  = new SDLGPUTransferBufferLocation { TransferBuffer = staging, Offset = 0 };
    var dstReg  = new SDLGPUBufferRegion            { Buffer = vertexBuffer, Offset = 0, Size = vertexDataSize };
    UploadToGPUBuffer(copy, &srcLoc, &dstReg, false);
    EndGPUCopyPass(copy);
    SubmitGPUCommandBuffer(cmd);
    WaitForGPUIdle(device);
    ReleaseGPUTransferBuffer(device, staging);

    // --- Dynamic quad buffers for DrawTexture ---

    // 4 vertices × (pos3 + uv2 + color4) floats
    uint quadDataSize = 4u * (3 + 2 + 4) * sizeof(float);

    var quadVBInfo = new SDLGPUBufferCreateInfo
    {
        Usage = SDLGPUBufferUsageFlags.Vertex,
        Size  = quadDataSize
    };

    quadVertexBuffer = CreateGPUBuffer(device, &quadVBInfo);
    if (quadVertexBuffer == null)
        throw new InvalidOperationException("Failed to create quad vertex buffer.");

    var quadStagingInfo = new SDLGPUTransferBufferCreateInfo
    {
        Usage = SDLGPUTransferBufferUsage.Upload,
        Size  = quadDataSize
    };

    quadStagingBuffer = CreateGPUTransferBuffer(device, &quadStagingInfo);
    if (quadStagingBuffer == null)
        throw new InvalidOperationException("Failed to create quad staging buffer.");
}

/// <summary>
/// Draw a single textured quad into the specified pixel-aligned rectangle with tint.
/// </summary>
public unsafe void DrawTexture(ITexture2D texture, Rectangle dest, Vector4 tint)
{
    // 1) Convert pixel rect → NDC
    float px = dest.X,  py = dest.Y;
    float pw = dest.Width, ph = dest.Height;
    float w  = settings.Window.Width;
    float h  = settings.Window.Height;

    float x0 = (px / w) * 2f - 1f;
    float x1 = ((px + pw) / w) * 2f - 1f;
    float y0 = 1f - (py / h) * 2f;
    float y1 = 1f - ((py + ph) / h) * 2f;

    // 2) Build quad vertex data: pos.xyz, uv.xy, color.xyzw
    float[] quad =
    {
        x0,y0,0f,  0f,1f,  tint.X,tint.Y,tint.Z,tint.W,
        x1,y0,0f,  1f,1f,  tint.X,tint.Y,tint.Z,tint.W,
        x1,y1,0f,  1f,0f,  tint.X,tint.Y,tint.Z,tint.W,
        x0,y1,0f,  0f,1f,  tint.X,tint.Y,tint.Z,tint.W
    };

    // 3) Upload to staging buffer
    float* dst = (float*)MapGPUTransferBuffer(device, quadStagingBuffer, false);
    fixed (float* src = quad)
        Buffer.MemoryCopy(src, dst, quad.Length * sizeof(float), quad.Length * sizeof(float));
    UnmapGPUTransferBuffer(device, quadStagingBuffer);

    // 4) Copy staging → GPU buffer
    var cmd = AcquireGPUCommandBuffer(device);
    var copy = BeginGPUCopyPass(cmd);
    var srcLoc = new SDLGPUTransferBufferLocation { TransferBuffer = quadStagingBuffer, Offset = 0 };
    var dstReg = new SDLGPUBufferRegion { Buffer = quadVertexBuffer, Offset = 0, Size = (uint)(quad.Length * sizeof(float)) };
    UploadToGPUBuffer(copy, &srcLoc, &dstReg, false);
    EndGPUCopyPass(copy);
    SubmitGPUCommandBuffer(cmd);
    WaitForGPUIdle(device);

    // 5) Render quad
    SDLGPUTexture* backbuffer;
    uint _w, _h;
    var renderCmd = AcquireGPUCommandBuffer(device);
    if (!WaitAndAcquireGPUSwapchainTexture(renderCmd, window, &backbuffer, &_w, &_h))
        return;

    var target = new SDLGPUColorTargetInfo
    {
        Texture   = backbuffer,
        LoadOp    = SDLGPULoadOp.Load,   // Preserve existing frame content
        StoreOp   = SDLGPUStoreOp.Store,
    };

    var pass = BeginGPURenderPass(renderCmd, &target, 1, null);
    if (pass != null)
    {
        BindGPUGraphicsPipeline(pass, buttonProgram!.Pipeline);

        var vbBind = new SDLGPUBufferBinding { Buffer = quadVertexBuffer, Offset = 0 };
        BindGPUVertexBuffers(pass, 0, &vbBind, 1);

        var ts = new SDLGPUTextureSamplerBinding
        {
            Texture = texture.Handle,
            Sampler = defaultSampler
        };
        BindGPUFragmentSamplers(pass, 0, &ts, 1);

        // Draw as triangle strip (4 vertices)
        DrawGPUPrimitives(pass, 4, 1, 0, 0);
        EndGPURenderPass(pass);
    }

    SubmitGPUCommandBuffer(renderCmd);
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
                Texture = backbuffer,
                LoadOp = SDLGPULoadOp.Clear,
                StoreOp = SDLGPUStoreOp.Store,
                ClearColor = new SDLFColor { R = 0.1f, G = 0.1f, B = 0.1f, A = 1f }
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

    /// <summary>
    /// Release all content, including the dynamic quad buffers.
    /// </summary>
    public unsafe void UnloadContent()
    {
        lock (renderLock)
        {
            // Release quad buffers first
            if (quadVertexBuffer != null)
            {
                ReleaseGPUBuffer(device, quadVertexBuffer);
                quadVertexBuffer = null;
            }
            if (quadStagingBuffer != null)
            {
                ReleaseGPUTransferBuffer(device, quadStagingBuffer);
                quadStagingBuffer = null;
            }

            // Existing cleanup…
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