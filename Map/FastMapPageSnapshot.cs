using Vintagestory.API.MathTools;

namespace FastMap.Map;

internal sealed class FastMapPageSnapshot
{
    public FastMapPageSnapshot(FastVec2i pageKey, uint[] validRows, int[] pixels, bool transferPixelsToPage = false)
    {
        PageKey = pageKey;
        ValidRows = validRows;
        Pixels = pixels;
        TransferPixelsToPage = transferPixelsToPage;
    }

    public FastVec2i PageKey { get; }

    public uint[] ValidRows { get; }

    public int[] Pixels { get; }

    public bool TransferPixelsToPage { get; }

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
