using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace FastMap.Map;

internal sealed class FastMapTextureAtlas : IDisposable
{
    private const int PreferredAtlasSize = 4096;
    private const int TextureClampToEdge = 33071;
    private const int TextureNearest = 9728;

    private readonly ICoreClientAPI capi;
    private readonly Dictionary<FastVec2i, FastMapAtlasSlot> slotsByPage = new();
    private readonly List<AtlasTexture> atlases = new();
    private readonly int atlasSize;
    private readonly int slotSize;
    private readonly int slotsPerAxis;
    private bool disposed;

    public FastMapTextureAtlas(ICoreClientAPI capi, int slotSize)
    {
        this.capi = capi;
        this.slotSize = Math.Clamp(slotSize, 1, FastMapPageComponent.PageSize);
        int maxTextureSize = Math.Max(this.slotSize, capi.Render.GlGetMaxTextureSize());
        atlasSize = Math.Min(PreferredAtlasSize, maxTextureSize);
        atlasSize -= atlasSize % this.slotSize;
        if (atlasSize < this.slotSize)
        {
            atlasSize = this.slotSize;
        }

        slotsPerAxis = Math.Max(1, atlasSize / this.slotSize);
    }

    public int AtlasCount => atlases.Count;

    public int SlotSize => slotSize;

    public long ApproxTextureBytes => (long)atlases.Count * atlasSize * atlasSize * sizeof(int);

    public FastMapAtlasSlot Upload(FastVec2i pageKey, int[] pixels, int pixelSize)
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(FastMapTextureAtlas));
        }

        if (pixelSize != slotSize || pixels.Length != slotSize * slotSize)
        {
            throw new ArgumentException("FastMap atlas upload dimensions must match the atlas slot size.", nameof(pixels));
        }

        if (!slotsByPage.TryGetValue(pageKey, out FastMapAtlasSlot? slot))
        {
            slot = AllocateSlot(pageKey);
        }

        GL.BindTexture(TextureTarget.Texture2D, slot.TextureId);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        GL.TexSubImage2D(
            TextureTarget.Texture2D,
            0,
            slot.SlotX,
            slot.SlotY,
            slotSize,
            slotSize,
            PixelFormat.Rgba,
            PixelType.UnsignedByte,
            pixels
        );
        capi.Render.CheckGlError("FastMap atlas page upload");

        return slot;
    }

    public void Release(FastMapAtlasSlot slot)
    {
        if (disposed)
        {
            return;
        }

        if (!slotsByPage.Remove(slot.PageKey))
        {
            return;
        }

        foreach (AtlasTexture atlas in atlases)
        {
            if (atlas.TextureId == slot.TextureId)
            {
                atlas.Release(slot);
                return;
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (AtlasTexture atlas in atlases)
        {
            capi.Render.GLDeleteTexture(atlas.TextureId);
        }

        atlases.Clear();
        slotsByPage.Clear();
    }

    private FastMapAtlasSlot AllocateSlot(FastVec2i pageKey)
    {
        foreach (AtlasTexture atlas in atlases)
        {
            if (atlas.TryAllocate(pageKey, out FastMapAtlasSlot? slot))
            {
                if (slot == null)
                {
                    throw new InvalidOperationException("FastMap atlas returned an empty slot.");
                }

                slotsByPage[pageKey] = slot;
                return slot;
            }
        }

        AtlasTexture newAtlas = CreateAtlasTexture();
        atlases.Add(newAtlas);
        if (!newAtlas.TryAllocate(pageKey, out FastMapAtlasSlot? newSlot))
        {
            throw new InvalidOperationException("FastMap atlas could not allocate a slot in a new texture.");
        }

        if (newSlot == null)
        {
            throw new InvalidOperationException("FastMap atlas returned an empty slot in a new texture.");
        }

        slotsByPage[pageKey] = newSlot;
        return newSlot;
    }

    private AtlasTexture CreateAtlasTexture()
    {
        int textureId = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, textureId);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, TextureNearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, TextureNearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, TextureClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, TextureClampToEdge);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, atlasSize, atlasSize, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
        capi.Render.CheckGlError("FastMap atlas texture create");

        return new AtlasTexture(this, textureId, atlasSize, slotsPerAxis);
    }

    private sealed class AtlasTexture
    {
        private readonly FastMapTextureAtlas owner;
        private readonly Stack<int> freeSlots = new();
        private readonly HashSet<int> usedSlots = new();
        private readonly int atlasSize;
        private readonly int slotsPerAxis;

        public AtlasTexture(FastMapTextureAtlas owner, int textureId, int atlasSize, int slotsPerAxis)
        {
            this.owner = owner;
            TextureId = textureId;
            this.atlasSize = atlasSize;
            this.slotsPerAxis = slotsPerAxis;

            for (int i = slotsPerAxis * slotsPerAxis - 1; i >= 0; i--)
            {
                freeSlots.Push(i);
            }
        }

        public int TextureId { get; }

        public bool TryAllocate(FastVec2i pageKey, out FastMapAtlasSlot? slot)
        {
            if (freeSlots.Count == 0)
            {
                slot = null;
                return false;
            }

            int index = freeSlots.Pop();
            usedSlots.Add(index);
            int slotX = index % slotsPerAxis * owner.slotSize;
            int slotY = index / slotsPerAxis * owner.slotSize;
            slot = new FastMapAtlasSlot(owner, pageKey, TextureId, slotX, slotY, atlasSize, owner.slotSize);
            return true;
        }

        public void Release(FastMapAtlasSlot slot)
        {
            int index = slot.SlotY / owner.slotSize * slotsPerAxis + slot.SlotX / owner.slotSize;
            if (usedSlots.Remove(index))
            {
                freeSlots.Push(index);
            }
        }
    }
}
