#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FizzleFramework2D.Configuration;
using Hexa.NET.SDL3;
using Serilog;
using static Hexa.NET.SDL3.Image.SDLImage;
using static Hexa.NET.SDL3.SDL;
using Log = Serilog.Log;

namespace FizzleFramework2D.Graphics.Textures
{
    public sealed class TextureManager : ITextureManager
    {
        private static readonly ILogger logger = Log.ForContext<TextureManager>();

        private readonly unsafe SDLGPUDevice* device;
        private readonly GameSettings settings;
        private readonly Dictionary<string, ITexture2D> textures = new();
        private readonly FileSystemWatcher? hotReloadWatcher;
        private bool disposed;

        public event EventHandler<TextureReloadedEventArgs>? TextureReloaded;

        public unsafe TextureManager(SDLGPUDevice* device, GameSettings settings)
        {
            this.device = device;
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));

            logger.Information("Initializing TextureManager");

            // Setup hot reload for development
            if (settings.Development.EnableHotReload && Directory.Exists(settings.Content.TexturesDirectory))
            {
                try
                {
                    hotReloadWatcher = new FileSystemWatcher(settings.Content.TexturesDirectory)
                    {
                        Filter = "*.*",
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                        EnableRaisingEvents = true,
                        IncludeSubdirectories = true
                    };
                    hotReloadWatcher.Changed += OnTextureFileChanged;
                    hotReloadWatcher.Created += OnTextureFileChanged;

                    logger.Information("Hot reload enabled for textures in: {Directory}",
                        settings.Content.TexturesDirectory);
                }
                catch (Exception ex)
                {
                    logger.Warning(ex, "Failed to setup texture hot reload");
                }
            }
        }

        public async Task<ITexture2D> LoadTextureAsync(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Texture path cannot be null or empty", nameof(path));

            var textureName = Path.GetFileNameWithoutExtension(path);
            logger.Information("Loading texture: {Name} from {Path}", textureName, path);

            if (textures.TryGetValue(textureName, out var existingTexture))
            {
                logger.Debug("Texture {Name} already loaded from cache", textureName);
                return existingTexture;
            }

            try
            {
                var texture = await LoadTextureFromFileAsync(path, textureName);
                textures[textureName] = texture;

                logger.Information("✅ Texture loaded successfully: {Name} ({Width}x{Height})",
                    textureName, texture.Width, texture.Height);
                return texture;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "❌ Failed to load texture: {Name}", textureName);
                throw;
            }
        }

        private async Task<ITexture2D> LoadTextureFromFileAsync(string path, string name)
        {
            var fullPath = Path.IsPathRooted(path)
                ? path
                : Path.Combine(settings.Content.TexturesDirectory, path);

            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"Texture file not found: {fullPath}");

            logger.Debug("Reading texture data from: {FilePath}", fullPath);

            unsafe
            {
                // Load image surface
                var surface = Load(fullPath);
                if (surface == null)
                {
                    var error = GetError()->ToString();
                    throw new TextureLoadException($"Failed to load image '{fullPath}': {error}");
                }

                try
                {
                    var surfaceData = *surface;
                    int width = surfaceData.W;
                    int height = surfaceData.H;
                    var format = SDLGPUTextureFormat.R8G8B8A8Unorm; // Common format for 2D textures

                    // Convert surface to RGBA if needed
                    var rgbaSurface = surface;
                    if (surfaceData.Format != SDLPixelFormat.Rgba32)
                    {
                        rgbaSurface = ConvertSurface(surface, SDLPixelFormat.Rgba32);
                        if (rgbaSurface == null)
                        {
                            var error = GetError()->ToString();
                            throw new TextureLoadException($"Failed to convert surface to RGBA: {error}");
                        }
                    }

                    try
                    {
                        // Create GPU texture
                        var textureCreateInfo = new SDLGPUTextureCreateInfo
                        {
                            Type = SDLGPUTextureType.Texturetype2D,
                            Format = format,
                            Width = (uint)width,
                            Height = (uint)height,
                            LayerCountOrDepth = 1,
                            NumLevels = 1,
                            Usage = SDLGPUTextureUsageFlags.Sampler,
                            SampleCount = SDLGPUSampleCount.Samplecount1
                        };

                        var gpuTexture = CreateGPUTexture(device, &textureCreateInfo);
                        if (gpuTexture == null)
                        {
                            var error = GetError()->ToString();
                            throw new TextureLoadException($"Failed to create GPU texture: {error}");
                        }

                        // ✅ FIX: Proper SDL3 copy pass workflow for texture upload
                        var rgbaSurfaceData = *rgbaSurface;
                        var uploadCmd = AcquireGPUCommandBuffer(device);
                        if (uploadCmd == null)
                        {
                            ReleaseGPUTexture(device, gpuTexture);
                            throw new TextureLoadException("Failed to acquire command buffer for texture upload");
                        }

                        // Create transfer buffer
                        var transferBufferCreateInfo = new SDLGPUTransferBufferCreateInfo
                        {
                            Usage = SDLGPUTransferBufferUsage.Upload,
                            Size = (uint)(width * height * 4) // RGBA = 4 bytes per pixel
                        };

                        var transferBuffer = CreateGPUTransferBuffer(device, &transferBufferCreateInfo);
                        if (transferBuffer == null)
                        {
                            ReleaseGPUTexture(device, gpuTexture);
                            throw new TextureLoadException("Failed to create transfer buffer");
                        }

                        // Map and copy pixel data
                        var mappedData = MapGPUTransferBuffer(device, transferBuffer, false);
                        if (mappedData == null)
                        {
                            ReleaseGPUTransferBuffer(device, transferBuffer);
                            ReleaseGPUTexture(device, gpuTexture);
                            throw new TextureLoadException("Failed to map transfer buffer");
                        }

                        // Copy pixel data to transfer buffer
                        var pixelDataSize = width * height * 4;
                        Buffer.MemoryCopy(rgbaSurfaceData.Pixels, mappedData, pixelDataSize, pixelDataSize);
                        UnmapGPUTransferBuffer(device, transferBuffer);

                        // ✅ CRITICAL FIX: Begin copy pass before uploading
                        var copyPass = BeginGPUCopyPass(uploadCmd);
                        if (copyPass == null)
                        {
                            ReleaseGPUTransferBuffer(device, transferBuffer);
                            ReleaseGPUTexture(device, gpuTexture);
                            throw new TextureLoadException("Failed to begin GPU copy pass");
                        }

                        // Upload to GPU texture using copy pass
                        var textureTransferInfo = new SDLGPUTextureTransferInfo
                        {
                            TransferBuffer = transferBuffer,
                            Offset = 0,
                            PixelsPerRow = (uint)width,
                            RowsPerLayer = (uint)height
                        };

                        var textureRegion = new SDLGPUTextureRegion
                        {
                            Texture = gpuTexture,
                            MipLevel = 0,
                            Layer = 0,
                            X = 0, Y = 0, Z = 0,
                            W = (uint)width,
                            H = (uint)height,
                            D = 1
                        };

                        // ✅ FIX: Use copy pass instead of command buffer
                        UploadToGPUTexture(copyPass, &textureTransferInfo, &textureRegion, false);

                        // ✅ FIX: End copy pass before submitting
                        EndGPUCopyPass(copyPass);

                        // Submit command buffer and wait for completion
                        SubmitGPUCommandBuffer(uploadCmd);
                        WaitForGPUIdle(device);

                        // Cleanup transfer buffer
                        ReleaseGPUTransferBuffer(device, transferBuffer);

                        logger.Debug("GPU texture created and uploaded successfully: {Name}", name);
                        return new Texture2D(name, device, gpuTexture, width, height, format);
                    }
                    finally
                    {
                        if (rgbaSurface != surface)
                            DestroySurface(rgbaSurface);
                    }
                }
                finally
                {
                    DestroySurface(surface);
                }
            }
        }

        public ITexture2D? GetTexture(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            return textures.GetValueOrDefault(name);
        }

        public void EnableHotReload(bool enable)
        {
            if (hotReloadWatcher != null)
            {
                hotReloadWatcher.EnableRaisingEvents = enable;
                logger.Information("Texture hot reload {Status}", enable ? "enabled" : "disabled");
            }
        }

        private async void OnTextureFileChanged(object sender, FileSystemEventArgs e)
        {
            if (e.Name == null) return;

            var extension = Path.GetExtension(e.Name).ToLowerInvariant();
            if (!IsValidImageExtension(extension))
                return;

            var textureName = Path.GetFileNameWithoutExtension(e.Name);
            logger.Information("🔄 Hot reloading texture: {Name}", textureName);

            try
            {
                await Task.Delay(100); // Ensure file write is complete
                TextureReloaded?.Invoke(this, new TextureReloadedEventArgs(textureName));
                logger.Information("✅ Texture hot reloaded: {Name}", textureName);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "❌ Hot reload failed for texture: {Name}", textureName);
            }
        }

        private static bool IsValidImageExtension(string extension)
        {
            return extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" or ".gif";
        }

        public void Dispose()
        {
            if (disposed)
                return;

            logger.Information("Disposing TextureManager");

            try
            {
                hotReloadWatcher?.Dispose();

                foreach (var texture in textures.Values)
                    texture.Dispose();

                textures.Clear();

                disposed = true;
                logger.Information("TextureManager disposed successfully");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Error during TextureManager disposal");
            }
        }
    }
}