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
    private const int VerticesPerSprite = 6;  // CRITICAL: 6 vertices for triangle list
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

        // Create sprite data for this draw call
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

        // FIXED: Accumulate all sprites into single buffer upload
        var allSprites = new List<SpriteData>();
        var drawCalls = new List<(ITexture2D texture, int startVertex, int spriteCount)>();
    
        int currentVertexOffset = 0;
    
        foreach (var batch in textureBatches)
        {
            var texture = batch.Key;
            var sprites = batch.Value;
        
            // Record draw call info with proper vertex offset
            drawCalls.Add((texture, currentVertexOffset, sprites.Count));
        
            // Add sprites to combined list
            allSprites.AddRange(sprites);
            currentVertexOffset += sprites.Count * VerticesPerSprite;
        }
    
        // Upload ALL sprite data in one operation
        if (allSprites.Count > 0)
        {
            UploadAllSpriteData(allSprites.ToArray());
        
            // Execute draw calls with proper vertex offsets
            foreach (var (texture, startVertex, spriteCount) in drawCalls)
            {
                DrawBatchWithOffset(renderPass, texture, startVertex, spriteCount);
            }
        }

        isBegun = false;
    }

    #endregion

    #region Private Implementation Methods

    /// <summary>
    /// Creates the dynamic vertex buffer and staging buffer for sprite batching.
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

        // Create staging buffer for CPU->GPU uploads
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
    /// Creates sprite vertex data with proper UV coordinates for SDL3 GPU.
    /// </summary>
    private SpriteData CreateSpriteData(Rectangle dest, Vector4 tint)
    {
        float px = dest.X, py = dest.Y;
        float pw = dest.Width, ph = dest.Height;
        float w = settings.Window.Width, h = settings.Window.Height;

        // Convert to NDC
        float x0 = (px / w) * 2f - 1f;      // left
        float x1 = ((px + pw) / w) * 2f - 1f; // right
        float y0 = 1f - (py / h) * 2f;      // top
        float y1 = 1f - ((py + ph) / h) * 2f; // bottom

        // FIXED: Correct UV coordinates for shader Y-flip
        return new SpriteData
        {
            Vertices = new[]
            {
                // Triangle 1: top-left, bottom-left, top-right
                x0, y0, 0f, 0f, 1f, tint.X, tint.Y, tint.Z, tint.W, // top-left: UV (0,1) - CHANGED
                x0, y1, 0f, 0f, 0f, tint.X, tint.Y, tint.Z, tint.W, // bottom-left: UV (0,0) - CHANGED
                x1, y0, 0f, 1f, 1f, tint.X, tint.Y, tint.Z, tint.W, // top-right: UV (1,1) - CHANGED
            
                // Triangle 2: top-right, bottom-left, bottom-right
                x1, y0, 0f, 1f, 1f, tint.X, tint.Y, tint.Z, tint.W, // top-right: UV (1,1) - CHANGED
                x0, y1, 0f, 0f, 0f, tint.X, tint.Y, tint.Z, tint.W, // bottom-left: UV (0,0) - CHANGED
                x1, y1, 0f, 1f, 0f, tint.X, tint.Y, tint.Z, tint.W  // bottom-right: UV (1,0) - CHANGED
            }
        };
    }

    /// <summary>
    /// Uploads ALL sprite vertex data to GPU in one operation - FIXED to prevent overwriting.
    /// </summary>
    private void UploadAllSpriteData(SpriteData[] sprites)
    {
        if (sprites.Length == 0) return;

        // Calculate total data size for ALL sprites
        int totalVertices = sprites.Length * VerticesPerSprite;
        int totalFloats = totalVertices * FloatsPerVertex;
        uint dataSize = (uint)(totalFloats * sizeof(float));

        // Map staging buffer for CPU access
        float* mappedData = (float*)MapGPUTransferBuffer(device, stagingBuffer, false);
        if (mappedData == null)
            throw new InvalidOperationException("Failed to map staging buffer");

        try
        {
            // Copy ALL sprite vertex data to staging buffer sequentially
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

        // Transfer from staging buffer to GPU vertex buffer
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
            Offset = 0,  // Start at beginning of buffer
            Size = dataSize 
        };

        UploadToGPUBuffer(copyPass, &srcLocation, &dstRegion, false);
        EndGPUCopyPass(copyPass);
        SubmitGPUCommandBuffer(cmd);
        WaitForGPUIdle(device);
    }

    /// <summary>
    /// FIXED: Draw batch with proper vertex buffer offset to prevent rendering conflicts.
    /// </summary>
    private void DrawBatchWithOffset(SDLGPURenderPass* renderPass, ITexture2D texture, int startVertex, int spriteCount)
    {
        // Bind graphics pipeline
        BindGPUGraphicsPipeline(renderPass, shaderProgram.Pipeline);

        // CRITICAL FIX: Use lowercase field names for SDL3 GPU structures
        var vertexBinding = new SDLGPUBufferBinding 
        { 
            Buffer = dynamicVertexBuffer,  // ✅ lowercase 'buffer'
            Offset = (uint)(startVertex * FloatsPerVertex * sizeof(float))  // ✅ lowercase 'offset' with proper calculation
        };
        BindGPUVertexBuffers(renderPass, 0, &vertexBinding, 1);

        // CRITICAL FIX: Use lowercase field names for texture binding
        var textureSamplerBinding = new SDLGPUTextureSamplerBinding
        {
            Texture = texture.Handle,  // ✅ lowercase 'texture'
            Sampler = sampler         // ✅ lowercase 'sampler'
        };
        BindGPUFragmentSamplers(renderPass, 0, &textureSamplerBinding, 1);

        // Draw this batch's sprites with correct vertex count
        uint totalVertices = (uint)(spriteCount * VerticesPerSprite);
        DrawGPUPrimitives(renderPass, totalVertices, 1, 0, 0);
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
