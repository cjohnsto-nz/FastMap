using System;
using Vintagestory.API.Common;

namespace FastMap.Config;

public sealed class FastMapConfig
{
    public int PageTextureBudget { get; set; } = 512;
    public int PageMemoryBudget { get; set; } = 1024;
    public int ReadyPageQueueBudget { get; set; } = 64;
    public bool EnableCompressedCache { get; set; } = true;
    public bool UseFilteredCache { get; set; } = false;
    public bool UseHighCompressionCache { get; set; } = false;
    public bool EnableTextureAtlas { get; set; } = true;
    public bool CleanupKeepLatestPageVersion { get; set; } = true;
    public bool EnableVanillaMapDbWriteback { get; set; } = true;
    public bool CleanupStaleVanillaMapDbSidecarsOnStartup { get; set; } = true;
    public bool EnableTerrainSamplerFallbackBackgroundGeneration { get; set; } = true;
    public int TerrainSamplerFallbackSampleStep { get; set; } = 4;
    public int TerrainSamplerFallbackResolutionScale { get; set; } = 4;
    public bool EnableTerrainSamplerFallbackTrueColor { get; set; } = true;
    public int TerrainSamplerFallbackTrueColorProbeStride { get; set; } = 4;
    public int TerrainSamplerFallbackSnowStartHeight { get; set; } = 250;
    public int TerrainSamplerFallbackSeasonUploadBucketsPerYear { get; set; } = 12;
    public string TerrainSamplerFallbackWaterBaseColor { get; set; } = "#0f3231";
    public string TerrainSamplerFallbackWaterRainfallColor { get; set; } = "#123940";
    public float TerrainSamplerFallbackWaterRainfallStrength { get; set; } = 1.0f;
    public string TerrainSamplerFallbackWaterTemperatureColor { get; set; } = "#123e30";
    public float TerrainSamplerFallbackWaterTemperatureStrength { get; set; } = 1.0f;
    public float TerrainSamplerFallbackWaterNoiseStrength { get; set; } = 0.04f;
    public bool UseBrownTerrainFallbackPalette { get; set; } = true;
    public bool EnableTrueColorAirSurfaceRepair { get; set; } = true;
    public int TrueColorAirSurfaceRepairDepth { get; set; } = 32;
    public string TrueColorAirFallbackColor { get; set; } = "#282828";
    public int TerrainSamplerFallbackMaxPagesPerSession { get; set; } = 2048;
    public int TerrainSamplerFallbackRadiusChunks { get; set; } = 512;
    public int TerrainSamplerFallbackBackgroundPagesPerPass { get; set; } = 16;
    public int TerrainSamplerFallbackMaxParallelBuilds { get; set; } = 4;
    public int TerrainSamplerFallbackMaxRetries { get; set; } = 3;
    public int TerrainSamplerFallbackRetryDelayMilliseconds { get; set; } = 5000;
    public float WorldMapMinZoomLevel { get; set; } = 0.1f;
    public float WorldMapMaxZoomLevel { get; set; } = 6.0f;
    public float ViewportLoadScale { get; set; } = 1.5f;
    public int ViewportPageRetentionRings { get; set; } = 1;
    public int PrewarmRadiusChunks { get; set; } = 16;
    public bool EnablePrewarm { get; set; } = true;
    public bool RegenerateOnChunkDirty { get; set; } = true;
    public bool UseMinimalChunkDirtyRepairFanout { get; set; } = true;
    public int ExperimentalChunkDirtyRepairDelayMilliseconds { get; set; } = 0;
    public int MaxBackgroundTilesPerPass { get; set; } = 256;
    public int MaxPageUploadsPerTick { get; set; } = 2;
    public int MaxParallelPageLoads { get; set; } = 4;
    public bool UseBatchedNativeDbPageQueries { get; set; } = true;
    public int MaxParallelNativeDbPageBuilds { get; set; } = 6;
    public int SurfaceTileCacheBudget { get; set; } = 4096;
    public int MainThreadUploadBudgetMilliseconds { get; set; } = 4;
    public float BackgroundWorkIntervalSeconds { get; set; } = 0.01f;
    public float PrewarmIntervalSeconds { get; set; } = 2.0f;
    public float PageFlushIntervalSeconds { get; set; } = 5.0f;
    public int PageFlushThreshold { get; set; } = 64;
    public bool LogStats { get; set; } = false;
    public float LogStatsIntervalSeconds { get; set; } = 5.0f;
    public bool LogTrueColorBrightSamples { get; set; } = false;
    public bool EnableTerrainSamplerRainfallLayer { get; set; } = false;
    public bool EnableTerrainSamplerTemperatureLayer { get; set; } = false;
    public bool EnableTerrainSamplerForestDensityLayer { get; set; } = false;
    public bool EnableTerrainSamplerShrubDensityLayer { get; set; } = false;
    public bool EnableHitchDiagnostics { get; set; } = false;
    public int HitchDiagnosticThresholdMilliseconds { get; set; } = 100;
    public bool EnableProfiling { get; set; } = false;
    public bool AutoStartProfilingOnStartup { get; set; } = false;
    public int ProfileAutoFlushIntervalSeconds { get; set; } = 5;

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
        PageMemoryBudget = Math.Clamp(PageMemoryBudget, PageTextureBudget, 20000);
        ReadyPageQueueBudget = Math.Clamp(ReadyPageQueueBudget, 4, 512);
        TerrainSamplerFallbackSampleStep = Math.Clamp(TerrainSamplerFallbackSampleStep, 1, 16);
        TerrainSamplerFallbackResolutionScale = Math.Clamp(TerrainSamplerFallbackResolutionScale, 1, 32);
        TerrainSamplerFallbackTrueColorProbeStride = Math.Clamp(TerrainSamplerFallbackTrueColorProbeStride, 1, 32);
        TerrainSamplerFallbackSnowStartHeight = Math.Clamp(TerrainSamplerFallbackSnowStartHeight, 1, 100000);
        TerrainSamplerFallbackSeasonUploadBucketsPerYear = Math.Clamp(TerrainSamplerFallbackSeasonUploadBucketsPerYear, 1, 128);
        TerrainSamplerFallbackWaterBaseColor = string.IsNullOrWhiteSpace(TerrainSamplerFallbackWaterBaseColor)
            ? "#012d48"
            : TerrainSamplerFallbackWaterBaseColor.Trim();
        TerrainSamplerFallbackWaterRainfallColor = string.IsNullOrWhiteSpace(TerrainSamplerFallbackWaterRainfallColor)
            ? "#82c7df"
            : TerrainSamplerFallbackWaterRainfallColor.Trim();
        TerrainSamplerFallbackWaterRainfallStrength = Math.Clamp(TerrainSamplerFallbackWaterRainfallStrength, 0f, 2f);
        TerrainSamplerFallbackWaterTemperatureColor = string.IsNullOrWhiteSpace(TerrainSamplerFallbackWaterTemperatureColor)
            ? "#b8894f"
            : TerrainSamplerFallbackWaterTemperatureColor.Trim();
        TerrainSamplerFallbackWaterTemperatureStrength = Math.Clamp(TerrainSamplerFallbackWaterTemperatureStrength, 0f, 2f);
        TerrainSamplerFallbackWaterNoiseStrength = Math.Clamp(TerrainSamplerFallbackWaterNoiseStrength, 0f, 1f);
        TrueColorAirSurfaceRepairDepth = Math.Clamp(TrueColorAirSurfaceRepairDepth, 1, 64);
        TerrainSamplerFallbackMaxPagesPerSession = Math.Clamp(TerrainSamplerFallbackMaxPagesPerSession, 0, 100000);
        TerrainSamplerFallbackRadiusChunks = Math.Clamp(TerrainSamplerFallbackRadiusChunks, 0, 8192);
        TerrainSamplerFallbackBackgroundPagesPerPass = Math.Clamp(TerrainSamplerFallbackBackgroundPagesPerPass, 1, 1024);
        TerrainSamplerFallbackMaxParallelBuilds = Math.Clamp(TerrainSamplerFallbackMaxParallelBuilds, 1, 16);
        TerrainSamplerFallbackMaxRetries = Math.Clamp(TerrainSamplerFallbackMaxRetries, 0, 16);
        TerrainSamplerFallbackRetryDelayMilliseconds = Math.Clamp(TerrainSamplerFallbackRetryDelayMilliseconds, 100, 120000);
        WorldMapMinZoomLevel = Math.Clamp(WorldMapMinZoomLevel, 0.01f, 0.25f);
        WorldMapMaxZoomLevel = Math.Clamp(WorldMapMaxZoomLevel, 6.0f, 32.0f);
        WorldMapMaxZoomLevel = Math.Max(WorldMapMaxZoomLevel, WorldMapMinZoomLevel);
        ViewportLoadScale = Math.Clamp(ViewportLoadScale, 1.0f, 4.0f);
        ViewportPageRetentionRings = Math.Clamp(ViewportPageRetentionRings, 0, 32);
        PrewarmRadiusChunks = Math.Clamp(PrewarmRadiusChunks, 0, 64);
        ExperimentalChunkDirtyRepairDelayMilliseconds = Math.Clamp(ExperimentalChunkDirtyRepairDelayMilliseconds, 0, 10000);
        MaxBackgroundTilesPerPass = Math.Clamp(MaxBackgroundTilesPerPass, 1, 1000);
        MaxPageUploadsPerTick = Math.Clamp(MaxPageUploadsPerTick, 1, 32);
        MaxParallelPageLoads = Math.Clamp(MaxParallelPageLoads, 1, 32);
        MaxParallelNativeDbPageBuilds = Math.Clamp(MaxParallelNativeDbPageBuilds, 1, 32);
        SurfaceTileCacheBudget = Math.Clamp(SurfaceTileCacheBudget, 0, 100000);
        MainThreadUploadBudgetMilliseconds = Math.Clamp(MainThreadUploadBudgetMilliseconds, 1, 32);
        BackgroundWorkIntervalSeconds = Math.Clamp(BackgroundWorkIntervalSeconds, 0.01f, 1.0f);
        PrewarmIntervalSeconds = Math.Clamp(PrewarmIntervalSeconds, 0.25f, 30f);
        PageFlushIntervalSeconds = Math.Clamp(PageFlushIntervalSeconds, 0.5f, 120f);
        PageFlushThreshold = Math.Clamp(PageFlushThreshold, 1, 10000);
        LogStatsIntervalSeconds = Math.Clamp(LogStatsIntervalSeconds, 1f, 120f);
        HitchDiagnosticThresholdMilliseconds = Math.Clamp(HitchDiagnosticThresholdMilliseconds, 16, 5000);
        ProfileAutoFlushIntervalSeconds = Math.Clamp(ProfileAutoFlushIntervalSeconds, 1, 120);
    }
}
