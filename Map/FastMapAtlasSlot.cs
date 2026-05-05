using Vintagestory.API.MathTools;

namespace FastMap.Map;

internal sealed class FastMapAtlasSlot
{
    private readonly FastMapTextureAtlas owner;

    public FastMapAtlasSlot(FastMapTextureAtlas owner, FastVec2i pageKey, int textureId, int slotX, int slotY, int atlasSize, int slotSize)
    {
        this.owner = owner;
        PageKey = pageKey;
        TextureId = textureId;
        SlotX = slotX;
        SlotY = slotY;
        AtlasSize = atlasSize;
        SlotSize = slotSize;

        U0 = (slotX + 0.5f) / atlasSize;
        V0 = (slotY + 0.5f) / atlasSize;
        U1 = (slotX + slotSize - 0.5f) / atlasSize;
        V1 = (slotY + slotSize - 0.5f) / atlasSize;
    }

    public FastVec2i PageKey { get; }

    public int TextureId { get; }

    public int SlotX { get; }

    public int SlotY { get; }

    public int AtlasSize { get; }

    public int SlotSize { get; }

    public float U0 { get; }

    public float V0 { get; }

    public float U1 { get; }

    public float V1 { get; }

    public void Release()
    {
        owner.Release(this);
    }
}
