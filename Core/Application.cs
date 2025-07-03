#nullable enable
using System;
using System.Collections.Generic;
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

namespace FizzleFramework2D.Core;

/// <summary>
/// Button state enumeration for dynamic UI interaction
/// </summary>
public enum ButtonState
{
    Normal,
    Hovered,
    Pressed
}

/// <summary>
/// Button class for managing individual button states and properties
/// </summary>
public class Button(Rectangle bounds, ITexture2D texture, int index)
{
    public Rectangle Bounds { get; set; } = bounds;
    public ITexture2D Texture { get; set; } = texture;
    public ButtonState State { get; set; } = ButtonState.Normal;
    public int Index { get; set; } = index;

    public Vector4 GetTintColor()
    {
        return State switch
        {
            ButtonState.Hovered => Application.HoverTint,
            ButtonState.Pressed => Application.PressedTint,
            _ => Application.NormalTint
        };
    }
}

/// <summary>
/// Main game/application entry-point using SpriteBatch rendering system with dynamic button management.
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

    // Dynamic button management
    private readonly List<Button> buttons = [];
    private Vector2 mousePosition;
    private bool isMousePressed ;

    // State
    private volatile bool running;
    private bool initialized;
    private bool contentLoaded;
    private readonly SemaphoreSlim disposalSem = new(1, 1);
    private readonly object renderLock = new();

    #region Button Tinting Colors (Public for Button class access)

    public static readonly Vector4 NormalTint = new(1f, 1f, 1f, 1f); // White (normal)
    public static readonly Vector4 HoverTint = new(0.8f, 0.8f, 0.8f, 1f); // Light gray (hover)
    public static readonly Vector4 PressedTint = new(0.6f, 0.6f, 0.6f, 1f); // Dark gray (pressed)
    #endregion

    public bool IsInitialized => initialized;
    public bool IsContentLoaded => contentLoaded;
    public bool IsRunning => running;

    public Application(GameSettings settings)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        logger.Debug("Application instance constructed");
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
                logger.Error("SDL_ClaimWindowForGPUDevice failed");
                return false;
            }

            SetGPUSwapchainParameters(device, window,
                SDLGPUSwapchainComposition.Sdr,
                settings.Rendering.VSync ? SDLGPUPresentMode.Vsync : SDLGPUPresentMode.Immediate);

            CreateDefaultSampler();
            CreateManagers();

            initialized = true;
            logger.Information("Initialization complete");
            return true;
        }
        catch (Exception ex)
        {
            logger.Fatal(ex, "Initialization exception");
            return false;
        }
    }

    private unsafe bool IsWindowMinimized() => (GetWindowFlags(window) & SDLWindowFlags.Minimized) != 0;

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
            logger.Warning("LoadContent called before Initialize");
            return;
        }

        logger.Information("Loading game content …");
        try
        {
            LoadContentAsync().Wait();
            contentLoaded = true;
            logger.Information("Content loaded");
        }
        catch (Exception ex)
        {
            logger.Fatal(ex, "Content loading failed");
            throw;
        }
    }

    // TEMP TEST TEXTURE:
    private ITexture2D? backgroundTexture;

    private async Task LoadContentAsync()
    {
        if (shaderManager == null || textureManager == null)
            throw new InvalidOperationException("Managers not ready.");

        // Load shaders
        await shaderManager.LoadShaderAsync("button", SDLGPUShaderStage.Vertex);
        await shaderManager.LoadShaderAsync("button", SDLGPUShaderStage.Fragment);
        buttonProgram = await shaderManager.CreateProgramAsync("button", "button");

        // Load textures

        backgroundTexture = await textureManager.LoadTextureAsync("backgrounds/origbig.png");

        buttonTextures =
        [
            await textureManager.LoadTextureAsync("btn0.png"),
            await textureManager.LoadTextureAsync("btn1.png"),
            await textureManager.LoadTextureAsync("btn2.png")
        ];

        unsafe
        {
            // Initialize SpriteBatch after shaders and textures are loaded
            spriteBatch = new SpriteBatch(device, buttonProgram, defaultSampler, settings);
        }

        logger.Information("SpriteBatch initialized successfully");

        // Initialize buttons after textures are loaded
        InitializeButtons();
    }

    #endregion

    #region Button Management System

    /// <summary>
    /// Initialize button objects with proper bounds and textures
    /// </summary>
    private void InitializeButtons()
    {
        if (buttonTextures == null) return;

        buttons.Clear();

        // Calculate button layout
        var buttonWidth = buttonTextures[0].Width / 4;
        var buttonHeight = buttonTextures[0].Height / 4;
        const int buttonSpacing = 50;
        const int startX = 100;
        const int startY = 200;

        // Create button objects with proper bounds
        for (int i = 0; i < buttonTextures.Length; i++)
        {
            var buttonRect = new Rectangle(
                startX + i * (buttonWidth + buttonSpacing),
                startY,
                buttonWidth,
                buttonHeight
            );

            buttons.Add(new Button(buttonRect, buttonTextures[i], i));
        }

        logger.Information("Initialized {Count} interactive buttons", buttons.Count);
    }

    /// <summary>
    /// Update button states based on current mouse position and button state
    /// </summary>
    private void UpdateButtonStates()
    {
        foreach (var button in buttons)
        {
            var isMouseOver = IsPointInRectangle(mousePosition, button.Bounds);

            button.State = isMouseOver switch
            {
                true when isMousePressed => ButtonState.Pressed,
                true => ButtonState.Hovered,
                _ => ButtonState.Normal
            };
        }
    }

    /// <summary>
    /// Check if a point is inside a rectangle (hit testing)
    /// </summary>
    private static bool IsPointInRectangle(Vector2 point, Rectangle rect)
    {
        return point.X >= rect.X && point.X <= rect.X + rect.Width &&
               point.Y >= rect.Y && point.Y <= rect.Y + rect.Height;
    }

    /// <summary>
    /// Handle button click events and trigger appropriate actions
    /// </summary>
    private void HandleButtonClick()
    {
        foreach (var button in buttons)
        {
            if (IsPointInRectangle(mousePosition, button.Bounds))
            {
                // Handle button click action here
                logger.Information("Button {Index} clicked!", button.Index);

                // Example: You can add specific button actions here
                switch (button.Index)
                {
                    case 0:
                        // Button 0 action (e.g., Start Game)
                        logger.Information("Start Game button pressed");
                        break;
                    case 1:
                        // Button 1 action (e.g., Settings)
                        logger.Information("Settings button pressed");
                        break;
                    case 2:
                        // Button 2 action (e.g., Exit)
                        logger.Information("Exit button pressed");
                        break;
                }

                break; // Only handle first button found
            }
        }
    }

    #endregion

    #region Main Loop

    public void Run()
    {
        if (!initialized || !contentLoaded)
        {
            logger.Error("Run called without initialization or content");
            return;
        }

        running = true;
        logger.Information("Entering main loop");
        while (running)
        {
            PollEvents();
            Render();
            Thread.Sleep(16); // ~60 FPS
        }
    }

    /// <summary>
    /// Enhanced event polling with mouse tracking for dynamic button interaction
    /// </summary>
    private unsafe void PollEvents()
    {
        SDLEvent e;
        while (PollEvent(&e))
        {
            switch ((SDLEventType)e.Type)
            {
                case SDLEventType.Quit:
                case SDLEventType.KeyDown when e.Key.Key == SDLK_ESCAPE:
                    running = false;
                    break;

                // ADDED: Mouse motion tracking for hover effects
                case SDLEventType.MouseMotion:
                    mousePosition = new Vector2(e.Motion.X, e.Motion.Y);
                    UpdateButtonStates();
                    break;

                // ADDED: Mouse button tracking for click effects
                case SDLEventType.MouseButtonDown when e.Button.Button == (int)SDLMouseButtonFlags.Left:
                    isMousePressed = true;
                    mousePosition = new Vector2(e.Button.X, e.Button.Y);
                    UpdateButtonStates();
                    HandleButtonClick();
                    break;

                case SDLEventType.MouseButtonUp when e.Button.Button == (int)SDLMouseButtonFlags.Left:
                    isMousePressed = false;
                    mousePosition = new Vector2(e.Button.X, e.Button.Y);
                    UpdateButtonStates();
                    break;
            }
        }
    }

    #endregion

    #region Rendering - SpriteBatch System with Dynamic Button States

    private unsafe void Render()
    {
        if (buttonProgram == null || buttonTextures == null || spriteBatch == null)
            return;

        // FIXED: Use the boolean function directly, no bitwise operation needed
        if (IsWindowMinimized()) // Bail out early, nothing to draw
            return;

        lock (renderLock)
        {
            SDLGPUTexture* backbuffer;
            uint w, h;
            var cmd = AcquireGPUCommandBuffer(device);

            if (!WaitAndAcquireGPUSwapchainTexture(cmd, window, &backbuffer, &w, &h))
            {
                // Could not get a back-buffer this frame
                SubmitGPUCommandBuffer(cmd);
                return;
            }

            // ADDED: Extra safety check for 0x0 surface or null backbuffer
            if (backbuffer == null || w == 0 || h == 0)
            {
                SubmitGPUCommandBuffer(cmd);
                return;
            }

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

                // Draw dynamic interactive buttons - PRESERVED
                DrawUIElements();

                spriteBatch.End(pass);
                EndGPURenderPass(pass);
            }

            SubmitGPUCommandBuffer(cmd);
        }
    }

    /// <summary>
    /// Dynamic UI drawing using SpriteBatch with real-time button state management
    /// </summary>
    private void DrawUIElements()
    {
        if (buttons.Count == 0)
        {
            InitializeButtons(); // Initialize once if not already done
            return;
        }

        spriteBatch.Draw(backgroundTexture, new Rectangle(0, 0, 1600, 900), new(1f, 1f, 1f, 1f));
        // Draw all buttons with their current dynamic states
        foreach (var button in buttons)
        {
            var tint = button.GetTintColor(); // Gets color based on current state
            spriteBatch!.Draw(button.Texture, button.Bounds, tint);
        }

        // Example: Draw additional UI elements at specific locations
        // var logoRect = new Rectangle(10, 10, 200, 100);
        // spriteBatch!.Draw(buttonTextures[0], logoRect, Vector4.One); // White tint

        // Example: Scale textures easily
        // var scaledRect = new Rectangle(600, 50, buttonTextures[0].Width / 2, buttonTextures[0].Height / 2);
        // spriteBatch!.Draw(buttonTextures[0], scaledRect, new Vector4(1f, 0.5f, 0.5f, 1f)); // Red tint
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

            // Clear button management
            buttons.Clear();

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

        if (window == null) return;
        DestroyWindow(window);
        window = null;
    }

    private void Dispose(bool disposing)
    {
        if (!disposing) return;
        running = false;
        UnloadContent();
        DestroyDeviceAndWindow();
        Quit();
        disposalSem.Dispose();
        Log.CloseAndFlush();
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

    /// <summary>
    /// Get the current button at a specific position (useful for external UI systems)
    /// </summary>
    public Button? GetButtonAtPosition(Vector2 position)
    {
        foreach (var button in buttons)
        {
            if (IsPointInRectangle(position, button.Bounds))
                return button;
        }

        return null;
    }

    /// <summary>
    /// Get all buttons (read-only access)
    /// </summary>
    public IReadOnlyList<Button> GetButtons() => buttons.AsReadOnly();

    #endregion
}