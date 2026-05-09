using System;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace FastMap.Map;

internal sealed class FastMapPageComponent : MapComponent
{
    public const int ChunkSize = 32;
    public const int ChunksPerPage = 32;
    public const int PageSize = ChunkSize * ChunksPerPage;
    public const int PixelCount = PageSize * PageSize;

    private readonly Vec3d worldPos;
    private Vec2f viewPos = new();
    private int[]? pixels;
    private readonly uint[] validRows = new uint[ChunksPerPage];
    private LoadedTexture? texture;
    private FastMapAtlasSlot? atlasSlot;
    private MeshRef? visibleChunksMesh;
    private bool visibleChunksMeshDirty = true;

    public FastMapPageComponent(ICoreClientAPI capi, FastVec2i pageKey)
        : base(capi)
    {
        PageKey = pageKey;
        BaseChunkCoord = new FastVec2i(pageKey.X * ChunksPerPage, pageKey.Y * ChunksPerPage);
        worldPos = new Vec3d(BaseChunkCoord.X * ChunkSize, 0, BaseChunkCoord.Y * ChunkSize);
    }

    public FastVec2i PageKey { get; }

    public FastVec2i BaseChunkCoord { get; }

    public LoadedTexture? Texture => texture;

    public bool HasGpuTexture => atlasSlot != null || (texture != null && !texture.Disposed);

    public bool HasPixelBuffer => pixels != null;

    public long LastTouchedMs { get; set; }

    public bool HasAnyValidChunks
    {
        get
        {
            for (int i = 0; i < validRows.Length; i++)
            {
                if (validRows[i] != 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public bool IsChunkValid(int localChunkX, int localChunkZ)
    {
        if (localChunkX < 0 || localChunkX >= ChunksPerPage || localChunkZ < 0 || localChunkZ >= ChunksPerPage)
        {
            return false;
        }

        return (validRows[localChunkZ] & (1u << localChunkX)) != 0;
    }

    public void ApplySnapshot(FastMapPageSnapshot snapshot)
    {
        if (snapshot.TransferPixelsToPage)
        {
            pixels = snapshot.Pixels;
        }
        else
        {
            int[] pagePixels = EnsurePixelBuffer();
            System.Array.Copy(snapshot.Pixels, pagePixels, pagePixels.Length);
        }

        System.Array.Copy(snapshot.ValidRows, validRows, validRows.Length);
        visibleChunksMeshDirty = true;
    }

    public void SetChunk(FastVec2i chunkCoord, int[] tilePixels)
    {
        int localChunkX = chunkCoord.X - BaseChunkCoord.X;
        int localChunkZ = chunkCoord.Y - BaseChunkCoord.Y;
        if (localChunkX < 0 || localChunkX >= ChunksPerPage || localChunkZ < 0 || localChunkZ >= ChunksPerPage)
        {
            return;
        }

        int[] pagePixels = EnsurePixelBuffer();
        int dstX = localChunkX * ChunkSize;
        int dstY = localChunkZ * ChunkSize;

        for (int row = 0; row < ChunkSize; row++)
        {
            System.Array.Copy(tilePixels, row * ChunkSize, pagePixels, (dstY + row) * PageSize + dstX, ChunkSize);
        }

        uint bit = 1u << localChunkX;
        if ((validRows[localChunkZ] & bit) == 0)
        {
            validRows[localChunkZ] |= bit;
            visibleChunksMeshDirty = true;
        }
    }

    public FastMapPageSnapshot CreateSnapshot()
    {
        int[] pagePixels = EnsurePixelBuffer();
        int[] pixelCopy = new int[pagePixels.Length];
        uint[] validCopy = new uint[validRows.Length];
        System.Array.Copy(pagePixels, pixelCopy, pagePixels.Length);
        System.Array.Copy(validRows, validCopy, validRows.Length);
        return new FastMapPageSnapshot(PageKey, validCopy, pixelCopy);
    }

    public void Upload()
    {
        if (!HasAnyValidChunks || pixels == null)
        {
            return;
        }

        if (texture == null || texture.Disposed)
        {
            texture = new LoadedTexture(capi, 0, PageSize, PageSize);
        }

        capi.Render.LoadOrUpdateTextureFromRgba(pixels, false, 0, ref texture);
        capi.Render.BindTexture2d(texture.TextureId);
        capi.Render.GlGenerateTex2DMipmaps();
        RefreshVisibleChunksMesh();
    }

    public void Upload(FastMapTextureAtlas atlas)
    {
        if (!HasAnyValidChunks || pixels == null)
        {
            return;
        }

        if (texture != null && !texture.Disposed)
        {
            texture.Dispose();
            texture = null;
        }

        FastMapAtlasSlot? previousSlot = atlasSlot;
        atlasSlot = atlas.Upload(PageKey, pixels);
        if (previousSlot != atlasSlot)
        {
            visibleChunksMeshDirty = true;
        }

        RefreshVisibleChunksMesh();
    }

    public void ReleasePixelBuffer()
    {
        pixels = null;
    }

    public override void Render(GuiElementMap map, float dt)
    {
        int textureId;
        if (atlasSlot != null)
        {
            textureId = atlasSlot.TextureId;
        }
        else if (texture != null && !texture.Disposed)
        {
            textureId = texture.TextureId;
        }
        else
        {
            return;
        }

        RefreshVisibleChunksMesh();
        if (visibleChunksMesh == null || visibleChunksMesh.Disposed)
        {
            return;
        }

        map.TranslateWorldPosToViewPos(worldPos, ref viewPos);
        Vec2f bottomRightViewPos = new();
        map.TranslateWorldPosToViewPos(new Vec3d(worldPos.X + PageSize, 0, worldPos.Z + PageSize), ref bottomRightViewPos);

        float x1 = (float)Math.Floor(map.Bounds.renderX + viewPos.X);
        float y1 = (float)Math.Floor(map.Bounds.renderY + viewPos.Y);
        float x2 = (float)Math.Ceiling(map.Bounds.renderX + bottomRightViewPos.X);
        float y2 = (float)Math.Ceiling(map.Bounds.renderY + bottomRightViewPos.Y);
        float width = Math.Max(1f, x2 - x1);
        float height = Math.Max(1f, y2 - y1);

        capi.Render.Render2DTexture(
            visibleChunksMesh,
            textureId,
            x1,
            y1,
            width,
            height,
            50f
        );
    }

    public void DisposeTexture()
    {
        if (texture != null && !texture.Disposed)
        {
            texture.Dispose();
        }

        texture = null;
        atlasSlot?.Release();
        atlasSlot = null;

        if (visibleChunksMesh != null && !visibleChunksMesh.Disposed)
        {
            visibleChunksMesh.Dispose();
        }

        visibleChunksMesh = null;
        visibleChunksMeshDirty = true;
    }

    private void RefreshVisibleChunksMesh()
    {
        if (!visibleChunksMeshDirty)
        {
            return;
        }

        visibleChunksMeshDirty = false;
        if (visibleChunksMesh != null && !visibleChunksMesh.Disposed)
        {
            visibleChunksMesh.Dispose();
            visibleChunksMesh = null;
        }

        int runCount = CountValidRuns();
        if (runCount == 0)
        {
            return;
        }

        MeshData mesh = new(runCount * 4, runCount * 6, withNormals: false, withUv: true, withRgba: false, withFlags: false);
        for (int z = 0; z < ChunksPerPage; z++)
        {
            uint row = validRows[z];
            int x = 0;
            while (x < ChunksPerPage)
            {
                while (x < ChunksPerPage && (row & (1u << x)) == 0)
                {
                    x++;
                }

                if (x >= ChunksPerPage)
                {
                    break;
                }

                int startX = x;
                while (x < ChunksPerPage && (row & (1u << x)) != 0)
                {
                    x++;
                }

                AddChunkRunQuad(mesh, startX, z, x, atlasSlot);
            }
        }

        visibleChunksMesh = capi.Render.UploadMesh(mesh);
    }

    private int[] EnsurePixelBuffer()
    {
        pixels ??= new int[PixelCount];
        return pixels;
    }

    private int CountValidRuns()
    {
        int count = 0;
        for (int z = 0; z < ChunksPerPage; z++)
        {
            uint row = validRows[z];
            bool inRun = false;
            for (int x = 0; x < ChunksPerPage; x++)
            {
                bool valid = (row & (1u << x)) != 0;
                if (valid && !inRun)
                {
                    count++;
                    inRun = true;
                }
                else if (!valid)
                {
                    inRun = false;
                }
            }
        }

        return count;
    }

    private static void AddChunkRunQuad(MeshData mesh, int startChunkX, int chunkZ, int endChunkXExclusive, FastMapAtlasSlot? atlasSlot)
    {
        float x1 = startChunkX / (float)ChunksPerPage;
        float x2 = endChunkXExclusive / (float)ChunksPerPage;
        float y1 = chunkZ / (float)ChunksPerPage;
        float y2 = (chunkZ + 1) / (float)ChunksPerPage;
        float u1 = atlasSlot == null ? x1 : atlasSlot.U0 + (atlasSlot.U1 - atlasSlot.U0) * x1;
        float u2 = atlasSlot == null ? x2 : atlasSlot.U0 + (atlasSlot.U1 - atlasSlot.U0) * x2;
        float v1 = atlasSlot == null ? y1 : atlasSlot.V0 + (atlasSlot.V1 - atlasSlot.V0) * y1;
        float v2 = atlasSlot == null ? y2 : atlasSlot.V0 + (atlasSlot.V1 - atlasSlot.V0) * y2;
        float drawX1 = x1 * 2f - 1f;
        float drawX2 = x2 * 2f - 1f;
        float drawY1 = y1 * 2f - 1f;
        float drawY2 = y2 * 2f - 1f;
        int vertexBase = mesh.VerticesCount;

        mesh.AddVertex(drawX1, drawY1, 0f, u1, v1);
        mesh.AddVertex(drawX2, drawY1, 0f, u2, v1);
        mesh.AddVertex(drawX2, drawY2, 0f, u2, v2);
        mesh.AddVertex(drawX1, drawY2, 0f, u1, v2);

        mesh.AddIndex(vertexBase);
        mesh.AddIndex(vertexBase + 1);
        mesh.AddIndex(vertexBase + 2);
        mesh.AddIndex(vertexBase);
        mesh.AddIndex(vertexBase + 2);
        mesh.AddIndex(vertexBase + 3);
    }
}
