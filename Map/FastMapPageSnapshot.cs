using Vintagestory.API.MathTools;

namespace FastMap.Map;

internal sealed class FastMapPageSnapshot
{
    public FastMapPageSnapshot(FastVec2i pageKey, uint[] validRows, int[] pixels, bool transferPixelsToPage = false, bool synthetic = false, int resolutionScale = 1)
    {
        PageKey = pageKey;
        ValidRows = validRows;
        Pixels = pixels;
        TransferPixelsToPage = transferPixelsToPage;
        Synthetic = synthetic;
        ResolutionScale = resolutionScale;
    }

    public FastVec2i PageKey { get; }

    public uint[] ValidRows { get; }

    public int[] Pixels { get; }

    public bool TransferPixelsToPage { get; }

    public bool Synthetic { get; }

    public int ResolutionScale { get; }

    public bool IsLowResolution => ResolutionScale > 1;

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
}
