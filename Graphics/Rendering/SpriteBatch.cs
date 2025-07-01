using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FizzleFramework2D.Configuration;
using FizzleFramework2D.Graphics.Shaders;
using FizzleFramework2D.Graphics.Shapes;
using FizzleFramework2D.Graphics.Textures;
using Hexa.NET.SDL3;
using static Hexa.NET.SDL3.SDL;

namespace FizzleFramework2D.Graphics.Rendering;

public unsafe class SpriteBatch : IDisposable
{
    private readonly SDLGPUDevice* device;
    private readonly IShaderProgram shaderProgram;
    private readonly SDLGPUSampler* sampler;
    private readonly GameSettings settings;

    private readonly List<SpriteData> spriteBatch = [];
    private readonly Dictionary<ITexture2D, List<SpriteData>> textureBatches = [];
    
    private SDLGPUBuffer* dynamicVertexBuffer;
    private SDLGPUTransferBuffer* stagingBuffer;
    
    private const int MaxSpritesPerBatch = 1000;
    private const int VerticesPerSprite = 6;  // FIXED: 6 vertices for triangle list
    private const int FloatsPerVertex = 9;   // pos(3) + uv(2) + color(4)
    
    private bool isBegun = false;
    private bool disposed = false;

    public SpriteBatch(SDLGPUDevice* device, IShaderProgram shaderProgram, 
        SDLGPUSampler* sampler, GameSettings settings)
    {
        this.device = device;
        this.shaderProgram = shaderProgram;
        this.sampler = sampler;
        this.settings = settings;
        
        CreateBuffers();
    }

    #region Public API Methods

    public void Begin()
    {
        if (isBegun)
            throw new InvalidOperationException("SpriteBatch.Begin called more than once");
        
        isBegun = true;
        spriteBatch.Clear();
        textureBatches.Clear();
    }

    public void Draw(ITexture2D texture, Rectangle destinationRectangle, Vector4 tint)
    {
        if (!isBegun)
            throw new InvalidOperationException("SpriteBatch.Draw called before Begin");

        // FIXED: Remove unused texture parameter from CreateSpriteData call
        var sprite = CreateSpriteData(destinationRectangle, tint);
    
        // Group by texture for efficient batching
        if (!textureBatches.ContainsKey(texture))
            textureBatches[texture] = new List<SpriteData>();
    
        textureBatches[texture].Add(sprite);
    }

    public void End(SDLGPURenderPass* renderPass)
    {
        if (!isBegun)
            throw new InvalidOperationException("SpriteBatch.End called before Begin");

        // Render all texture batches
        foreach (var batch in textureBatches)
        {
            RenderTextureBatch(renderPass, batch.Key, batch.Value);
        }

        isBegun = false;
    }

    #endregion

    #region Private Implementation Methods

    /// <summary>
    /// Creates the dynamic vertex buffer and staging buffer for sprite batching[2][7].
    /// </summary>
    private void CreateBuffers()
    {
        // Calculate total buffer size: max sprites × vertices per sprite × floats per vertex
        uint totalFloats = MaxSpritesPerBatch * VerticesPerSprite * FloatsPerVertex;
        uint bufferSize = totalFloats * sizeof(float);

        // Create dynamic vertex buffer for GPU rendering
        var vertexBufferInfo = new SDLGPUBufferCreateInfo
        {
            Usage = SDLGPUBufferUsageFlags.Vertex,
            Size = bufferSize
        };

        dynamicVertexBuffer = CreateGPUBuffer(device, &vertexBufferInfo);
        if (dynamicVertexBuffer == null)
            throw new InvalidOperationException($"Failed to create dynamic vertex buffer: {GetError()->ToString()}");

        // Create staging buffer for CPU->GPU uploads[12][15]
        var stagingBufferInfo = new SDLGPUTransferBufferCreateInfo
        {
            Usage = SDLGPUTransferBufferUsage.Upload,
            Size = bufferSize
        };

        stagingBuffer = CreateGPUTransferBuffer(device, &stagingBufferInfo);
        if (stagingBuffer == null)
            throw new InvalidOperationException($"Failed to create staging buffer: {GetError()->ToString()}");
    }

    /// <summary>
    /// Creates sprite vertex data with proper UV coordinates for SDL3 GPU[18][19].
    /// </summary>
    private SpriteData CreateSpriteData(Rectangle dest, Vector4 tint)  // Removed unused texture parameter
    {
        float px = dest.X, py = dest.Y;
        float pw = dest.Width, ph = dest.Height;
        float w = settings.Window.Width, h = settings.Window.Height;

        // Convert to NDC
        float x0 = (px / w) * 2f - 1f;      // left
        float x1 = ((px + pw) / w) * 2f - 1f; // right
        float y0 = 1f - (py / h) * 2f;      // top (NDC +Y up)
        float y1 = 1f - ((py + ph) / h) * 2f; // bottom

        // FIXED: Use standard top-left origin UV coordinates
        // Your shader will flip the Y, so provide UVs as if (0,0) is top-left
        return new SpriteData
        {
            Vertices = new[]
            {
                // Triangle 1: counter-clockwise winding
                x0, y0, 0f, 0f, 0f, tint.X, tint.Y, tint.Z, tint.W, // top-left: UV (0,0)
                x0, y1, 0f, 0f, 1f, tint.X, tint.Y, tint.Z, tint.W, // bottom-left: UV (0,1)
                x1, y0, 0f, 1f, 0f, tint.X, tint.Y, tint.Z, tint.W, // top-right: UV (1,0)
            
                // Triangle 2: counter-clockwise winding
                x1, y0, 0f, 1f, 0f, tint.X, tint.Y, tint.Z, tint.W, // top-right: UV (1,0)
                x0, y1, 0f, 0f, 1f, tint.X, tint.Y, tint.Z, tint.W, // bottom-left: UV (0,1)
                x1, y1, 0f, 1f, 1f, tint.X, tint.Y, tint.Z, tint.W  // bottom-right: UV (1,1)
            }
        };
    }
    /// <summary>
    /// Uploads sprite vertex data to GPU using efficient memory copying[11][17].
    /// </summary>
    private void UploadSpriteData(SpriteData[] sprites)
    {
        if (sprites.Length == 0) return;

        // Calculate total data size
        int totalVertices = sprites.Length * VerticesPerSprite;
        int totalFloats = totalVertices * FloatsPerVertex;
        uint dataSize = (uint)(totalFloats * sizeof(float));

        // Map staging buffer for CPU access
        float* mappedData = (float*)MapGPUTransferBuffer(device, stagingBuffer, false);
        if (mappedData == null)
            throw new InvalidOperationException("Failed to map staging buffer");

        try
        {
            // Copy all sprite vertex data to staging buffer
            int offset = 0;
            for (int i = 0; i < sprites.Length; i++)
            {
                var vertices = sprites[i].Vertices;
                fixed (float* src = vertices)
                {
                    Buffer.MemoryCopy(src, mappedData + offset, 
                        vertices.Length * sizeof(float), 
                        vertices.Length * sizeof(float));
                }
                offset += vertices.Length;
            }
        }
        finally
        {
            UnmapGPUTransferBuffer(device, stagingBuffer);
        }

        // Transfer from staging buffer to GPU vertex buffer[12][15]
        var cmd = AcquireGPUCommandBuffer(device);
        var copyPass = BeginGPUCopyPass(cmd);

        var srcLocation = new SDLGPUTransferBufferLocation 
        { 
            TransferBuffer = stagingBuffer, 
            Offset = 0 
        };
        
        var dstRegion = new SDLGPUBufferRegion 
        { 
            Buffer = dynamicVertexBuffer, 
            Offset = 0, 
            Size = dataSize 
        };

        UploadToGPUBuffer(copyPass, &srcLocation, &dstRegion, false);
        EndGPUCopyPass(copyPass);
        SubmitGPUCommandBuffer(cmd);
        WaitForGPUIdle(device);
    }

    /// <summary>
    /// Renders a batch of sprites with the same texture in a single draw call[21][24].
    /// </summary>
    private void DrawBatch(SDLGPURenderPass* renderPass, ITexture2D texture, int spriteCount)
    {
        // Bind graphics pipeline
        BindGPUGraphicsPipeline(renderPass, shaderProgram.Pipeline);

        // FIXED: Use lowercase field names
        var vertexBinding = new SDLGPUBufferBinding 
        { 
            Buffer = dynamicVertexBuffer,  
            Offset = 0                     
        };
        BindGPUVertexBuffers(renderPass, 0, &vertexBinding, 1);

        // FIXED: Use lowercase field names
        var textureSamplerBinding = new SDLGPUTextureSamplerBinding
        {
            Texture = texture.Handle,  
            Sampler = sampler          
        };
        BindGPUFragmentSamplers(renderPass, 0, &textureSamplerBinding, 1);

        // FIXED: Now uses correct vertex count (6 per sprite)
        uint totalVertices = (uint)(spriteCount * VerticesPerSprite);
        DrawGPUPrimitives(renderPass, totalVertices, 1, 0, 0);
    }

    /// <summary>
    /// Renders a batch of sprites with efficient sub-batching for large sprite counts[6][22].
    /// </summary>
    private void RenderTextureBatch(SDLGPURenderPass* renderPass, ITexture2D texture, List<SpriteData> sprites)
    {
        // Process sprites in chunks to avoid exceeding buffer limits
        for (int i = 0; i < sprites.Count; i += MaxSpritesPerBatch)
        {
            int spritesToDraw = Math.Min(MaxSpritesPerBatch, sprites.Count - i);
            var spritesSlice = sprites.Skip(i).Take(spritesToDraw).ToArray();
            
            UploadSpriteData(spritesSlice);
            DrawBatch(renderPass, texture, spritesToDraw);
        }
    }

    #endregion

    #region IDisposable Implementation

    public void Dispose()
    {
        if (disposed) return;

        if (dynamicVertexBuffer != null)
        {
            ReleaseGPUBuffer(device, dynamicVertexBuffer);
            dynamicVertexBuffer = null;
        }

        if (stagingBuffer != null)
        {
            ReleaseGPUTransferBuffer(device, stagingBuffer);
            stagingBuffer = null;
        }

        disposed = true;
    }

    #endregion
}
