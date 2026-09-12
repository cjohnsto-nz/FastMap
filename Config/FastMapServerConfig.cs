namespace FastMap.Config;

public sealed class FastMapServerConfig
{
    public bool EnableTerrainSampling { get; set; } = false;
    // Empty permits everyone; "controlserver" permits administrators only.
    public string RequiredPrivilege { get; set; } = "";
    public int SamplingBudgetMilliseconds { get; set; } = 2;
    public int MaxSamplesPerTick { get; set; } = 256;
    public bool BackgroundSampling { get; set; } = true;
    public bool EnableServerPrewarm { get; set; } = true;
    // Covers a viewport extending about 11,500 blocks either side of its centre.
    public int PrewarmRadiusPages { get; set; } = 12;
    public int PrewarmSampleStep { get; set; } = 4;
    public bool AdaptiveSampling { get; set; } = true;
    public int AdaptiveMaxBudgetMilliseconds { get; set; } = 15;
    public int AdaptiveMaxSamplesPerTick { get; set; } = 16384;
    public int SampleCacheMegabytes { get; set; } = 64;
    public int TileCacheMegabytes { get; set; } = 256;
    public int MaxTransferKilobytesPerTick { get; set; } = 256;
    public bool LogSamplingStats { get; set; } = true;
}
