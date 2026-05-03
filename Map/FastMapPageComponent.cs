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
    private readonly int[] pixels = new int[PixelCount];
    private readonly uint[] validRows = new uint[ChunksPerPage];
    private LoadedTexture? texture;

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
        System.Array.Copy(snapshot.Pixels, pixels, pixels.Length);
        System.Array.Copy(snapshot.ValidRows, validRows, validRows.Length);
    }

    public void SetChunk(FastVec2i chunkCoord, int[] tilePixels)
    {
        int localChunkX = chunkCoord.X - BaseChunkCoord.X;
        int localChunkZ = chunkCoord.Y - BaseChunkCoord.Y;
        if (localChunkX < 0 || localChunkX >= ChunksPerPage || localChunkZ < 0 || localChunkZ >= ChunksPerPage)
        {
            return;
        }

        int dstX = localChunkX * ChunkSize;
        int dstY = localChunkZ * ChunkSize;

        for (int row = 0; row < ChunkSize; row++)
        {
            System.Array.Copy(tilePixels, row * ChunkSize, pixels, (dstY + row) * PageSize + dstX, ChunkSize);
        }

        validRows[localChunkZ] |= 1u << localChunkX;
    }

    public FastMapPageSnapshot CreateSnapshot()
    {
        int[] pixelCopy = new int[pixels.Length];
        uint[] validCopy = new uint[validRows.Length];
        System.Array.Copy(pixels, pixelCopy, pixels.Length);
        System.Array.Copy(validRows, validCopy, validRows.Length);
        return new FastMapPageSnapshot(PageKey, validCopy, pixelCopy);
    }

    public void Upload()
    {
        if (!HasAnyValidChunks)
        {
            return;
        }

        if (texture == null || texture.Disposed)
        {
            texture = new LoadedTexture(capi, 0, PageSize, PageSize);
        }

        capi.Render.LoadOrUpdateTextureFromRgba(pixels, false, 0, ref texture);
    }

    public override void Render(GuiElementMap map, float dt)
    {
        if (texture == null || texture.Disposed)
        {
            return;
        }

        map.TranslateWorldPosToViewPos(worldPos, ref viewPos);
        capi.Render.Render2DTexture(
            texture.TextureId,
            (float)(int)(map.Bounds.renderX + viewPos.X),
            (float)(int)(map.Bounds.renderY + viewPos.Y),
            (float)(int)(texture.Width * map.ZoomLevel),
            (float)(int)(texture.Height * map.ZoomLevel),
            50f,
            null
        );
    }

    public void DisposeTexture()
    {
        if (texture != null && !texture.Disposed)
        {
            texture.Dispose();
        }
    }
}
