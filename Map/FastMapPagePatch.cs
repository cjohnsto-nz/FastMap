using Vintagestory.API.MathTools;

namespace FastMap.Map;

internal sealed class FastMapPagePatch
{
    public FastMapPagePatch(FastVec2i chunkCoord, int[] pixels)
    {
        ChunkCoord = chunkCoord;
        Pixels = pixels;
    }

    public FastVec2i ChunkCoord { get; }

    public int[] Pixels { get; }
}
