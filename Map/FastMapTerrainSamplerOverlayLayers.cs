using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace FastMap.Map;

public sealed class FastMapRainfallLayer : FastMapTerrainSamplerOverlayLayer
{
    public FastMapRainfallLayer(ICoreAPI api, IWorldMapManager mapSink)
        : base(api, mapSink)
    {
    }

    public override string Title => "Rainfall";

    public override string LayerGroupCode => "fastmap-rainfall";

    protected override string HoverLabel => "Rainfall";

    protected override float SelectValue(FastMapTerrainSamplerColumn sample) => sample.Rainfall;

    protected override int ColorForValue(float value)
    {
        if (value <= 0.001f)
        {
            return 0;
        }

        return PackRgbWithAlpha(0x355FA8, 0.22f + Math.Clamp(value, 0f, 1f) * 0.78f);
    }
}

public sealed class FastMapTemperatureLayer : FastMapTerrainSamplerOverlayLayer
{
    public FastMapTemperatureLayer(ICoreAPI api, IWorldMapManager mapSink)
        : base(api, mapSink)
    {
    }

    public override string Title => "Temperature";

    public override string LayerGroupCode => "fastmap-temperature";

    protected override string HoverLabel => "Temperature";

    protected override float SelectValue(FastMapTerrainSamplerColumn sample) => sample.Temperature;

    protected override int ColorForSample(FastMapTerrainSamplerColumn sample)
    {
        float temperatureCelsius = TemperatureCelsius(sample);
        if (temperatureCelsius < 0f)
        {
            return PackRgbWithAlpha(0x5B7DD9, Math.Clamp(-temperatureCelsius / 20f, 0f, 1f));
        }

        return PackRgbWithAlpha(0xC8753F, Math.Clamp(temperatureCelsius / 40f, 0f, 1f));
    }

    protected override string FormatHoverValue(FastMapTerrainSamplerColumn sample)
    {
        return $"{MathF.Round(TemperatureCelsius(sample), 1)}C";
    }

    private float TemperatureCelsius(FastMapTerrainSamplerColumn sample)
    {
        int unscaledTemp = (sample.ClimateColor >> 16) & 0xFF;
        if (unscaledTemp == 0)
        {
            unscaledTemp = Math.Clamp((int)MathF.Round(sample.Temperature * 255f), 0, 255);
        }

        return Climate.GetScaledAdjustedTemperatureFloat(unscaledTemp, sample.Height - capi.World.SeaLevel);
    }
}

public sealed class FastMapForestDensityLayer : FastMapTerrainSamplerOverlayLayer
{
    public FastMapForestDensityLayer(ICoreAPI api, IWorldMapManager mapSink)
        : base(api, mapSink)
    {
    }

    public override string Title => "Forest";

    public override string LayerGroupCode => "fastmap-forest-density";

    protected override string HoverLabel => "Forest";

    protected override float SelectValue(FastMapTerrainSamplerColumn sample) => sample.ForestDensity;

    protected override int ColorForValue(float value) => PackRgbWithAlpha(0x98844C, value);
}

public sealed class FastMapShrubDensityLayer : FastMapTerrainSamplerOverlayLayer
{
    public FastMapShrubDensityLayer(ICoreAPI api, IWorldMapManager mapSink)
        : base(api, mapSink)
    {
    }

    public override string Title => "Shrubs";

    public override string LayerGroupCode => "fastmap-shrub-density";

    protected override string HoverLabel => "Shrub";

    protected override float SelectValue(FastMapTerrainSamplerColumn sample) => sample.ShrubDensity;

    protected override int ColorForValue(float value) => PackRgbWithAlpha(0x9CA361, value);
}

public abstract class FastMapTerrainSamplerOverlayLayer : MapLayer
{
    private const int ChunkSize = 32;
    private const int GroupSize = 3;
    private const int SampleStep = 8;
    private const int SamplesPerAxis = ChunkSize / SampleStep;
    private const int SamplesPerChunk = SamplesPerAxis * SamplesPerAxis;
    private const int LowResolutionChunkSize = SamplesPerAxis;
    private const int MaxChunksPerViewChange = 24;
    private const int MaxPageUploadsPerTick = 2;
    private const int MaxOverlayAlpha = 180;
    private const int TextureClampToEdge = 33071;
    private static readonly ConcurrentDictionary<FastVec2i, FastMapTerrainSamplerColumn[]> SharedSamplesByChunk = new();

    protected readonly ICoreClientAPI capi;
    private readonly FastMapTerrainFallbackDiskCache diskCache;
    private readonly Dictionary<FastVec2i, FastMapPageComponent> pages = new();
    private readonly HashSet<FastVec2i> dirtyPages = new();
    private readonly HashSet<FastVec2i> visibleChunks = new();
    private readonly HashSet<FastVec2i> visiblePageKeys = new();
    private readonly HashSet<FastVec2i> knownMissingDiskPages = new();
    private Vec3d hoverWorldPos = new();

    private FastMapTerrainSamplerAdapter? sampler;
    private bool samplerLookupAttempted;
    private bool unavailableLogged;

    protected FastMapTerrainSamplerOverlayLayer(ICoreAPI api, IWorldMapManager mapSink)
        : base(api, mapSink)
    {
        capi = (ICoreClientAPI)api;
        diskCache = new FastMapTerrainFallbackDiskCache(
            api.World.SavegameIdentifier,
            SampleStep,
            FastMapModSystem.Instance?.Config.UseHighCompressionCache ?? false,
            $"terrain-overlay-{LayerCodeForCache()}-v1");
        ZIndex = 2;
    }

    public override EnumMapAppSide DataSide => EnumMapAppSide.Client;

    public override bool RequireChunkLoaded => false;

    protected abstract string HoverLabel { get; }

    protected abstract float SelectValue(FastMapTerrainSamplerColumn sample);

    protected virtual int ColorForValue(float value) => 0;

    protected virtual int ColorForSample(FastMapTerrainSamplerColumn sample)
    {
        return ColorForValue(SelectValue(sample));
    }

    protected virtual string FormatHoverValue(FastMapTerrainSamplerColumn sample)
    {
        return $"{MathF.Round(SelectValue(sample) * 100f)}%";
    }

    public override void OnLoaded()
    {
        EnsureSampler();
    }

    public override void OnMapClosedClient()
    {
        visibleChunks.Clear();
    }

    public override void OnViewChangedClient(List<FastVec2i> nowVisible, List<FastVec2i> nowHidden)
    {
        foreach (FastVec2i coord in nowHidden)
        {
            visibleChunks.Remove(coord);
        }

        int generated = 0;
        foreach (FastVec2i coord in nowVisible)
        {
            visibleChunks.Add(coord);
            EnsurePageLoaded(PageKey(coord));
            if (generated < MaxChunksPerViewChange && EnsureChunk(coord))
            {
                generated++;
            }
        }

        RebuildVisiblePageKeys();
    }

    public override void OnTick(float dt)
    {
        if (!Active)
        {
            return;
        }

        int generated = 0;
        foreach (FastVec2i coord in visibleChunks)
        {
            if (generated >= MaxChunksPerViewChange)
            {
                break;
            }

            EnsurePageLoaded(PageKey(coord));
            if (EnsureChunk(coord))
            {
                generated++;
            }
        }

        UploadDirtyPages();
    }

    public override void Render(GuiElementMap mapElem, float dt)
    {
        if (!Active)
        {
            return;
        }

        EnsureSampler();
        foreach (FastVec2i pageKey in visiblePageKeys)
        {
            if (pages.TryGetValue(pageKey, out FastMapPageComponent? page) && page.HasGpuTexture)
            {
                if (page.Texture != null)
                {
                    ApplyNearestNeighbor(page.Texture);
                }

                capi.Render.GlToggleBlend(true);
                page.Render(mapElem, dt);
            }
        }
    }

    public override void OnMouseMoveClient(MouseEvent args, GuiElementMap mapElem, StringBuilder hoverText)
    {
        if (!Active || !EnsureSampler())
        {
            return;
        }

        mapElem.TranslateViewPosToWorldPos(new Vec2f(args.X - (float)mapElem.Bounds.renderX, args.Y - (float)mapElem.Bounds.renderY), ref hoverWorldPos);
        FastMapTerrainSamplerColumn sample = sampler!.SampleColumn((int)MathF.Floor((float)hoverWorldPos.X), (int)MathF.Floor((float)hoverWorldPos.Z));
        if (!sample.HasClimate)
        {
            return;
        }

        if (hoverText.Length > 0)
        {
            hoverText.AppendLine();
        }

        hoverText.Append(HoverLabel).Append(": ").Append(FormatHoverValue(sample));
        if (FastMapModSystem.Instance?.Config.LogStats == true && TryGetOverlayPixelAt((int)MathF.Floor((float)hoverWorldPos.X), (int)MathF.Floor((float)hoverWorldPos.Z), out OverlayPixelDebug pixelDebug))
        {
            hoverText
                .Append(" [page ")
                .Append(pixelDebug.PageKey.X)
                .Append("/")
                .Append(pixelDebug.PageKey.Y)
                .Append(" px ")
                .Append(pixelDebug.PixelX)
                .Append("/")
                .Append(pixelDebug.PixelZ)
                .Append(" rgba=0x")
                .Append(pixelDebug.Color.ToString("X8"))
                .Append(" a=")
                .Append(pixelDebug.Alpha)
                .Append("/")
                .Append(MaxOverlayAlpha)
                .Append(" (")
                .Append(MathF.Round(pixelDebug.Alpha / (float)MaxOverlayAlpha * 100f))
                .Append("%)]");
        }
    }

    public override void Dispose()
    {
        foreach (FastMapPageComponent page in pages.Values)
        {
            page.DisposeTexture();
        }

        pages.Clear();
        dirtyPages.Clear();
    }

    public override void OnShutDown()
    {
        Dispose();
        SharedSamplesByChunk.Clear();
    }

    protected static int PackRgbWithAlpha(int rgb, float alphaWeight)
    {
        alphaWeight = Math.Clamp(alphaWeight, 0f, 1f);
        if (alphaWeight <= 0.001f)
        {
            return 0;
        }

        int alpha = Math.Clamp((int)MathF.Round(alphaWeight * MaxOverlayAlpha), 0, MaxOverlayAlpha);
        int r = (rgb >> 16) & 0xFF;
        int g = (rgb >> 8) & 0xFF;
        int b = rgb & 0xFF;

        // Map textures use Vintage Story's reversed byte order: 0xAABBGGRR.
        return (alpha << 24) | (b << 16) | (g << 8) | r;
    }

    private bool EnsureSampler()
    {
        if (samplerLookupAttempted)
        {
            return sampler != null && sampler.HasColumnSamples;
        }

        samplerLookupAttempted = true;
        sampler = FastMapTerrainSamplerAdapter.TryCreate();
        if (sampler == null || !sampler.HasColumnSamples)
        {
            if (!unavailableLogged)
            {
                unavailableLogged = true;
                capi.Logger.Notification("[FastMap] {0} layer needs Terrain Sampler column samples; overlay disabled.", Title);
            }

            return false;
        }

        return true;
    }

    private bool EnsureChunk(FastVec2i chunk)
    {
        if (!EnsureSampler())
        {
            return false;
        }

        FastVec2i pageKey = PageKey(chunk);
        FastMapPageComponent page = EnsurePageLoaded(pageKey);

        int localChunkX = chunk.X - page.BaseChunkCoord.X;
        int localChunkZ = chunk.Y - page.BaseChunkCoord.Y;
        if (page.IsChunkValid(localChunkX, localChunkZ))
        {
            return false;
        }

        page.SetLowResolutionChunk(chunk, GenerateChunkPixels(chunk), LowResolutionChunkSize);
        dirtyPages.Add(pageKey);
        return true;
    }

    private int[] GenerateChunkPixels(FastVec2i chunk)
    {
        int[] pixels = new int[LowResolutionChunkSize * LowResolutionChunkSize];
        FastMapTerrainSamplerColumn[] samples = SharedSamplesByChunk.GetOrAdd(chunk, BuildChunkSamples);

        for (int sampleZ = 0; sampleZ < LowResolutionChunkSize; sampleZ++)
        {
            for (int sampleX = 0; sampleX < LowResolutionChunkSize; sampleX++)
            {
                FastMapTerrainSamplerColumn sample = samples[sampleZ * SamplesPerAxis + sampleX];
                pixels[sampleZ * LowResolutionChunkSize + sampleX] = sample.HasClimate ? ColorForSample(sample) : 0;
            }
        }

        return pixels;
    }

    private FastMapTerrainSamplerColumn[] BuildChunkSamples(FastVec2i chunk)
    {
        FastMapTerrainSamplerColumn[] samples = new FastMapTerrainSamplerColumn[SamplesPerChunk];
        int worldX0 = chunk.X * ChunkSize;
        int worldZ0 = chunk.Y * ChunkSize;

        for (int sampleZ = 0; sampleZ < ChunkSize; sampleZ += SampleStep)
        {
            for (int sampleX = 0; sampleX < ChunkSize; sampleX += SampleStep)
            {
                samples[(sampleZ / SampleStep) * SamplesPerAxis + sampleX / SampleStep] =
                    sampler!.SampleColumn(worldX0 + sampleX, worldZ0 + sampleZ);
            }
        }

        return samples;
    }

    private void UploadDirtyPages()
    {
        if (dirtyPages.Count == 0)
        {
            return;
        }

        int uploaded = 0;
        foreach (FastVec2i pageKey in dirtyPages.ToArray())
        {
            if (uploaded >= MaxPageUploadsPerTick)
            {
                return;
            }

            if (pages.TryGetValue(pageKey, out FastMapPageComponent? page) && page.HasPixelBuffer)
            {
                page.Upload();
                if (page.Texture != null)
                {
                    ApplyNearestNeighbor(page.Texture);
                }

                int[]? pixels = page.HasAllValidChunks ? page.CopyPixels() : null;
                if (pixels != null)
                {
                    diskCache.Save(pageKey, pixels);
                }
            }

            dirtyPages.Remove(pageKey);
            uploaded++;
        }
    }

    private void ApplyNearestNeighbor(LoadedTexture texture)
    {
        if (texture.TextureId <= 0)
        {
            return;
        }

        capi.Render.GlToggleBlend(true);
        capi.Render.BindTexture2d(texture.TextureId);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, TextureClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, TextureClampToEdge);
    }

    private FastMapPageComponent EnsurePageLoaded(FastVec2i pageKey)
    {
        if (pages.TryGetValue(pageKey, out FastMapPageComponent? page))
        {
            return page;
        }

        page = new FastMapPageComponent(capi, pageKey);
        pages[pageKey] = page;

        if (!knownMissingDiskPages.Contains(pageKey)
            && diskCache.MightContain(pageKey)
            && diskCache.TryLoad(pageKey, out FastMapPageSnapshot snapshot))
        {
            page.ApplySnapshot(snapshot);
            page.Upload();
            if (page.Texture != null)
            {
                ApplyNearestNeighbor(page.Texture);
            }
        }
        else
        {
            knownMissingDiskPages.Add(pageKey);
        }

        return page;
    }

    private void RebuildVisiblePageKeys()
    {
        visiblePageKeys.Clear();
        foreach (FastVec2i chunk in visibleChunks)
        {
            visiblePageKeys.Add(PageKey(chunk));
        }
    }

    private static FastVec2i PageKey(FastVec2i chunk)
    {
        return new FastVec2i(Math.DivRem(chunk.X, FastMapPageComponent.ChunksPerPage, out int rx) - (rx < 0 ? 1 : 0),
            Math.DivRem(chunk.Y, FastMapPageComponent.ChunksPerPage, out int ry) - (ry < 0 ? 1 : 0));
    }

    private bool TryGetOverlayPixelAt(int worldX, int worldZ, out OverlayPixelDebug pixelDebug)
    {
        pixelDebug = default;
        int chunkX = FloorDiv(worldX, ChunkSize);
        int chunkZ = FloorDiv(worldZ, ChunkSize);
        FastVec2i pageKey = PageKey(new FastVec2i(chunkX, chunkZ));
        if (!pages.TryGetValue(pageKey, out FastMapPageComponent? page))
        {
            return false;
        }

        int[]? pixels = page.CopyPixels();
        if (pixels == null || pixels.Length == 0)
        {
            return false;
        }

        int texturePixelSize = page.TexturePixelSize;
        int localBlockX = Mod(worldX - page.BaseChunkCoord.X * ChunkSize, FastMapPageComponent.PageSize);
        int localBlockZ = Mod(worldZ - page.BaseChunkCoord.Y * ChunkSize, FastMapPageComponent.PageSize);
        int pixelX = Math.Clamp(localBlockX / SampleStep, 0, texturePixelSize - 1);
        int pixelZ = Math.Clamp(localBlockZ / SampleStep, 0, texturePixelSize - 1);
        int color = pixels[pixelZ * texturePixelSize + pixelX];
        pixelDebug = new OverlayPixelDebug(pageKey, pixelX, pixelZ, color, (color >> 24) & 0xFF);
        return true;
    }

    private static int FloorDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        int remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private static int Mod(int value, int divisor)
    {
        int result = value % divisor;
        return result < 0 ? result + divisor : result;
    }

    private string LayerCodeForCache()
    {
        string code = LayerGroupCode;
        const string prefix = "fastmap-";
        return code.StartsWith(prefix, StringComparison.Ordinal) ? code[prefix.Length..] : code;
    }

    private readonly record struct OverlayPixelDebug(FastVec2i PageKey, int PixelX, int PixelZ, int Color, int Alpha);
}
