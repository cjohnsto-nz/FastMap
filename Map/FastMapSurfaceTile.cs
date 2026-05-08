using System;
using Vintagestory.API.MathTools;

namespace FastMap.Map;

internal sealed class FastMapSurfaceTile
{
    public FastMapSurfaceTile(FastVec2i chunkCoord, bool colorAccurate, int[] heights, int[] chunkYs, int[] blockIds)
    {
        ChunkCoord = chunkCoord;
        ColorAccurate = colorAccurate;
        Heights = new int[FastMapPageComponent.ChunkSize * FastMapPageComponent.ChunkSize];
        ChunkYs = new int[Heights.Length];
        BlockIds = new int[Heights.Length];
        Array.Copy(heights, Heights, Heights.Length);
        Array.Copy(chunkYs, ChunkYs, ChunkYs.Length);
        Array.Copy(blockIds, BlockIds, BlockIds.Length);
    }

    public FastVec2i ChunkCoord { get; }

    public bool ColorAccurate { get; }

    public int[] Heights { get; }

    public int[] ChunkYs { get; }

    public int[] BlockIds { get; }

    public long LastTouchedMs { get; set; }
}
