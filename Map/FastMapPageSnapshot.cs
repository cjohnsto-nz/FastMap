using Vintagestory.API.MathTools;

namespace FastMap.Map;

internal sealed class FastMapPageSnapshot
{
    public FastMapPageSnapshot(FastVec2i pageKey, uint[] validRows, int[] pixels)
    {
        PageKey = pageKey;
        ValidRows = validRows;
        Pixels = pixels;
    }

    public FastVec2i PageKey { get; }

    public uint[] ValidRows { get; }

    public int[] Pixels { get; }

    public bool HasAnyValidChunks
    {
        get
        {
            for (int i = 0; i < ValidRows.Length; i++)
            {
                if (ValidRows[i] != 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public FastMapPageSnapshot Merge(FastMapPageSnapshot overlay)
    {
        int[] mergedPixels = new int[Pixels.Length];
        uint[] mergedValidRows = new uint[ValidRows.Length];
        System.Array.Copy(Pixels, mergedPixels, Pixels.Length);
        System.Array.Copy(ValidRows, mergedValidRows, ValidRows.Length);

        for (int chunkZ = 0; chunkZ < FastMapPageComponent.ChunksPerPage; chunkZ++)
        {
            uint overlayRow = overlay.ValidRows[chunkZ];
            if (overlayRow == 0)
            {
                continue;
            }

            mergedValidRows[chunkZ] |= overlayRow;
            for (int chunkX = 0; chunkX < FastMapPageComponent.ChunksPerPage; chunkX++)
            {
                if ((overlayRow & (1u << chunkX)) == 0)
                {
                    continue;
                }

                CopyChunk(overlay.Pixels, mergedPixels, chunkX, chunkZ);
            }
        }

        return new FastMapPageSnapshot(PageKey, mergedValidRows, mergedPixels);
    }

    private static void CopyChunk(int[] sourcePixels, int[] destinationPixels, int chunkX, int chunkZ)
    {
        int sourceX = chunkX * FastMapPageComponent.ChunkSize;
        int sourceY = chunkZ * FastMapPageComponent.ChunkSize;

        for (int row = 0; row < FastMapPageComponent.ChunkSize; row++)
        {
            System.Array.Copy(
                sourcePixels,
                (sourceY + row) * FastMapPageComponent.PageSize + sourceX,
                destinationPixels,
                (sourceY + row) * FastMapPageComponent.PageSize + sourceX,
                FastMapPageComponent.ChunkSize
            );
        }
    }
}
