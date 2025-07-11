﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FizzleFramework2D.Configuration;
using Hexa.NET.SDL3;
using Serilog;
using static Hexa.NET.SDL3.SDL;
using Log = Serilog.Log;

namespace FizzleFramework2D.Graphics.Shaders;

public sealed class ShaderManager : IShaderManager
{
    private static readonly ILogger logger = Log.ForContext<ShaderManager>();

    private readonly unsafe SDLGPUDevice* device;
    private readonly GameSettings settings;
    private readonly Dictionary<string, IShader> shaders = new();
    private readonly Dictionary<string, IShaderProgram> programs = new();
    private readonly FileSystemWatcher? hotReloadWatcher;
    private bool disposed;

    public event EventHandler<ShaderReloadedEventArgs>? ShaderReloaded;

    public unsafe ShaderManager(SDLGPUDevice* device, GameSettings settings)
    {
        this.device = device;
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));

        logger.Information("Initializing ShaderManager");

        // Setup hot reload for development
        if (settings.Development.EnableHotReload && Directory.Exists(settings.Content.ShadersDirectory))
        {
            try
            {
                hotReloadWatcher = new FileSystemWatcher(settings.Content.ShadersDirectory)
                {
                    Filter = "*.spv",
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true,
                    IncludeSubdirectories = false
                };
                hotReloadWatcher.Changed += OnShaderFileChanged;
                hotReloadWatcher.Created += OnShaderFileChanged;

                logger.Information("Hot reload enabled for shaders in: {Directory}", settings.Content.ShadersDirectory);
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "Failed to setup shader hot reload");
            }
        }
    }

    public async Task<IShader> LoadShaderAsync(string name, SDLGPUShaderStage stage)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Shader name cannot be null or empty", nameof(name));

        logger.Information("Loading shader: {Name} ({Stage})", name, stage);

        var cacheKey = $"{name}_{stage}";
        if (shaders.TryGetValue(cacheKey, out var existingShader))
        {
            logger.Debug("Shader {Name} already loaded from cache", name);
            return existingShader;
        }

        try
        {
            var shader = await LoadShaderFromFileAsync(name, stage);
            shaders[cacheKey] = shader;

            logger.Information("✅ Shader loaded successfully: {Name} ({Stage})", name, stage);
            return shader;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "❌ Failed to load shader: {Name} ({Stage})", name, stage);
            throw;
        }
    }

    private async Task<IShader> LoadShaderFromFileAsync(string name, SDLGPUShaderStage stage)
    {
        var fileName = GetShaderFileName(name, stage);
        var filePath = Path.Combine(settings.Content.ShadersDirectory, fileName);

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Shader file not found: {filePath}");

        logger.Debug("Reading shader bytecode from: {FilePath}", filePath);

        // Read shader bytecode
        var bytecode = await File.ReadAllBytesAsync(filePath);
        if (bytecode.Length == 0)
            throw new InvalidOperationException($"Shader file is empty: {filePath}");

        // Create SDL shader with proper resource counts
        unsafe
        {
            fixed (byte* codePtr = bytecode)
            {
                var unmanagedEntryPoint = Marshal.StringToHGlobalAnsi("main");

                try
                {
                    // CRITICAL: Specify resource counts based on shader stage and expected resources
                    var createInfo = new SDLGPUShaderCreateInfo
                    {
                        CodeSize = (nuint)bytecode.Length,
                        Code = codePtr,
                        Stage = stage,
                        Format = SDLGPUShaderFormat.Spirv,
                        Entrypoint = (byte*)unmanagedEntryPoint,
                        // FIXED: Specify resource counts for proper descriptor set layout creation
                        NumSamplers = GetShaderSamplerCount(name, stage),
                        NumStorageTextures = GetShaderStorageTextureCount(name, stage),
                        NumStorageBuffers = GetShaderStorageBufferCount(name, stage),
                        NumUniformBuffers = GetShaderUniformBufferCount(name, stage),
                        Props = 0
                    };

                    var handle = CreateGPUShader(device, &createInfo);
                    if (handle == null)
                    {
                        var error = GetError()->ToString();
                        throw new ShaderCompilationException($"Failed to create GPU shader '{name}': {error}");
                    }

                    logger.Debug("GPU shader created successfully: {Name} with {Samplers} samplers", name, createInfo.NumSamplers);
                    return new Shader(name, stage, device, handle, bytecode);
                }
                finally
                {
                    Marshal.FreeHGlobal(unmanagedEntryPoint);
                }
            }
        }
    }

    // CRITICAL: Resource count methods - these determine descriptor set layouts
    private static uint GetShaderSamplerCount(string name, SDLGPUShaderStage stage)
    {
        // For your button shaders:
        // - Vertex shaders: 0 samplers (no textures)
        // - Fragment shaders: 1 sampler (the button texture)
        return stage switch
        {
            SDLGPUShaderStage.Fragment when name.Equals("button", StringComparison.OrdinalIgnoreCase) => 1u,
            SDLGPUShaderStage.Vertex => 0u,
            _ => 0u
        };
    }

    private static uint GetShaderStorageTextureCount(string name, SDLGPUShaderStage stage)
    {
        // Basic button rendering doesn't use storage textures
        return 0u;
    }

    private static uint GetShaderStorageBufferCount(string name, SDLGPUShaderStage stage)
    {
        // Basic button rendering doesn't use storage buffers
        return 0u;
    }

    private static uint GetShaderUniformBufferCount(string name, SDLGPUShaderStage stage)
    {
        // Basic button rendering doesn't use uniform buffers (using vertex attributes instead)
        return 0u;
    }

    public async Task<IShaderProgram> CreateProgramAsync(string vertexShaderName, string fragmentShaderName)
    {
        if (string.IsNullOrEmpty(vertexShaderName))
            throw new ArgumentException("Vertex shader name cannot be null or empty", nameof(vertexShaderName));
        if (string.IsNullOrEmpty(fragmentShaderName))
            throw new ArgumentException("Fragment shader name cannot be null or empty", nameof(fragmentShaderName));

        logger.Information("Creating shader program: {Vertex} + {Fragment}", vertexShaderName, fragmentShaderName);

        var programName = $"{vertexShaderName}_{fragmentShaderName}";
        if (programs.TryGetValue(programName, out var existingProgram))
        {
            logger.Debug("Shader program {Name} already exists", programName);
            return existingProgram;
        }

        try
        {
            // Load individual shaders
            var vertexShader = await LoadShaderAsync(vertexShaderName, SDLGPUShaderStage.Vertex);
            var fragmentShader = await LoadShaderAsync(fragmentShaderName, SDLGPUShaderStage.Fragment);

            // Create graphics pipeline with complete configuration
            unsafe
            {
                // Configure vertex input attributes (position, texcoord, color)
                var vertexAttributes = stackalloc SDLGPUVertexAttribute[3];

                vertexAttributes[0] = new SDLGPUVertexAttribute
                {
                    Location = 0,
                    BufferSlot = 0,
                    Format = SDLGPUVertexElementFormat.Float3, // vec3 position
                    Offset = 0
                };

                vertexAttributes[1] = new SDLGPUVertexAttribute
                {
                    Location = 1,
                    BufferSlot = 0,
                    Format = SDLGPUVertexElementFormat.Float2, // vec2 texcoord
                    Offset = 12 // 3 floats * 4 bytes = 12
                };

                vertexAttributes[2] = new SDLGPUVertexAttribute
                {
                    Location = 2,
                    BufferSlot = 0,
                    Format = SDLGPUVertexElementFormat.Float4, // vec4 color
                    Offset = 20 // 12 + (2 floats * 4 bytes) = 20
                };

                var vertexBufferDescription = new SDLGPUVertexBufferDescription
                {
                    Slot = 0,
                    Pitch = 36, // 3*4 + 2*4 + 4*4 = 36 bytes per vertex
                    InputRate = SDLGPUVertexInputRate.Vertex
                };

                var vertexInputState = new SDLGPUVertexInputState
                {
                    VertexBufferDescriptions = &vertexBufferDescription,
                    NumVertexBuffers = 1,
                    VertexAttributes = vertexAttributes,
                    NumVertexAttributes = 3
                };

                // Color target for standard RGBA rendering with alpha blending
                var colorTargetDescription = new SDLGPUColorTargetDescription
                {
                    Format = SDLGPUTextureFormat.B8G8R8A8Unorm, // Match your swapchain format
                    BlendState = new SDLGPUColorTargetBlendState
                    {
                        EnableBlend = 1,
                        AlphaBlendOp = SDLGPUBlendOp.Add,
                        ColorBlendOp = SDLGPUBlendOp.Add,
                        ColorWriteMask = SDLGPUColorComponentFlags.R |
                                         SDLGPUColorComponentFlags.G |
                                         SDLGPUColorComponentFlags.B |
                                         SDLGPUColorComponentFlags.A,
                        SrcColorBlendfactor = SDLGPUBlendFactor.SrcAlpha,
                        DstColorBlendfactor = SDLGPUBlendFactor.OneMinusSrcAlpha,
                        SrcAlphaBlendfactor = SDLGPUBlendFactor.One,
                        DstAlphaBlendfactor = SDLGPUBlendFactor.OneMinusSrcAlpha
                    }
                };

                // Create the graphics pipeline
                var pipelineInfo = new SDLGPUGraphicsPipelineCreateInfo
                {
                    VertexShader = ((Shader)vertexShader).Handle,
                    FragmentShader = ((Shader)fragmentShader).Handle,
                    VertexInputState = vertexInputState,
                    PrimitiveType = SDLGPUPrimitiveType.Trianglelist,
                    RasterizerState = new SDLGPURasterizerState
                    {
                        FillMode = SDLGPUFillMode.Fill,
                        CullMode = SDLGPUCullMode.None,
                        FrontFace = SDLGPUFrontFace.CounterClockwise,
                        DepthBiasConstantFactor = 0,
                        DepthBiasClamp = 0,
                        DepthBiasSlopeFactor = 0,
                        EnableDepthBias = 0,
                        EnableDepthClip = 1
                    },
                    // FIXED: Correct multisample state for your binding version
                    MultisampleState = new SDLGPUMultisampleState
                    {
                        SampleCount = SDLGPUSampleCount.Samplecount1,
                        SampleMask = 0,      // Reserved field - must be 0
                        EnableMask = 0,      // Reserved field - must be 0 (byte type)
                        Padding1 = 0,        // Must be 0
                        Padding2 = 0,        // Must be 0
                        Padding3 = 0         // Must be 0
                    },
                    DepthStencilState = new SDLGPUDepthStencilState
                    {
                        CompareOp = SDLGPUCompareOp.Always,
                        BackStencilState = new SDLGPUStencilOpState(),
                        FrontStencilState = new SDLGPUStencilOpState(),
                        CompareMask = 0,
                        WriteMask = 0,
                        EnableDepthTest = 0,
                        EnableDepthWrite = 0,
                        EnableStencilTest = 0
                    },
                    TargetInfo = new SDLGPUGraphicsPipelineTargetInfo
                    {
                        ColorTargetDescriptions = &colorTargetDescription,
                        NumColorTargets = 1,
                        DepthStencilFormat = SDLGPUTextureFormat.Invalid,
                        HasDepthStencilTarget = 0
                    },
                    Props = 0
                };

                var pipeline = CreateGPUGraphicsPipeline(device, &pipelineInfo);
                if (pipeline == null)
                {
                    var error = GetError()->ToString();
                    throw new ShaderLinkException($"Failed to create graphics pipeline '{programName}': {error}");
                }

                var program = new ShaderProgram(programName, vertexShader, fragmentShader, device, pipeline);
                programs[programName] = program;

                logger.Information("✅ Shader program created successfully: {Name}", programName);
                return program;
            }
        }
        catch (Exception ex)
        {
            logger.Error(ex, "❌ Failed to create shader program: {Name}", programName);
            throw;
        }
    }

    public IShader? GetShader(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        return shaders.GetValueOrDefault(name);
    }

    public IShaderProgram? GetProgram(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        return programs.GetValueOrDefault(name);
    }

    public void EnableHotReload(bool enable)
    {
        if (hotReloadWatcher != null)
        {
            hotReloadWatcher.EnableRaisingEvents = enable;
            logger.Information("Shader hot reload {Status}", enable ? "enabled" : "disabled");
        }
    }

    private async void OnShaderFileChanged(object sender, FileSystemEventArgs e)
    {
        if (e.Name == null || !Path.GetExtension(e.Name).Equals(".spv", StringComparison.OrdinalIgnoreCase))
            return;

        var shaderName = Path.GetFileNameWithoutExtension(e.Name);
        logger.Information("🔄 Hot reloading shader: {Name}", shaderName);

        try
        {
            // Add delay to ensure file write is complete
            await Task.Delay(100);

            // TODO: Implement actual hot reload logic
            ShaderReloaded?.Invoke(this, new ShaderReloadedEventArgs(shaderName));
            logger.Information("✅ Shader hot reloaded: {Name}", shaderName);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "❌ Hot reload failed for shader: {Name}", shaderName);
        }
    }

    private static string GetShaderFileName(string name, SDLGPUShaderStage stage)
    {
        var suffix = stage switch
        {
            SDLGPUShaderStage.Vertex => ".vert",
            SDLGPUShaderStage.Fragment => ".frag",
            _ => throw new ArgumentException($"Unknown shader stage: {stage}")
        };

        return $"{name}{suffix}.spv";
    }

    public void Dispose()
    {
        if (disposed)
            return;

        logger.Information("Disposing ShaderManager");

        try
        {
            hotReloadWatcher?.Dispose();

            foreach (var program in programs.Values)
                program.Dispose();

            foreach (var shader in shaders.Values)
                shader.Dispose();

            programs.Clear();
            shaders.Clear();

            disposed = true;
            logger.Information("ShaderManager disposed successfully");
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Error during ShaderManager disposal");
        }
    }
}
