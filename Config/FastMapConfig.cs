using System;
using Vintagestory.API.Common;

namespace FastMap.Config;

public sealed class FastMapConfig
{
    public int PageTextureBudget { get; set; } = 512;
    public bool EnableCompressedCache { get; set; } = true;
    public bool UseHighCompressionCache { get; set; } = false;
    public bool CleanupKeepLatestPageVersion { get; set; } = true;
    public float ViewportLoadScale { get; set; } = 1.5f;
    public int PrewarmRadiusChunks { get; set; } = 16;
    public bool EnablePrewarm { get; set; } = true;
    public bool RegenerateOnChunkDirty { get; set; } = true;
    public int MaxBackgroundTilesPerPass { get; set; } = 256;
    public int MaxPageUploadsPerTick { get; set; } = 2;
    public int MaxParallelPageLoads { get; set; } = 4;
    public int MainThreadUploadBudgetMilliseconds { get; set; } = 4;
    public float BackgroundWorkIntervalSeconds { get; set; } = 0.01f;
    public float PrewarmIntervalSeconds { get; set; } = 2.0f;
    public float PageFlushIntervalSeconds { get; set; } = 5.0f;
    public int PageFlushThreshold { get; set; } = 64;
    public bool LogStats { get; set; } = false;
    public float LogStatsIntervalSeconds { get; set; } = 5.0f;

    public static FastMapConfig Load(ICoreAPI api)
    {
        const string filename = "fastmap.json";

        FastMapConfig config;
        try
        {
            config = api.LoadModConfig<FastMapConfig>(filename) ?? new FastMapConfig();
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[FastMap] Failed to load fastmap.json, using defaults. Error: {0}", ex.Message);
            config = new FastMapConfig();
        }

        config.Normalize();
        api.StoreModConfig(config, filename);
        return config;
    }

    public void Normalize()
    {
        PageTextureBudget = Math.Clamp(PageTextureBudget, 16, 10000);
        ViewportLoadScale = Math.Clamp(ViewportLoadScale, 1.0f, 4.0f);
        PrewarmRadiusChunks = Math.Clamp(PrewarmRadiusChunks, 0, 64);
        MaxBackgroundTilesPerPass = Math.Clamp(MaxBackgroundTilesPerPass, 1, 1000);
        MaxPageUploadsPerTick = Math.Clamp(MaxPageUploadsPerTick, 1, 32);
        MaxParallelPageLoads = Math.Clamp(MaxParallelPageLoads, 1, 32);
        MainThreadUploadBudgetMilliseconds = Math.Clamp(MainThreadUploadBudgetMilliseconds, 1, 32);
        BackgroundWorkIntervalSeconds = Math.Clamp(BackgroundWorkIntervalSeconds, 0.01f, 1.0f);
        PrewarmIntervalSeconds = Math.Clamp(PrewarmIntervalSeconds, 0.25f, 30f);
        PageFlushIntervalSeconds = Math.Clamp(PageFlushIntervalSeconds, 0.5f, 120f);
        PageFlushThreshold = Math.Clamp(PageFlushThreshold, 1, 10000);
        LogStatsIntervalSeconds = Math.Clamp(LogStatsIntervalSeconds, 1f, 120f);
    }
}
