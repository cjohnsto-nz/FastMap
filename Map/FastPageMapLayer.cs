using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using FastMap.Config;
using FastMap.Profiling;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.Common.Database;
using Vintagestory.GameContent;

namespace FastMap.Map;

public sealed class FastPageMapLayer : RGBMapLayer
{
    private const int ChunkSize = FastMapPageComponent.ChunkSize;
    private const int ChunksPerPage = FastMapPageComponent.ChunksPerPage;
    private const int TilePixelCount = ChunkSize * ChunkSize;
    private const int NativeDbQueryBatchSize = 512;
    private const int PageLoadsPerTask = 64;
    private static readonly FastVec2i[] MinimalDirtyRepairOffsets =
    {
        new(0, 0),
        new(-1, 0),
        new(1, 0),
        new(0, -1),
        new(0, 1),
        new(1, 1)
    };

    private readonly ICoreClientAPI capi;
    private readonly FastMapConfig config;
    private readonly FastMapPageDiskCache pageDiskCache;
    private readonly FastMapTerrainFallbackDiskCache terrainFallbackDiskCache;
    private readonly FastMapTerrainFallbackDiskCache trueColorTerrainFallbackDiskCache;
    private readonly FastMapFallbackPalette fallbackPalette;
    private readonly FastMapTextureAtlas? textureAtlas;
    private readonly FastMapTextureAtlas? fallbackTextureAtlas;
    private readonly Dictionary<FastVec2i, FastMapPageComponent> pages = new();
    private readonly Dictionary<FastVec2i, FastMapPageComponent> fallbackPages = new();
    private readonly HashSet<FastVec2i> visibleChunks = new();
    private readonly HashSet<FastVec2i> visiblePageKeys = new();
    private FastVec2i visibleMinPage;
    private FastVec2i visibleMaxPage;
    private bool hasVisiblePageRect;
    private readonly Queue<FastVec2i> pageUploadQueue = new();
    private readonly HashSet<FastVec2i> queuedPageUploads = new();
    private readonly HashSet<FastVec2i> pagesNeedingUpload = new();

    private readonly object pageLoadLock = new();
    private readonly SortedDictionary<int, Queue<FastVec2i>> pageLoadQueue = new();
    private readonly Dictionary<FastVec2i, int> queuedPageLoadPriorities = new();
    private readonly HashSet<FastVec2i> queuedPageLoads = new();
    private readonly HashSet<FastVec2i> knownMissingPages = new();
    private readonly ConcurrentQueue<FastMapPageSnapshot> readyPages = new();

    private readonly object terrainSamplerLoadLock = new();
    private readonly Queue<FastVec2i> terrainFallbackCacheLoadQueue = new();
    private readonly HashSet<FastVec2i> queuedTerrainFallbackCacheLoads = new();
    private readonly SortedDictionary<int, Queue<FastVec2i>> terrainSamplerLoadQueue = new();
    private readonly SortedDictionary<long, Queue<FastVec2i>> delayedTerrainSamplerLoadQueue = new();
    private readonly HashSet<FastVec2i> queuedTerrainSamplerLoads = new();
    private readonly HashSet<FastVec2i> terrainSamplerFallbackPageKeys = new();
    private readonly Dictionary<FastVec2i, int> terrainSamplerLoadAttempts = new();

    private readonly object repairLock = new();
    private readonly Queue<FastVec2i> repairQueue = new();
    private readonly SortedDictionary<long, Queue<FastVec2i>> delayedRepairQueue = new();
    private readonly HashSet<FastVec2i> queuedRepairs = new();
    private readonly Dictionary<FastVec2i, string> queuedRepairReasons = new();
    private readonly Dictionary<FastVec2i, long> queuedRepairDueMs = new();
    private readonly ConcurrentQueue<FastMapPagePatch> readyPatches = new();
    private readonly HashSet<FastVec2i> chunksKnownValid = new();
    private readonly object chunkValidityLock = new();
#if FASTMAPHITCHDIAGNOSTICS
    private readonly object hitchDiagnosticsLock = new();
    private readonly Dictionary<string, long> queuedRepairDiagnostics = new();
    private readonly Dictionary<string, long> processedRepairDiagnostics = new();
    private readonly Dictionary<string, long> generatedRepairDiagnostics = new();
    private readonly Dictionary<string, long> missingRepairDiagnostics = new();
#endif

    private readonly object dbLock = new();
    private readonly SemaphoreSlim nativeDbPageBuildSemaphore;
    private readonly object pageSaveLock = new();
    private readonly Dictionary<FastVec2i, FastMapPageSnapshot> pendingPageSaves = new();
    private readonly Dictionary<FastVec2i, MapPieceDB> pendingTileSaves = new();

    private MapDB? mapdb;
    private string? mapDbPath;
    private bool mapDbWritable;
    private HashSet<ulong>? mapDbKnownPositions;
    private HashSet<FastVec2i>? mapDbKnownPageKeys;
    private IWorldChunk[] chunksTmp = Array.Empty<IWorldChunk>();
    private Dictionary<string, int> colorsByCode = new();
    private int[] blockColorByBlockId = Array.Empty<int>();
    private bool[] blockIsLakeByBlockId = Array.Empty<bool>();
    private bool[] blockIsSnowByBlockId = Array.Empty<bool>();
    private int fallbackLandColor = unchecked((int)0xFFAC8858);
    private int trueColorAirFallbackColor = unchecked((int)0xFF282828);
    private FastMapTerrainSamplerAdapter? terrainSamplerAdapter;
    private bool terrainSamplerUnavailableLogged;
    private bool terrainSamplerCapabilitiesLogged;
    private readonly ConcurrentDictionary<string, byte> loggedColorAccurateFallbacks = new();
    private readonly ConcurrentDictionary<string, byte> loggedTrueColorBrightSamples = new();
    private readonly object surfaceTileCacheLock = new();
    private readonly Dictionary<FastVec2i, FastMapSurfaceTile> surfaceTileCache = new();
    private bool colorAccurate;
    private float colorRandomizationWeight = 0.6f;
    private float workerAccum;
    private float prewarmAccum;
    private float terrainSamplerBackgroundAccum;
    private float flushAccum;
    private float evictAccum;
    private float statsAccum;
    private int activePageLoadTasks;
    private int activeTerrainSamplerLoadTasks;
    private int activeTerrainFallbackCacheLoadTasks;
#if FASTMAPHITCHDIAGNOSTICS
    private long lastHitchDiagnosticLogMs;
    private int lastHitchGen0Collections;
    private int lastHitchGen1Collections;
    private int lastHitchGen2Collections;
    private long lastHitchTickTimestamp;
    private long lastHitchRenderTimestamp;
#endif

    private long pageDiskHits;
    private long pageDiskMisses;
    private long pageDiskSkippedByIndex;
    private long pageDbHits;
    private long pageDbMisses;
    private long pageDbSkippedByIndex;
    private long pageLoadBatchesStarted;
    private long pageLoadItemsProcessed;
    private long pagePixelBuffersReleased;
    private long pagePixelBufferReloads;
    private long terrainSamplerFallbackPages;
    private long terrainSamplerFallbackQueued;
    private long terrainSamplerFallbackSkipped;
    private long terrainSamplerFallbackSkippedDisabled;
    private long terrainSamplerFallbackSkippedLimit;
    private long terrainSamplerFallbackSkippedRadius;
    private long terrainSamplerFallbackSkippedDuplicate;
    private long terrainSamplerFallbackCacheHits;
    private long terrainSamplerFallbackCacheMisses;
    private long terrainSamplerFallbackBuildFailures;
    private long terrainSamplerFallbackGenerationDeferrals;
    private long terrainSamplerFallbackRetriesQueued;
    private long terrainSamplerFallbackRetriesExhausted;
    private long terrainSamplerFallbackSamples;
    private long terrainSamplerFallbackSeaSamples;
    private long terrainSamplerFallbackNearSeaSamples;
    private long terrainSamplerFallbackTrueColorProbes;
    private long terrainSamplerFallbackTrueColorHits;
    private long terrainSamplerFallbackTrueColorMisses;
    private long terrainSamplerFallbackTrueColorMissingChunk;
    private long terrainSamplerFallbackTrueColorInvalidHeight;
    private long terrainSamplerFallbackTrueColorAir;
    private long terrainSamplerFallbackTrueColorErrors;
    private long pageUploads;
    private long pageSaves;
    private long generatedChunks;
    private long missingSourceChunks;
    private long tileDbHits;
    private long pageLoadMs;
    private long pageUploadMs;
    private long generationMs;
    private bool disposed;

    private readonly struct PageEvictionCandidate
    {
        public PageEvictionCandidate(FastVec2i pageKey, FastMapPageComponent page, bool fallback)
        {
            PageKey = pageKey;
            Page = page;
            Fallback = fallback;
        }

        public FastVec2i PageKey { get; }

        public FastMapPageComponent Page { get; }

        public bool Fallback { get; }
    }

    [ThreadStatic]
    private static byte[]? shadowMapReusable;

    [ThreadStatic]
    private static byte[]? shadowMapCopyReusable;

    [ThreadStatic]
    private static int[]? surfaceHeightReusable;

    [ThreadStatic]
    private static int[]? surfaceChunkYReusable;

    [ThreadStatic]
    private static int[]? surfaceBlockIdReusable;

    public override MapLegendItem[] LegendItems => Array.Empty<MapLegendItem>();

    public override EnumMinMagFilter MinFilter => EnumMinMagFilter.Linear;

    public override EnumMinMagFilter MagFilter => EnumMinMagFilter.Nearest;

    public override string Title => "Terrain";

    public override EnumMapAppSide DataSide => EnumMapAppSide.Client;

    public override string LayerGroupCode => "terrain";

    public FastPageMapLayer(ICoreAPI api, IWorldMapManager mapSink)
        : base(api, mapSink)
    {
        capi = (ICoreClientAPI)api;
        config = FastMapModSystem.Instance?.Config ?? new FastMapConfig();
        config.Normalize();
        nativeDbPageBuildSemaphore = new SemaphoreSlim(config.MaxParallelNativeDbPageBuilds);
        pageDiskCache = new FastMapPageDiskCache(api.World.SavegameIdentifier, config.EnableCompressedCache, config.UseFilteredCache, config.UseHighCompressionCache);
        terrainFallbackDiskCache = new FastMapTerrainFallbackDiskCache(
            api.World.SavegameIdentifier,
            config.TerrainSamplerFallbackResolutionScale,
            config.UseHighCompressionCache,
            NormalFallbackCacheVariant());
        trueColorTerrainFallbackDiskCache = new FastMapTerrainFallbackDiskCache(
            api.World.SavegameIdentifier,
            config.TerrainSamplerFallbackResolutionScale,
            config.UseHighCompressionCache,
            TrueColorFallbackCacheVariant());
        fallbackPalette = FastMapFallbackPalette.Load(api);
        textureAtlas = config.EnableTextureAtlas ? new FastMapTextureAtlas(capi, FastMapPageComponent.PageSize) : null;
        fallbackTextureAtlas = config.EnableTextureAtlas
            ? new FastMapTextureAtlas(capi, FastMapTerrainFallbackDiskCache.LowResolutionSize(config.TerrainSamplerFallbackResolutionScale))
            : null;

        OpenMapDatabase();
        api.Event.ChunkDirty += OnChunkDirty;
        api.Logger.Notification("[FastMap] Page cache: {0}", pageDiskCache.RootPath);
        api.Logger.Notification("[FastMap] Terrain fallback cache: {0}", terrainFallbackDiskCache.RootPath);
        api.Logger.Notification("[FastMap] Terrain true-colour fallback cache: {0}", trueColorTerrainFallbackDiskCache.RootPath);
    }

    private FastMapTerrainFallbackDiskCache CurrentTerrainFallbackDiskCache =>
        colorAccurate && config.EnableTerrainSamplerFallbackTrueColor
            ? trueColorTerrainFallbackDiskCache
            : terrainFallbackDiskCache;

    private static bool TerrainFallbackLayerActive => FastMapTerrainFallbackLayer.IsFallbackLayerActive;

    private string NormalFallbackCacheVariant()
    {
        string palette = config.UseBrownTerrainFallbackPalette ? "brown" : "vanilla";
        return "normal-" + palette + "-v10-h" + config.TerrainSamplerFallbackSnowStartHeight;
    }

    private string TrueColorFallbackCacheVariant()
    {
        string palette = config.UseBrownTerrainFallbackPalette ? "brown" : "palette";
        return "truecolour-" + palette + "-v7-h" + config.TerrainSamplerFallbackSnowStartHeight;
    }

    public override void OnLoaded()
    {
        chunksTmp = new IWorldChunk[Math.Max(1, api.World.BlockAccessor.MapSizeY / ChunkSize)];
        BuildColorLookup();
    }

    public override void OnMapOpenedClient()
    {
        colorAccurate = api.World.Config.GetAsBool("colorAccurateWorldmap", false)
            || Array.IndexOf(capi.World.Player.Privileges, "colorAccurateWorldmap") >= 0;
        colorRandomizationWeight = (float)api.World.Config.GetDecimal("colorRandomizationWeight", 0.6000000238418579);
    }

    public override void OnMapClosedClient()
    {
        visibleChunks.Clear();
        visiblePageKeys.Clear();
    }

    public override void OnViewChangedClient(List<FastVec2i> nowVisible, List<FastVec2i> nowHidden)
    {
        foreach (FastVec2i coord in nowHidden)
        {
            visibleChunks.Remove(coord);
        }

        foreach (FastVec2i coord in nowVisible)
        {
            if (IsValidTile(coord))
            {
                visibleChunks.Add(coord);
            }
        }

        RebuildVisiblePages();
    }

    public override void OnOffThreadTick(float dt)
    {
        if (disposed)
        {
            return;
        }

#if FASTMAPHITCHDIAGNOSTICS
        Stopwatch? hitchStopwatch = StartHitchStopwatch();
#endif
        workerAccum += dt;
        flushAccum += dt;
#if FASTMAPHITCHDIAGNOSTICS
        bool startedPageLoads = false;
        bool processedRepairs = false;
        bool flushedSaves = false;
#endif

        if (workerAccum >= config.BackgroundWorkIntervalSeconds)
        {
            workerAccum = 0f;
            QueueTerrainSamplerBackgroundGeneration(dt);
            StartPageLoadTasks();
            StartTerrainFallbackCacheLoadTasks();
            StartTerrainSamplerLoadTasks();
            ProcessChunkRepairs(config.MaxBackgroundTilesPerPass);
#if FASTMAPHITCHDIAGNOSTICS
            startedPageLoads = true;
            processedRepairs = true;
#endif
        }

        if (flushAccum >= config.PageFlushIntervalSeconds || PendingPageSaveCount() >= config.PageFlushThreshold)
        {
            flushAccum = 0f;
            FlushPendingSaves();
#if FASTMAPHITCHDIAGNOSTICS
            flushedSaves = true;
#endif
        }

#if FASTMAPHITCHDIAGNOSTICS
        LogHitchDiagnostic(
            hitchStopwatch,
            "offthread",
            $"startedPageLoads={startedPageLoads};processedRepairs={processedRepairs};flushedSaves={flushedSaves}");
#endif
    }

    public override void OnTick(float dt)
    {
#if FASTMAPHITCHDIAGNOSTICS
        LogFrameGapDiagnostic(dt, "tick_gap");
        LogWallClockGapDiagnostic(ref lastHitchTickTimestamp, "tick_wall_gap");
#endif
        if (disposed)
        {
            return;
        }

#if FASTMAPHITCHDIAGNOSTICS
        MarkFrameProfiler("fastmap-tick-begin");
        Stopwatch? hitchStopwatch = StartHitchStopwatch();
#endif
        Stopwatch stopwatch = Stopwatch.StartNew();
        ProcessReadyPages(stopwatch);
#if FASTMAPHITCHDIAGNOSTICS
        MarkFrameProfiler("fastmap-ready-pages");
#endif
        ProcessReadyPatches(stopwatch);
#if FASTMAPHITCHDIAGNOSTICS
        MarkFrameProfiler("fastmap-ready-patches");
#endif
        EnsureVisiblePagesQueuedOrUploaded(stopwatch);
#if FASTMAPHITCHDIAGNOSTICS
        MarkFrameProfiler("fastmap-ensure-visible");
#endif
        ProcessQueuedPageUploads(stopwatch);
#if FASTMAPHITCHDIAGNOSTICS
        MarkFrameProfiler("fastmap-page-uploads");
#endif
        StartTerrainSamplerLoadTasks();
        PrewarmAroundPlayer(dt);
#if FASTMAPHITCHDIAGNOSTICS
        MarkFrameProfiler("fastmap-prewarm");
#endif
        EvictPages(dt);
#if FASTMAPHITCHDIAGNOSTICS
        MarkFrameProfiler("fastmap-evict");
#endif
        LogStats(dt);
#if FASTMAPHITCHDIAGNOSTICS
        MarkFrameProfiler("fastmap-stats");
        LogHitchDiagnostic(hitchStopwatch, "tick");
        MarkFrameProfiler("fastmap-tick-end");
#endif
    }

    public override void Render(GuiElementMap mapElem, float dt)
    {
#if FASTMAPHITCHDIAGNOSTICS
        LogFrameGapDiagnostic(dt, "render_gap");
        LogWallClockGapDiagnostic(ref lastHitchRenderTimestamp, "render_wall_gap");
#endif
        if (disposed || !Active)
        {
            return;
        }

#if FASTMAPHITCHDIAGNOSTICS
        MarkFrameProfiler("fastmap-render-begin");
        Stopwatch? hitchStopwatch = StartHitchStopwatch();
        int renderedPages = 0;
#endif
        foreach (FastVec2i pageKey in visiblePageKeys)
        {
            if (TerrainFallbackLayerActive && fallbackPages.TryGetValue(pageKey, out FastMapPageComponent? fallbackPage))
            {
                if (!fallbackPage.HasGpuTexture && fallbackPage.HasPixelBuffer)
                {
                    UploadFallbackPage(fallbackPage);
                }

                if (fallbackPage.HasGpuTexture)
                {
                    fallbackPage.LastTouchedMs = capi.ElapsedMilliseconds;
                    fallbackPage.Render(mapElem, dt);
                }
            }

            if (pages.TryGetValue(pageKey, out FastMapPageComponent? page) && page.HasGpuTexture)
            {
                page.LastTouchedMs = capi.ElapsedMilliseconds;
                page.Render(mapElem, dt);
#if FASTMAPHITCHDIAGNOSTICS
                renderedPages++;
#endif
            }
        }

#if FASTMAPHITCHDIAGNOSTICS
        LogHitchDiagnostic(hitchStopwatch, "render", $"renderedPages={renderedPages}");
        MarkFrameProfiler("fastmap-render-end");
#endif
    }

    public override void OnMouseMoveClient(MouseEvent args, GuiElementMap mapElem, StringBuilder hoverText)
    {
    }

    public override void OnMouseUpClient(MouseEvent args, GuiElementMap mapElem)
    {
    }

    public override void OnShutDown()
    {
        DisposeResources();
    }

    public override void Dispose()
    {
        DisposeResources();
        base.Dispose();
    }

    private void DisposeResources()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        api.Event.ChunkDirty -= OnChunkDirty;
        WaitForTerrainSamplerTasksToStop();
        FlushPendingSaves();
        mapdb?.Dispose();
        mapdb = null;
        mapDbPath = null;
        mapDbWritable = false;
        mapDbKnownPositions = null;

        foreach (FastMapPageComponent page in pages.Values)
        {
            page.DisposeTexture();
        }

        foreach (FastMapPageComponent page in fallbackPages.Values)
        {
            page.DisposeTexture();
        }

        textureAtlas?.Dispose();
        fallbackTextureAtlas?.Dispose();
        pages.Clear();
        fallbackPages.Clear();
        ClearQueues();
        visibleChunks.Clear();
        visiblePageKeys.Clear();
        chunksKnownValid.Clear();
        lock (surfaceTileCacheLock)
        {
            surfaceTileCache.Clear();
        }

        colorsByCode.Clear();
        fallbackLandColor = unchecked((int)0xFFAC8858);
        trueColorAirFallbackColor = unchecked((int)0xFF282828);
        loggedColorAccurateFallbacks.Clear();
        loggedTrueColorBrightSamples.Clear();
        blockColorByBlockId = Array.Empty<int>();
        blockIsLakeByBlockId = Array.Empty<bool>();
        blockIsSnowByBlockId = Array.Empty<bool>();
        chunksTmp = Array.Empty<IWorldChunk>();
    }

    private void WaitForTerrainSamplerTasksToStop()
    {
        const int maxWaitMs = 2000;
        const int sleepMs = 25;

        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < maxWaitMs)
        {
            if (Volatile.Read(ref activeTerrainSamplerLoadTasks) == 0
                && Volatile.Read(ref activeTerrainFallbackCacheLoadTasks) == 0)
            {
                return;
            }

            Thread.Sleep(sleepMs);
        }

        api.Logger.Warning(
            "[FastMap] Terrain sampler work was still active during shutdown after {0}ms; samplerActive={1}, cacheActive={2}. Continuing shutdown without waiting longer.",
            maxWaitMs,
            Volatile.Read(ref activeTerrainSamplerLoadTasks),
            Volatile.Read(ref activeTerrainFallbackCacheLoadTasks));
    }

    private void OpenMapDatabase()
    {
        string mapsDir = Path.Combine(GamePaths.DataPath, "Maps");
        GamePaths.EnsurePathExists(mapsDir);
        string path = Path.Combine(mapsDir, api.World.SavegameIdentifier + ".db");
        mapDbPath = path;

        if (config.CleanupStaleVanillaMapDbSidecarsOnStartup)
        {
            CleanupVanillaMapDbSidecars(path);
        }

        mapdb = new MapDB(api.World.Logger);
        string? error = null;
        bool requestWriteAccess = config.EnableVanillaMapDbWriteback;
        if (!mapdb.OpenOrCreate(path, ref error, requireWriteAccess: requestWriteAccess, corruptionProtection: true, doIntegrityCheck: false))
        {
            api.Logger.Warning(
                "[FastMap] Vanilla map database could not be opened with requested writeback={0}, continuing with read-only map DB access. Generated vanilla tile saves will be skipped. Path: {1}; error: {2}",
                requestWriteAccess,
                path,
                error ?? "unknown");
            mapdb.Dispose();

            mapdb = new MapDB(api.World.Logger);
            error = null;
            if (!mapdb.OpenOrCreate(path, ref error, requireWriteAccess: false, corruptionProtection: true, doIntegrityCheck: false))
            {
                api.Logger.Warning(
                    "[FastMap] Vanilla map database could not be opened; FastMap will skip vanilla map DB reads and fall back to generated/disk pages where possible. Path: {0}; error: {1}",
                    path,
                    error ?? "unknown");
                mapdb.Dispose();
                mapdb = null;
                return;
            }

            mapDbWritable = false;
            return;
        }

        mapDbWritable = requestWriteAccess;
        if (!mapDbWritable)
        {
            api.Logger.Notification("[FastMap] Vanilla map database writeback is disabled; FastMap will read vanilla map tiles but only write FastMap page cache files.");
        }
    }

    private void CleanupVanillaMapDbSidecars(string dbPath)
    {
        DeleteVanillaMapDbSidecar(dbPath + "-wal");
        DeleteVanillaMapDbSidecar(dbPath + "-shm");
    }

    private void DeleteVanillaMapDbSidecar(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
            api.Logger.Notification("[FastMap] Deleted stale vanilla map database sidecar on startup: {0}", path);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[FastMap] Failed deleting stale vanilla map database sidecar {0}: {1}", path, ex.Message);
        }
    }

    private void RebuildVisiblePages()
    {
        visiblePageKeys.Clear();
        hasVisiblePageRect = false;
        if (visibleChunks.Count == 0)
        {
            return;
        }

        int minX = int.MaxValue;
        int minZ = int.MaxValue;
        int maxX = int.MinValue;
        int maxZ = int.MinValue;

        foreach (FastVec2i chunkCoord in visibleChunks)
        {
            minX = Math.Min(minX, chunkCoord.X);
            minZ = Math.Min(minZ, chunkCoord.Y);
            maxX = Math.Max(maxX, chunkCoord.X);
            maxZ = Math.Max(maxZ, chunkCoord.Y);
        }

        float scale = config.ViewportLoadScale;
        int width = maxX - minX + 1;
        int height = maxZ - minZ + 1;
        int padX = (int)Math.Ceiling(width * (scale - 1f) * 0.5f);
        int padZ = (int)Math.Ceiling(height * (scale - 1f) * 0.5f);

        FastVec2i minPage = PageKey(new FastVec2i(minX - padX, minZ - padZ));
        FastVec2i maxPage = PageKey(new FastVec2i(maxX + padX, maxZ + padZ));
        visibleMinPage = minPage;
        visibleMaxPage = maxPage;
        hasVisiblePageRect = true;

        for (int pageZ = minPage.Y; pageZ <= maxPage.Y; pageZ++)
        {
            for (int pageX = minPage.X; pageX <= maxPage.X; pageX++)
            {
                visiblePageKeys.Add(new FastVec2i(pageX, pageZ));
            }
        }

        ReprioritizeViewportQueues();
        foreach (FastVec2i pageKey in visiblePageKeys)
        {
            QueuePageLoad(pageKey);
            if (TerrainFallbackLayerActive)
            {
                QueueVisibleTerrainFallbackCacheLoad(pageKey);
            }

            if (pagesNeedingUpload.Contains(pageKey))
            {
                QueuePageUpload(pageKey);
            }
        }

        StartPageLoadTasks();
        if (TerrainFallbackLayerActive)
        {
            StartTerrainFallbackCacheLoadTasks();
        }
    }

    private void ReprioritizeViewportQueues()
    {
        lock (pageLoadLock)
        {
            ReprioritizeQueuedPageLoadsLocked();
        }

        lock (terrainSamplerLoadLock)
        {
            PruneTerrainFallbackCacheLoadsLocked();
            PruneDelayedTerrainSamplerLoadsLocked();
            ReprioritizeQueuedTerrainSamplerLoadsLocked();
        }
    }

    private void QueuePageLoad(FastVec2i pageKey)
    {
        if (disposed)
        {
            return;
        }

        if (pages.TryGetValue(pageKey, out FastMapPageComponent? page) && page.HasGpuTexture)
        {
            if (pagesNeedingUpload.Contains(pageKey))
            {
                QueuePageUpload(pageKey);
            }

            return;
        }

        lock (pageLoadLock)
        {
            if (knownMissingPages.Contains(pageKey))
            {
                return;
            }

            int priority = PageLoadPriority(pageKey);
            if (!queuedPageLoads.Add(pageKey))
            {
                if (queuedPageLoadPriorities.TryGetValue(pageKey, out int existingPriority) && priority < existingPriority)
                {
                    queuedPageLoadPriorities[pageKey] = priority;
                    EnqueuePageLoadLocked(pageKey, priority);
                }

                return;
            }

            queuedPageLoadPriorities[pageKey] = priority;
            EnqueuePageLoadLocked(pageKey, priority);
        }
    }

    private void EnqueuePageLoadLocked(FastVec2i pageKey, int priority)
    {
        if (!pageLoadQueue.TryGetValue(priority, out Queue<FastVec2i>? queue))
        {
            queue = new Queue<FastVec2i>();
            pageLoadQueue[priority] = queue;
        }

        queue.Enqueue(pageKey);
    }

    private void ReprioritizeQueuedPageLoadsLocked()
    {
        if (queuedPageLoadPriorities.Count == 0)
        {
            pageLoadQueue.Clear();
            return;
        }

        List<FastVec2i> pageKeys = new(queuedPageLoadPriorities.Keys);
        pageLoadQueue.Clear();
        foreach (FastVec2i pageKey in pageKeys)
        {
            if (!ShouldRetainQueuedPageWork(pageKey))
            {
                queuedPageLoads.Remove(pageKey);
                queuedPageLoadPriorities.Remove(pageKey);
                continue;
            }

            int priority = PageLoadPriority(pageKey);
            queuedPageLoadPriorities[pageKey] = priority;
            EnqueuePageLoadLocked(pageKey, priority);
        }
    }

    private int PageLoadPriority(FastVec2i pageKey)
    {
        if (!hasVisiblePageRect)
        {
            return 0;
        }

        int dx = pageKey.X < visibleMinPage.X ? visibleMinPage.X - pageKey.X : pageKey.X > visibleMaxPage.X ? pageKey.X - visibleMaxPage.X : 0;
        int dz = pageKey.Y < visibleMinPage.Y ? visibleMinPage.Y - pageKey.Y : pageKey.Y > visibleMaxPage.Y ? pageKey.Y - visibleMaxPage.Y : 0;
        return Math.Max(dx, dz);
    }

    private bool ShouldRetainQueuedPageWork(FastVec2i pageKey)
    {
        return !hasVisiblePageRect || PageLoadPriority(pageKey) <= config.ViewportPageRetentionRings;
    }

    private void StartPageLoadTasks()
    {
        if (disposed)
        {
            return;
        }

        while (Volatile.Read(ref activePageLoadTasks) < config.MaxParallelPageLoads && HasQueuedPageLoads())
        {
            Interlocked.Increment(ref activePageLoadTasks);
            Interlocked.Increment(ref pageLoadBatchesStarted);
            Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < PageLoadsPerTask && TryDequeuePageLoad(out FastVec2i pageKey); i++)
                    {
                        ProcessPageLoad(pageKey);
                        Interlocked.Increment(ref pageLoadItemsProcessed);
                        if (disposed)
                        {
                            return;
                        }
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref activePageLoadTasks);
                    StartTerrainSamplerLoadTasks();
                }
            });
        }
    }

    private void StartTerrainSamplerLoadTasks()
    {
        if (disposed || !CanStartTerrainSamplerLoads())
        {
            return;
        }

        while (HasReadyPageCapacity()
            && Volatile.Read(ref activeTerrainSamplerLoadTasks) < config.TerrainSamplerFallbackMaxParallelBuilds
            && TryDequeueTerrainSamplerLoad(out FastVec2i pageKey))
        {
            Interlocked.Increment(ref activeTerrainSamplerLoadTasks);
            Task.Run(() =>
            {
                try
                {
                    ProcessTerrainSamplerLoad(pageKey);
                }
                finally
                {
                    Interlocked.Decrement(ref activeTerrainSamplerLoadTasks);
                    StartTerrainSamplerLoadTasks();
                }
            });
        }
    }

    private void StartTerrainFallbackCacheLoadTasks()
    {
        if (disposed || !TerrainFallbackLayerActive)
        {
            return;
        }

        while (HasReadyPageCapacity()
            && Volatile.Read(ref activeTerrainFallbackCacheLoadTasks) < config.TerrainSamplerFallbackMaxParallelBuilds
            && TryDequeueTerrainFallbackCacheLoad(out FastVec2i pageKey))
        {
            Interlocked.Increment(ref activeTerrainFallbackCacheLoadTasks);
            Task.Run(() =>
            {
                try
                {
                    ProcessTerrainFallbackCacheLoad(pageKey);
                }
                finally
                {
                    Interlocked.Decrement(ref activeTerrainFallbackCacheLoadTasks);
                    StartTerrainSamplerLoadTasks();
                }
            });
        }
    }

    private bool CanStartTerrainSamplerLoads()
    {
        return TerrainFallbackLayerActive
            && HasReadyPageCapacity()
            && !HasPendingViewportFillWork();
    }

    private bool HasReadyPageCapacity()
    {
        return readyPages.Count < config.ReadyPageQueueBudget;
    }

    private bool HasPendingViewportFillWork()
    {
        return Volatile.Read(ref activePageLoadTasks) != 0
            || Volatile.Read(ref activeTerrainFallbackCacheLoadTasks) != 0
            || HasQueuedPageLoads()
            || HasQueuedTerrainFallbackCacheLoads();
    }

    private bool HasPendingTruePageWork()
    {
        return Volatile.Read(ref activePageLoadTasks) != 0
            || !readyPages.IsEmpty
            || HasQueuedPageLoads();
    }

    private bool HasQueuedPageLoads()
    {
        lock (pageLoadLock)
        {
            return queuedPageLoadPriorities.Count > 0;
        }
    }

    private bool HasQueuedTerrainFallbackCacheLoads()
    {
        lock (terrainSamplerLoadLock)
        {
            return terrainFallbackCacheLoadQueue.Count > 0;
        }
    }

    private bool TryDequeuePageLoad(out FastVec2i pageKey)
    {
        lock (pageLoadLock)
        {
            while (pageLoadQueue.Count > 0)
            {
                KeyValuePair<int, Queue<FastVec2i>> first = FirstPageLoadBucket();
                FastVec2i candidate = first.Value.Dequeue();
                if (first.Value.Count == 0)
                {
                    pageLoadQueue.Remove(first.Key);
                }

                if (queuedPageLoadPriorities.TryGetValue(candidate, out int currentPriority) && currentPriority == first.Key)
                {
                    queuedPageLoadPriorities.Remove(candidate);
                    pageKey = candidate;
                    return true;
                }
            }
        }

        pageKey = default;
        return false;
    }

    private KeyValuePair<int, Queue<FastVec2i>> FirstPageLoadBucket()
    {
        foreach (KeyValuePair<int, Queue<FastVec2i>> entry in pageLoadQueue)
        {
            return entry;
        }

        throw new InvalidOperationException("Page load queue is empty.");
    }

    private bool TryDequeueTerrainFallbackCacheLoad(out FastVec2i pageKey)
    {
        lock (terrainSamplerLoadLock)
        {
            if (terrainFallbackCacheLoadQueue.Count > 0)
            {
                pageKey = terrainFallbackCacheLoadQueue.Dequeue();
                queuedTerrainFallbackCacheLoads.Remove(pageKey);
                return true;
            }
        }

        pageKey = default;
        return false;
    }

    private bool TryDequeueTerrainSamplerLoad(out FastVec2i pageKey)
    {
        lock (terrainSamplerLoadLock)
        {
            MoveDueTerrainSamplerLoadsToReadyQueue(capi.ElapsedMilliseconds);
            if (terrainSamplerLoadQueue.Count > 0)
            {
                KeyValuePair<int, Queue<FastVec2i>> first = FirstTerrainSamplerLoadBucket();
                pageKey = first.Value.Dequeue();
                if (first.Value.Count == 0)
                {
                    terrainSamplerLoadQueue.Remove(first.Key);
                }

                return true;
            }
        }

        pageKey = default;
        return false;
    }

    private KeyValuePair<int, Queue<FastVec2i>> FirstTerrainSamplerLoadBucket()
    {
        foreach (KeyValuePair<int, Queue<FastVec2i>> entry in terrainSamplerLoadQueue)
        {
            return entry;
        }

        throw new InvalidOperationException("Terrain sampler queue is empty.");
    }

    private void MoveDueTerrainSamplerLoadsToReadyQueue(long nowMs)
    {
        while (delayedTerrainSamplerLoadQueue.Count > 0)
        {
            KeyValuePair<long, Queue<FastVec2i>> first = FirstTerrainSamplerRetryBucket();
            if (first.Key > nowMs)
            {
                return;
            }

            delayedTerrainSamplerLoadQueue.Remove(first.Key);
            while (first.Value.Count > 0)
            {
                FastVec2i pageKey = first.Value.Dequeue();
                if (!ShouldRetainQueuedPageWork(pageKey))
                {
                    terrainSamplerLoadAttempts.Remove(pageKey);
                    queuedTerrainSamplerLoads.Remove(pageKey);
                    continue;
                }

                EnqueueTerrainSamplerLoadLocked(pageKey, preferCacheLoad: visiblePageKeys.Contains(pageKey) && CurrentTerrainFallbackDiskCache.MightContain(pageKey));
            }
        }
    }

    private KeyValuePair<long, Queue<FastVec2i>> FirstTerrainSamplerRetryBucket()
    {
        foreach (KeyValuePair<long, Queue<FastVec2i>> entry in delayedTerrainSamplerLoadQueue)
        {
            return entry;
        }

        throw new InvalidOperationException("Delayed terrain sampler queue is empty.");
    }

    private void ProcessPageLoad(FastVec2i pageKey)
    {
        if (disposed || !ShouldRetainQueuedPageWork(pageKey))
        {
            lock (pageLoadLock)
            {
                queuedPageLoads.Remove(pageKey);
                queuedPageLoadPriorities.Remove(pageKey);
            }

            return;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        bool diskMightContain = pageDiskCache.MightContain(pageKey);
        if (diskMightContain && pageDiskCache.TryLoad(pageKey, out FastMapPageSnapshot diskSnapshot))
        {
            if (disposed)
            {
                return;
            }

            Interlocked.Increment(ref pageDiskHits);
            Interlocked.Add(ref pageLoadMs, stopwatch.ElapsedMilliseconds);
            FastMapProfileRecorder.RecordClient("fastmap_page_disk_load", pageKey.X, 0, pageKey.Y, stopwatch.Elapsed.TotalMilliseconds, detail: "hit");
            readyPages.Enqueue(diskSnapshot);
            QueueTerrainSamplerLoadIfIncomplete(diskSnapshot);
            return;
        }

        if (diskMightContain)
        {
            Interlocked.Increment(ref pageDiskMisses);
        }
        else
        {
            Interlocked.Increment(ref pageDiskSkippedByIndex);
        }

        if (TryBuildPageFromDb(pageKey, out FastMapPageSnapshot dbSnapshot, out bool skippedByDbIndex))
        {
            if (disposed)
            {
                return;
            }

            Interlocked.Increment(ref pageDbHits);
            Interlocked.Add(ref pageLoadMs, stopwatch.ElapsedMilliseconds);
            FastMapProfileRecorder.RecordClient(
                "fastmap_page_db_build",
                pageKey.X,
                0,
                pageKey.Y,
                stopwatch.Elapsed.TotalMilliseconds,
                detail: "hit",
                kind: "inclusive");
            QueuePageSave(dbSnapshot);
            readyPages.Enqueue(dbSnapshot);
            QueueTerrainSamplerLoadIfIncomplete(dbSnapshot);
            return;
        }

        string missDetail = skippedByDbIndex && !diskMightContain ? "indexskip" : "miss";
        FastMapProfileRecorder.RecordClient("fastmap_page_load", pageKey.X, 0, pageKey.Y, stopwatch.Elapsed.TotalMilliseconds, detail: missDetail);
        QueueTerrainSamplerLoad(pageKey);

        if (!skippedByDbIndex)
        {
            Interlocked.Increment(ref pageDbMisses);
        }

        Interlocked.Add(ref pageLoadMs, stopwatch.ElapsedMilliseconds);
        if (disposed)
        {
            return;
        }

        lock (pageLoadLock)
        {
            knownMissingPages.Add(pageKey);
            queuedPageLoads.Remove(pageKey);
        }
    }

    private void QueueTerrainSamplerLoadIfIncomplete(FastMapPageSnapshot snapshot)
    {
        if (!snapshot.Synthetic && !IsCompletePage(snapshot.ValidRows))
        {
            QueueTerrainSamplerLoad(snapshot.PageKey);
        }
    }

    private bool QueueVisibleTerrainFallbackCacheLoad(FastVec2i pageKey)
    {
        if (!TerrainFallbackLayerActive)
        {
            return false;
        }

        if (!CurrentTerrainFallbackDiskCache.MightContain(pageKey))
        {
            return false;
        }

        if (fallbackPages.TryGetValue(pageKey, out FastMapPageComponent? fallbackPage) && fallbackPage.HasGpuTexture)
        {
            return true;
        }

        lock (terrainSamplerLoadLock)
        {
            if (queuedTerrainFallbackCacheLoads.Add(pageKey))
            {
                terrainFallbackCacheLoadQueue.Enqueue(pageKey);
            }
        }

        return true;
    }

    private void QueueTerrainSamplerLoad(FastVec2i pageKey, bool preferCacheLoad = false)
    {
        if (disposed)
        {
            Interlocked.Increment(ref terrainSamplerFallbackSkipped);
            return;
        }

        if (!ShouldRetainQueuedPageWork(pageKey))
        {
            Interlocked.Increment(ref terrainSamplerFallbackSkipped);
            return;
        }

        if (!CanUseTerrainSamplerFallback(out TerrainSamplerSkipReason skipReason) || !PageIntersectsTerrainSamplerRadius(pageKey))
        {
            IncrementTerrainSamplerSkip(skipReason == TerrainSamplerSkipReason.None ? TerrainSamplerSkipReason.Radius : skipReason);
            return;
        }

        lock (terrainSamplerLoadLock)
        {
            if (!TryReserveTerrainSamplerFallbackPageLocked(pageKey))
            {
                IncrementTerrainSamplerSkip(TerrainSamplerSkipReason.Limit);
                return;
            }

            if (queuedTerrainSamplerLoads.Add(pageKey))
            {
                EnqueueTerrainSamplerLoadLocked(pageKey, preferCacheLoad);
                Interlocked.Increment(ref terrainSamplerFallbackQueued);
            }
            else
            {
                IncrementTerrainSamplerSkip(TerrainSamplerSkipReason.Duplicate);
            }
        }
    }

    private bool TryReserveTerrainSamplerFallbackPageLocked(FastVec2i pageKey)
    {
        if (terrainSamplerFallbackPageKeys.Contains(pageKey))
        {
            return true;
        }

        if (terrainSamplerFallbackPageKeys.Count >= config.TerrainSamplerFallbackMaxPagesPerSession)
        {
            return false;
        }

        terrainSamplerFallbackPageKeys.Add(pageKey);
        return true;
    }

    private bool IsTerrainSamplerFallbackPageReserved(FastVec2i pageKey)
    {
        lock (terrainSamplerLoadLock)
        {
            return terrainSamplerFallbackPageKeys.Contains(pageKey);
        }
    }

    private void EnqueueTerrainSamplerLoadLocked(FastVec2i pageKey, bool preferCacheLoad = false)
    {
        int priority = TerrainSamplerPagePriority(pageKey, preferCacheLoad);
        if (!terrainSamplerLoadQueue.TryGetValue(priority, out Queue<FastVec2i>? queue))
        {
            queue = new Queue<FastVec2i>();
            terrainSamplerLoadQueue[priority] = queue;
        }

        queue.Enqueue(pageKey);
    }

    private void ReprioritizeQueuedTerrainSamplerLoadsLocked()
    {
        if (terrainSamplerLoadQueue.Count == 0)
        {
            return;
        }

        List<FastVec2i> pageKeys = new();
        foreach (Queue<FastVec2i> queue in terrainSamplerLoadQueue.Values)
        {
            pageKeys.AddRange(queue);
        }

        terrainSamplerLoadQueue.Clear();
        foreach (FastVec2i pageKey in pageKeys)
        {
            if (!ShouldRetainQueuedPageWork(pageKey))
            {
                queuedTerrainSamplerLoads.Remove(pageKey);
                terrainSamplerLoadAttempts.Remove(pageKey);
                continue;
            }

            EnqueueTerrainSamplerLoadLocked(pageKey, preferCacheLoad: false);
        }
    }

    private void PruneTerrainFallbackCacheLoadsLocked()
    {
        if (terrainFallbackCacheLoadQueue.Count == 0)
        {
            return;
        }

        Queue<FastVec2i> currentViewportLoads = new();
        while (terrainFallbackCacheLoadQueue.Count > 0)
        {
            FastVec2i pageKey = terrainFallbackCacheLoadQueue.Dequeue();
            if (ShouldRetainQueuedPageWork(pageKey))
            {
                currentViewportLoads.Enqueue(pageKey);
            }
            else
            {
                queuedTerrainFallbackCacheLoads.Remove(pageKey);
            }
        }

        while (currentViewportLoads.Count > 0)
        {
            terrainFallbackCacheLoadQueue.Enqueue(currentViewportLoads.Dequeue());
        }
    }

    private void PruneDelayedTerrainSamplerLoadsLocked()
    {
        if (delayedTerrainSamplerLoadQueue.Count == 0)
        {
            return;
        }

        List<long> emptyBuckets = new();
        List<KeyValuePair<long, Queue<FastVec2i>>> retainedBuckets = new();
        foreach (KeyValuePair<long, Queue<FastVec2i>> entry in delayedTerrainSamplerLoadQueue)
        {
            Queue<FastVec2i> retainedAtTime = new();
            while (entry.Value.Count > 0)
            {
                FastVec2i pageKey = entry.Value.Dequeue();
                if (ShouldRetainQueuedPageWork(pageKey))
                {
                    retainedAtTime.Enqueue(pageKey);
                }
                else
                {
                    queuedTerrainSamplerLoads.Remove(pageKey);
                    terrainSamplerLoadAttempts.Remove(pageKey);
                }
            }

            if (retainedAtTime.Count == 0)
            {
                emptyBuckets.Add(entry.Key);
            }
            else
            {
                retainedBuckets.Add(new KeyValuePair<long, Queue<FastVec2i>>(entry.Key, retainedAtTime));
            }
        }

        foreach (long dueMs in emptyBuckets)
        {
            delayedTerrainSamplerLoadQueue.Remove(dueMs);
        }

        foreach (KeyValuePair<long, Queue<FastVec2i>> entry in retainedBuckets)
        {
            delayedTerrainSamplerLoadQueue[entry.Key] = entry.Value;
        }
    }

    private int TerrainSamplerPagePriority(FastVec2i pageKey, bool preferCacheLoad)
    {
        int basePriority = preferCacheLoad && visiblePageKeys.Contains(pageKey) && CurrentTerrainFallbackDiskCache.MightContain(pageKey)
            ? -100000
            : 0;

        if (!hasVisiblePageRect)
        {
            return basePriority;
        }

        int dx = pageKey.X < visibleMinPage.X ? visibleMinPage.X - pageKey.X : pageKey.X > visibleMaxPage.X ? pageKey.X - visibleMaxPage.X : 0;
        int dz = pageKey.Y < visibleMinPage.Y ? visibleMinPage.Y - pageKey.Y : pageKey.Y > visibleMaxPage.Y ? pageKey.Y - visibleMaxPage.Y : 0;
        return basePriority + Math.Max(dx, dz);
    }

    private bool ShouldQueueTerrainSamplerLoad(FastVec2i pageKey)
    {
        return CanUseTerrainSamplerFallback(out _) && PageIntersectsTerrainSamplerRadius(pageKey);
    }

    private bool CanUseTerrainSamplerFallback(out TerrainSamplerSkipReason skipReason)
    {
        if (!TerrainFallbackLayerActive)
        {
            skipReason = TerrainSamplerSkipReason.Disabled;
            return false;
        }

        if (config.TerrainSamplerFallbackMaxPagesPerSession <= 0)
        {
            skipReason = TerrainSamplerSkipReason.Limit;
            return false;
        }

        skipReason = TerrainSamplerSkipReason.None;
        return true;
    }

    private int CountDelayedTerrainSamplerLoadsLocked()
    {
        int count = 0;
        foreach (Queue<FastVec2i> queue in delayedTerrainSamplerLoadQueue.Values)
        {
            count += queue.Count;
        }

        return count;
    }

    private int CountTerrainSamplerLoadsLocked()
    {
        int count = 0;
        foreach (Queue<FastVec2i> queue in terrainSamplerLoadQueue.Values)
        {
            count += queue.Count;
        }

        return count;
    }

    private void IncrementTerrainSamplerSkip(TerrainSamplerSkipReason reason)
    {
        Interlocked.Increment(ref terrainSamplerFallbackSkipped);
        switch (reason)
        {
            case TerrainSamplerSkipReason.Disabled:
                Interlocked.Increment(ref terrainSamplerFallbackSkippedDisabled);
                break;
            case TerrainSamplerSkipReason.Limit:
                Interlocked.Increment(ref terrainSamplerFallbackSkippedLimit);
                break;
            case TerrainSamplerSkipReason.Radius:
                Interlocked.Increment(ref terrainSamplerFallbackSkippedRadius);
                break;
            case TerrainSamplerSkipReason.Duplicate:
                Interlocked.Increment(ref terrainSamplerFallbackSkippedDuplicate);
                break;
        }
    }

    private bool PageIntersectsTerrainSamplerRadius(FastVec2i pageKey)
    {
        int radius = config.TerrainSamplerFallbackRadiusChunks;
        if (radius <= 0)
        {
            return true;
        }

        EntityPlayer? player = capi.World.Player?.Entity;
        if (player == null)
        {
            return false;
        }

        BlockPos playerPos = player.Pos.AsBlockPos;
        int playerChunkX = FloorDiv(playerPos.X, ChunkSize);
        int playerChunkZ = FloorDiv(playerPos.Z, ChunkSize);
        int minChunkX = pageKey.X * ChunksPerPage;
        int minChunkZ = pageKey.Y * ChunksPerPage;
        int maxChunkX = minChunkX + ChunksPerPage - 1;
        int maxChunkZ = minChunkZ + ChunksPerPage - 1;
        int dx = playerChunkX < minChunkX ? minChunkX - playerChunkX : playerChunkX > maxChunkX ? playerChunkX - maxChunkX : 0;
        int dz = playerChunkZ < minChunkZ ? minChunkZ - playerChunkZ : playerChunkZ > maxChunkZ ? playerChunkZ - maxChunkZ : 0;
        long radiusSquared = (long)radius * radius;
        return (long)dx * dx + (long)dz * dz <= radiusSquared;
    }

    private void ProcessTerrainSamplerLoad(FastVec2i pageKey)
    {
        if (disposed || !ShouldRetainQueuedPageWork(pageKey) || !ShouldQueueTerrainSamplerLoad(pageKey))
        {
            FinishTerrainSamplerLoad(pageKey);
            return;
        }

        if (!HasReadyPageCapacity())
        {
            RequeueTerrainSamplerLoad(pageKey);
            return;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        FastMapTerrainFallbackDiskCache fallbackDiskCache = CurrentTerrainFallbackDiskCache;
        if (fallbackDiskCache.MightContain(pageKey) && fallbackDiskCache.TryLoad(pageKey, out FastMapPageSnapshot cachedSnapshot))
        {
            if (!disposed)
            {
                Interlocked.Increment(ref terrainSamplerFallbackPages);
                Interlocked.Increment(ref terrainSamplerFallbackCacheHits);
                FastMapProfileRecorder.RecordClient(
                    "fastmap_page_terrain_sampler_cache_load",
                    pageKey.X,
                    0,
                    pageKey.Y,
                    stopwatch.Elapsed.TotalMilliseconds,
                    cachedSnapshot.Pixels.Length * sizeof(int),
                    detail: "synthetic",
                    kind: "inclusive");
                readyPages.Enqueue(cachedSnapshot);
            }

            FinishTerrainSamplerLoad(pageKey);
            return;
        }

        Interlocked.Increment(ref terrainSamplerFallbackCacheMisses);
        if (HasPendingViewportFillWork())
        {
            Interlocked.Increment(ref terrainSamplerFallbackGenerationDeferrals);
            RequeueTerrainSamplerLoad(pageKey);
            return;
        }

        if (!TryBuildPageFromTerrainSampler(pageKey, out FastMapPageSnapshot sampledSnapshot))
        {
            Interlocked.Increment(ref terrainSamplerFallbackBuildFailures);
            RetryOrFinishTerrainSamplerLoad(pageKey);
            return;
        }

        if (disposed)
        {
            FinishTerrainSamplerLoad(pageKey);
            return;
        }

        Interlocked.Increment(ref terrainSamplerFallbackPages);
        FastMapProfileRecorder.RecordClient(
            "fastmap_page_terrain_sampler_build",
            pageKey.X,
            0,
            pageKey.Y,
            stopwatch.Elapsed.TotalMilliseconds,
            sampledSnapshot.Pixels.Length * sizeof(int),
            detail: "synthetic",
            kind: "inclusive");
        readyPages.Enqueue(sampledSnapshot);
        FinishTerrainSamplerLoad(pageKey);
    }

    private void ProcessTerrainFallbackCacheLoad(FastVec2i pageKey)
    {
        FastMapTerrainFallbackDiskCache fallbackDiskCache = CurrentTerrainFallbackDiskCache;
        if (disposed || !ShouldRetainQueuedPageWork(pageKey) || !fallbackDiskCache.MightContain(pageKey))
        {
            return;
        }

        if (!HasReadyPageCapacity())
        {
            RequeueTerrainFallbackCacheLoad(pageKey);
            return;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        if (!fallbackDiskCache.TryLoad(pageKey, out FastMapPageSnapshot cachedSnapshot))
        {
            Interlocked.Increment(ref terrainSamplerFallbackCacheMisses);
            QueueTerrainSamplerLoad(pageKey);
            return;
        }

        if (disposed)
        {
            return;
        }

        Interlocked.Increment(ref terrainSamplerFallbackPages);
        Interlocked.Increment(ref terrainSamplerFallbackCacheHits);
        FastMapProfileRecorder.RecordClient(
            "fastmap_page_terrain_sampler_cache_load",
            pageKey.X,
            0,
            pageKey.Y,
            stopwatch.Elapsed.TotalMilliseconds,
            cachedSnapshot.Pixels.Length * sizeof(int),
            detail: "synthetic-priority-cache",
            kind: "inclusive");
        readyPages.Enqueue(cachedSnapshot);
    }

    private void RequeueTerrainFallbackCacheLoad(FastVec2i pageKey)
    {
        lock (terrainSamplerLoadLock)
        {
            if (ShouldRetainQueuedPageWork(pageKey) && queuedTerrainFallbackCacheLoads.Add(pageKey))
            {
                terrainFallbackCacheLoadQueue.Enqueue(pageKey);
            }
        }
    }

    private void RetryOrFinishTerrainSamplerLoad(FastVec2i pageKey)
    {
        lock (terrainSamplerLoadLock)
        {
            if (!ShouldRetainQueuedPageWork(pageKey))
            {
                terrainSamplerLoadAttempts.Remove(pageKey);
                queuedTerrainSamplerLoads.Remove(pageKey);
                return;
            }

            terrainSamplerLoadAttempts.TryGetValue(pageKey, out int attempts);
            attempts++;
            if (attempts > config.TerrainSamplerFallbackMaxRetries)
            {
                terrainSamplerLoadAttempts.Remove(pageKey);
                queuedTerrainSamplerLoads.Remove(pageKey);
                Interlocked.Increment(ref terrainSamplerFallbackRetriesExhausted);
                if (config.LogStats)
                {
                    api.Logger.Notification("[FastMap] Terrain fallback page {0}/{1} failed after {2} attempts.", pageKey.X, pageKey.Y, attempts);
                }

                return;
            }

            terrainSamplerLoadAttempts[pageKey] = attempts;
            long dueMs = capi.ElapsedMilliseconds + config.TerrainSamplerFallbackRetryDelayMilliseconds;
            if (!delayedTerrainSamplerLoadQueue.TryGetValue(dueMs, out Queue<FastVec2i>? delayedAtTime))
            {
                delayedAtTime = new Queue<FastVec2i>();
                delayedTerrainSamplerLoadQueue[dueMs] = delayedAtTime;
            }

            delayedAtTime.Enqueue(pageKey);
            Interlocked.Increment(ref terrainSamplerFallbackRetriesQueued);
            if (config.LogStats)
            {
                api.Logger.Notification("[FastMap] Terrain fallback page {0}/{1} failed; retry {2}/{3} queued.", pageKey.X, pageKey.Y, attempts, config.TerrainSamplerFallbackMaxRetries);
            }
        }
    }

    private void RequeueTerrainSamplerLoad(FastVec2i pageKey)
    {
        lock (terrainSamplerLoadLock)
        {
            if (!ShouldRetainQueuedPageWork(pageKey))
            {
                terrainSamplerLoadAttempts.Remove(pageKey);
                queuedTerrainSamplerLoads.Remove(pageKey);
                return;
            }

            EnqueueTerrainSamplerLoadLocked(pageKey, preferCacheLoad: false);
        }
    }

    private void FinishTerrainSamplerLoad(FastVec2i pageKey)
    {
        lock (terrainSamplerLoadLock)
        {
            terrainSamplerLoadAttempts.Remove(pageKey);
            queuedTerrainSamplerLoads.Remove(pageKey);
        }
    }

    private enum TerrainSamplerSkipReason
    {
        None,
        Disabled,
        Limit,
        Radius,
        Duplicate
    }

    private bool TryBuildPageFromTerrainSampler(FastVec2i pageKey, out FastMapPageSnapshot snapshot)
    {
        snapshot = null!;
        if (!ShouldQueueTerrainSamplerLoad(pageKey))
        {
            return false;
        }

        FastMapTerrainSamplerAdapter? sampler = terrainSamplerAdapter ??= FastMapTerrainSamplerAdapter.TryCreate();
        if (sampler == null)
        {
            if (!terrainSamplerUnavailableLogged)
            {
                terrainSamplerUnavailableLogged = true;
                api.Logger.Notification("[FastMap] Terrain sampler fallback maps are enabled, but Algernon's Terrain Sampler was not available in this process.");
            }

            return false;
        }

        if (!terrainSamplerCapabilitiesLogged)
        {
            terrainSamplerCapabilitiesLogged = true;
            api.Logger.Notification(
                "[FastMap] Terrain sampler fallback using {0} column samples.",
                sampler.HasColumnSamples ? "height+climate" : "height-only");
        }

        if (!TryBuildTerrainSamplerPage(pageKey, sampler, out int[] lowResolutionPixels))
        {
            return false;
        }

        CurrentTerrainFallbackDiskCache.Save(pageKey, lowResolutionPixels);
        snapshot = FastMapTerrainFallbackDiskCache.CreateSnapshot(pageKey, lowResolutionPixels, config.TerrainSamplerFallbackResolutionScale);
        return true;
    }

    private bool TryBuildTerrainSamplerPage(FastVec2i pageKey, FastMapTerrainSamplerAdapter sampler, out int[] lowResolutionPixels)
    {
        int sampleStep = config.TerrainSamplerFallbackResolutionScale;
        int pageSize = FastMapPageComponent.PageSize;
        int cellsPerAxis = (pageSize + sampleStep - 1) / sampleStep;
        int baseBlockX = pageKey.X * pageSize;
        int baseBlockZ = pageKey.Y * pageSize;
        int seaLevel = api.World.SeaLevel;
        int landColor = fallbackLandColor;
        int waterColor = colorsByCode.TryGetValue("ocean", out int ocean) ? ocean : landColor;
        int waterEdgeColor = colorsByCode.TryGetValue("wateredge", out int edge) ? edge : waterColor;
        int[]? previousRow = null;
        int[] currentRow = new int[cellsPerAxis];
        lowResolutionPixels = new int[cellsPerAxis * cellsPerAxis];
        bool usePaletteFallback = colorAccurate && config.EnableTerrainSamplerFallbackTrueColor;
        bool useBrownFallback = config.UseBrownTerrainFallbackPalette;
        bool useTrueColorProbes = false;
        int trueColorProbeStride = Math.Max(1, config.TerrainSamplerFallbackTrueColorProbeStride);
        int trueColorProbeCellsPerAxis = useTrueColorProbes ? (cellsPerAxis + trueColorProbeStride - 1) / trueColorProbeStride : 0;
        bool[] trueColorProbeAttempted = useTrueColorProbes ? new bool[trueColorProbeCellsPerAxis * trueColorProbeCellsPerAxis] : Array.Empty<bool>();
        int[] trueColorProbeColors = useTrueColorProbes ? new int[trueColorProbeAttempted.Length] : Array.Empty<int>();
        int seaSamples = 0;
        int nearSeaSamples = 0;
        long trueColorProbes = 0;
        long trueColorHits = 0;
        long trueColorMisses = 0;

        for (int cellZ = 0; cellZ < cellsPerAxis; cellZ++)
        {
            if (disposed)
            {
                return false;
            }

            int localZ = cellZ * sampleStep;
            int worldZ = baseBlockZ + localZ;
            for (int cellX = 0; cellX < cellsPerAxis; cellX++)
            {
                if (disposed)
                {
                    return false;
                }

                int localX = cellX * sampleStep;
                int worldX = baseBlockX + localX;
                FastMapTerrainSamplerColumn terrainSample = sampler.SampleColumn(worldX, worldZ);
                int height = terrainSample.Height;
                currentRow[cellX] = height;
                if (height <= seaLevel)
                {
                    seaSamples++;
                }
                else if (height <= seaLevel + 2)
                {
                    nearSeaSamples++;
                }

                int westHeight = cellX > 0 ? currentRow[cellX - 1] : sampler.GetBlockColumnHeight(worldX - sampleStep, worldZ);
                int northHeight = previousRow != null ? previousRow[cellX] : sampler.GetBlockColumnHeight(worldX, worldZ - sampleStep);
                int diagonalHeight = cellX > 0 && previousRow != null
                    ? previousRow[cellX - 1]
                    : sampler.GetBlockColumnHeight(worldX - sampleStep, worldZ - sampleStep);

                int color;
                if (useBrownFallback && fallbackPalette.TryGetBrownColor(height, worldX, worldZ, out int brownColor))
                {
                    bool flattenBrownColor = height <= seaLevel - 2;
                    int brownFallbackColor = TrueColorFallbackBrownColor(brownColor, height, seaLevel, worldX, worldZ, flattenBrownColor);
                    brownFallbackColor = TerrainSamplerBrownClimateTintColor(brownFallbackColor, terrainSample, flattenBrownColor, seaLevel);
                    color = flattenBrownColor
                        ? brownFallbackColor
                        : TerrainSamplerShadeColor(brownFallbackColor, height, westHeight, northHeight, diagonalHeight, seaLevel);
                }
                else if (usePaletteFallback)
                {
                    bool paletteSnow = IsTerrainSamplerSnow(terrainSample, height, seaLevel);
                    GetTerrainSamplerGrassSpectrumWeights(terrainSample, height, seaLevel, out float dryGrassWeight, out float lushGrassWeight);
                    if (!fallbackPalette.TryGetColor(
                        height,
                        seaLevel,
                        paletteSnow,
                        dryGrassWeight,
                        lushGrassWeight,
                        worldX,
                        worldZ,
                        out int paletteColor,
                        out bool flattenPaletteColor))
                    {
                        return false;
                    }

                    if (!flattenPaletteColor && !paletteSnow)
                    {
                        paletteColor = TerrainSamplerPaletteClimateTintColor(paletteColor, terrainSample, worldX, worldZ, seaLevel);
                    }

                    color = flattenPaletteColor
                        ? paletteColor
                        : TerrainSamplerShadeColor(paletteColor, height, westHeight, northHeight, diagonalHeight, seaLevel);
                }
                else
                {
                    int climateLandColor = TerrainSamplerClimateTintColor(landColor, terrainSample, water: false, worldX, worldZ, seaLevel);
                    color = TerrainSamplerColor(height, westHeight, northHeight, diagonalHeight, seaLevel, climateLandColor, waterColor, waterEdgeColor);
                    color = TerrainSamplerColdSnowTintColor(color, terrainSample, height, westHeight, northHeight, diagonalHeight, seaLevel, worldX, worldZ);
                }

                if (useTrueColorProbes
                    && height > seaLevel
                    && TryGetCachedTerrainSamplerTrueColor(
                        cellX,
                        cellZ,
                        trueColorProbeStride,
                        trueColorProbeCellsPerAxis,
                        baseBlockX,
                        baseBlockZ,
                        sampleStep,
                        sampler,
                        trueColorProbeAttempted,
                        trueColorProbeColors,
                        ref trueColorProbes,
                        ref trueColorHits,
                        ref trueColorMisses,
                        out int trueColor))
                {
                    color = TerrainSamplerShadeColor(trueColor, height, westHeight, northHeight, diagonalHeight, seaLevel);
                }

                lowResolutionPixels[cellZ * cellsPerAxis + cellX] = color;
            }

            int[] completedRow = currentRow;
            currentRow = previousRow ?? new int[cellsPerAxis];
            previousRow = completedRow;
        }

        Interlocked.Add(ref terrainSamplerFallbackSamples, cellsPerAxis * cellsPerAxis);
        Interlocked.Add(ref terrainSamplerFallbackSeaSamples, seaSamples);
        Interlocked.Add(ref terrainSamplerFallbackNearSeaSamples, nearSeaSamples);
        Interlocked.Add(ref terrainSamplerFallbackTrueColorProbes, trueColorProbes);
        Interlocked.Add(ref terrainSamplerFallbackTrueColorHits, trueColorHits);
        Interlocked.Add(ref terrainSamplerFallbackTrueColorMisses, trueColorMisses);
        return true;
    }

    private bool TryGetCachedTerrainSamplerTrueColor(
        int cellX,
        int cellZ,
        int probeStride,
        int probeCellsPerAxis,
        int baseBlockX,
        int baseBlockZ,
        int sampleStep,
        FastMapTerrainSamplerAdapter sampler,
        bool[] attempted,
        int[] colors,
        ref long probes,
        ref long hits,
        ref long misses,
        out int color)
    {
        int probeX = cellX / probeStride;
        int probeZ = cellZ / probeStride;
        int probeIndex = probeZ * probeCellsPerAxis + probeX;
        if (!attempted[probeIndex])
        {
            attempted[probeIndex] = true;
            probes++;

            int sampleCellX = probeX * probeStride;
            int sampleCellZ = probeZ * probeStride;
            int worldX = baseBlockX + sampleCellX * sampleStep;
            int worldZ = baseBlockZ + sampleCellZ * sampleStep;
            int height = sampler.GetBlockColumnHeight(worldX, worldZ);
            TerrainSamplerTrueColorProbeResult result = TryGetTerrainSamplerSurfaceTrueColor(worldX, height, worldZ, out int sampledColor);
            if (result == TerrainSamplerTrueColorProbeResult.Hit)
            {
                colors[probeIndex] = sampledColor | unchecked((int)0xFF000000);
                hits++;
            }
            else
            {
                misses++;
                IncrementTerrainSamplerTrueColorMiss(result);
            }
        }

        color = colors[probeIndex];
        return color != 0;
    }

    private TerrainSamplerTrueColorProbeResult TryGetTerrainSamplerSurfaceTrueColor(int worldX, int height, int worldZ, out int color)
    {
        color = 0;
        int chunkX = FloorDiv(worldX, ChunkSize);
        int chunkZ = FloorDiv(worldZ, ChunkSize);
        int localX = PositiveMod(worldX, ChunkSize);
        int localZ = PositiveMod(worldZ, ChunkSize);
        bool sawAir = false;

        for (int dy = 0; dy >= -3; dy--)
        {
            int sampleY = height + dy;
            int chunkY = FloorDiv(sampleY, ChunkSize);
            if ((uint)chunkY >= (uint)chunksTmp.Length)
            {
                continue;
            }

            IWorldChunk chunk = capi.World.BlockAccessor.GetChunk(chunkX, chunkY, chunkZ);
            if (chunk is not IClientChunk clientChunk || !clientChunk.LoadedFromServer)
            {
                return TerrainSamplerTrueColorProbeResult.MissingChunk;
            }

            chunk.Unpack_ReadOnly();
            int blockId = ReadBlockId(chunk, localX, PositiveMod(sampleY, ChunkSize), localZ);
            if (blockId <= 0 || (uint)blockId >= (uint)api.World.Blocks.Count)
            {
                sawAir = true;
                continue;
            }

            try
            {
                Block block = api.World.Blocks[blockId];
                BlockPos blockPos = new(0);
                blockPos.Set(worldX, sampleY, worldZ);
                color = GetColorAccurateMapColor(block, blockId, blockPos, new FastVec2i(chunkX, chunkZ));
                return TerrainSamplerTrueColorProbeResult.Hit;
            }
            catch
            {
                return TerrainSamplerTrueColorProbeResult.ColorError;
            }
        }

        return sawAir ? TerrainSamplerTrueColorProbeResult.Air : TerrainSamplerTrueColorProbeResult.InvalidHeight;
    }

    private void IncrementTerrainSamplerTrueColorMiss(TerrainSamplerTrueColorProbeResult result)
    {
        switch (result)
        {
            case TerrainSamplerTrueColorProbeResult.MissingChunk:
                Interlocked.Increment(ref terrainSamplerFallbackTrueColorMissingChunk);
                break;
            case TerrainSamplerTrueColorProbeResult.InvalidHeight:
                Interlocked.Increment(ref terrainSamplerFallbackTrueColorInvalidHeight);
                break;
            case TerrainSamplerTrueColorProbeResult.Air:
                Interlocked.Increment(ref terrainSamplerFallbackTrueColorAir);
                break;
            case TerrainSamplerTrueColorProbeResult.ColorError:
                Interlocked.Increment(ref terrainSamplerFallbackTrueColorErrors);
                break;
        }
    }

    private enum TerrainSamplerTrueColorProbeResult
    {
        Hit,
        MissingChunk,
        InvalidHeight,
        Air,
        ColorError
    }

    private static int TerrainSamplerColor(int height, int westHeight, int northHeight, int diagonalHeight, int seaLevel, int landColor, int waterColor, int waterEdgeColor)
    {
        if (height <= seaLevel)
        {
            // Vanilla keeps ocean pixels flat in the non-true-colour map path and only colours shorelines as wateredge.
            bool nearLand = westHeight > seaLevel || northHeight > seaLevel || diagonalHeight > seaLevel;
            return (nearLand ? waterEdgeColor : waterColor) | unchecked((int)0xFF000000);
        }

        return TerrainSamplerShadeColor(landColor, height, westHeight, northHeight, diagonalHeight, seaLevel);
    }

    private static int TerrainSamplerShadeColor(int color, int height, int westHeight, int northHeight, int diagonalHeight, int seaLevel)
    {
        int diagonalDelta = height - diagonalHeight;
        int westDelta = height - westHeight;
        int northDelta = height - northHeight;
        float signSum = Math.Sign(diagonalDelta) + Math.Sign(westDelta) + Math.Sign(northDelta);
        float maxDelta = Math.Max(Math.Max(Math.Abs(diagonalDelta), Math.Abs(westDelta)), Math.Abs(northDelta));
        float relief = Math.Min(0.5f, maxDelta / 12f);
        float altitude = Math.Clamp((height - seaLevel) / 180f, 0f, 0.35f);
        float shade = signSum > 0f
            ? 1.02f + relief * 0.55f + altitude
            : signSum < 0f
                ? 0.96f - relief * 0.35f + altitude * 0.5f
                : 1f + altitude * 0.75f;

        return ColorUtil.ColorMultiply3Clamped(color, shade) | unchecked((int)0xFF000000);
    }

    private static int TrueColorFallbackBrownColor(int color, int height, int seaLevel, int worldX, int worldZ, bool water)
    {
        uint hash = MixFallbackNoise((uint)worldX, (uint)worldZ, (uint)height);
        float contrast = water ? 1.0f : 1.04f;
        int noise = water ? 0 : (int)(hash % 7) - 3;
        int altitude = Math.Clamp(height - seaLevel, -32, 220);
        int altitudeLift = water ? -14 : (int)MathF.Round(altitude * 0.01f);
        int adjusted = AdjustFallbackBrownColor(color, contrast, noise + altitudeLift);
        return water ? adjusted : BlendFallbackBrownColor(adjusted, color, 0.55f);
    }

    private int TerrainSamplerBrownClimateTintColor(int color, FastMapTerrainSamplerColumn sample, bool water, int seaLevel)
    {
        if (water || !sample.HasClimate)
        {
            return color;
        }

        float rainfall = Math.Clamp(sample.Rainfall, 0f, 1f);
        float temperature = Math.Clamp(sample.Temperature, 0f, 1f);
        GetTerrainSamplerVegetationWeights(sample, seaLevel, out float forest, out float shrub);
        float vegetation = Math.Max(forest, shrub * 0.65f);
        float aridity = Math.Clamp(temperature * (1f - rainfall), 0f, 1f);

        // Brown fallback should stay close to map-bkg.png; avoid vegetation hue shifts.
        int valueShift = (int)MathF.Round(aridity * 3f - Math.Max(0f, rainfall - 0.75f) * 2f - vegetation * 2f);
        return AdjustFallbackBrownColor(color, 1.0f, valueShift);
    }

    private int TerrainSamplerClimateTintColor(int color, FastMapTerrainSamplerColumn sample, bool water, int worldX, int worldZ, int seaLevel)
    {
        if (water || !sample.HasClimate)
        {
            return color;
        }

        float rainfall = Math.Clamp(sample.Rainfall, 0f, 1f);
        float temperature = Math.Clamp(sample.Temperature, 0f, 1f);
        float dryness = 1f - rainfall;
        float cold = 0.5f - temperature;
        GetTerrainSamplerVegetationWeights(sample, seaLevel, out float forest, out float shrub);
        float vegetation = Math.Max(forest, shrub * 0.45f);
        float aridity = Math.Clamp(temperature * dryness, 0f, 1f);
        float aridWeight = Math.Clamp((aridity - 0.28f) / 0.55f, 0f, 0.85f) * (1f - vegetation * 0.85f);

        int tinted = BlendFallbackBrownColor(color, unchecked((int)0xFF68A4C4), aridWeight);
        if (shrub > 0f)
        {
            int shrubColor = ApplyVegetationValueNoise(unchecked((int)0xFF61A39C), worldX, worldZ, sample.Height, 0x9E3779B9u);
            tinted = BlendFallbackBrownColor(tinted, shrubColor, Math.Clamp(shrub * 0.38f, 0f, 0.45f));
        }

        if (forest > 0f)
        {
            int forestColor = ApplyVegetationValueNoise(unchecked((int)0xFF4C8498), worldX, worldZ, sample.Height, 0x85EBCA6Bu);
            tinted = BlendFallbackBrownColor(tinted, forestColor, Math.Clamp(forest * 0.72f, 0f, 0.78f));
        }

        int greenShift = (int)MathF.Round((rainfall - 0.5f) * 2f);
        int blueShift = (int)MathF.Round(cold * 3f);
        int valueShift = (int)MathF.Round(-Math.Max(0f, rainfall - 0.8f) * 2f);

        int r = Math.Clamp((tinted & 0xFF) + valueShift, 0, 255);
        int g = Math.Clamp(((tinted >> 8) & 0xFF) + greenShift + valueShift, 0, 255);
        int b = Math.Clamp(((tinted >> 16) & 0xFF) + blueShift + valueShift, 0, 255);
        return unchecked((int)0xFF000000) | (b << 16) | (g << 8) | r;
    }

    private int TerrainSamplerPaletteClimateTintColor(int color, FastMapTerrainSamplerColumn sample, int worldX, int worldZ, int seaLevel)
    {
        if (!sample.HasClimate)
        {
            return color;
        }

        int tinted = color;
        GetTerrainSamplerVegetationWeights(sample, seaLevel, out float forest, out float shrub);
        if (shrub > 0f)
        {
            int shrubColor = fallbackPalette.TryGetTreeColor(sample.Height, worldX, worldZ, 0x9E3779B9u, out int paletteShrubColor)
                ? paletteShrubColor
                : unchecked((int)0xFF4F6B78);
            shrubColor = ApplyVegetationValueNoise(shrubColor, worldX, worldZ, sample.Height, 0x9E3779B9u);
            shrubColor = BlendFallbackBrownColor(shrubColor, unchecked((int)0xFF3A596B), 0.42f);
            tinted = BlendFallbackBrownColor(tinted, shrubColor, Math.Clamp(shrub * 0.26f, 0f, 0.38f));
        }

        if (forest > 0f)
        {
            int forestColor = fallbackPalette.TryGetTreeColor(sample.Height, worldX, worldZ, 0x85EBCA6Bu, out int paletteForestColor)
                ? paletteForestColor
                : unchecked((int)0xFF3E5F73);
            forestColor = ApplyVegetationValueNoise(forestColor, worldX, worldZ, sample.Height, 0x85EBCA6Bu);
            forestColor = BlendFallbackBrownColor(forestColor, unchecked((int)0xFF2F4A5D), 0.48f);
            tinted = BlendFallbackBrownColor(tinted, forestColor, Math.Clamp(forest * 0.5f, 0f, 0.62f));
        }

        return tinted;
    }

    private void GetTerrainSamplerGrassSpectrumWeights(
        FastMapTerrainSamplerColumn sample,
        int height,
        int seaLevel,
        out float dryWeight,
        out float lushWeight)
    {
        dryWeight = 0f;
        lushWeight = 0f;
        if (!sample.HasClimate)
        {
            return;
        }

        float fertility = TerrainSamplerFertility(sample, height, seaLevel);
        dryWeight = SmoothStep(0.42f, 0.12f, fertility);
        lushWeight = SmoothStep(0.48f, 0.82f, fertility);
    }

    private void GetTerrainSamplerVegetationWeights(FastMapTerrainSamplerColumn sample, int seaLevel, out float forest, out float shrub)
    {
        float rawForest = Math.Clamp(sample.ForestDensity, 0f, 1f);
        float rawShrub = Math.Clamp(sample.ShrubDensity, 0f, 1f);
        if (!sample.HasClimate || rawForest <= 0f && rawShrub <= 0f)
        {
            forest = rawForest;
            shrub = rawShrub;
            return;
        }

        float adjustedRain = TerrainSamplerAdjustedRainfall(sample, sample.Height);
        float adjustedTemp = TerrainSamplerTemperatureCelsius(sample, sample.Height, seaLevel);
        float relativeHeight = TerrainSamplerRelativeHeight(sample.Height, seaLevel);
        float fertilityRel = TerrainSamplerFertility(sample, sample.Height, seaLevel);

        // Vanilla still uses forest/shrub maps as density gates, but tree choice is climate-scored.
        // This broad suitability curve keeps the tint closer to where actual vegetation can appear.
        float forestClimate = SmoothStep(0.22f, 0.48f, adjustedRain)
            * SmoothStep(-8f, 4f, adjustedTemp)
            * (1f - SmoothStep(34f, 42f, adjustedTemp))
            * SmoothStep(0.12f, 0.36f, fertilityRel)
            * (1f - SmoothStep(0.72f, 0.96f, relativeHeight));
        float shrubClimate = SmoothStep(0.10f, 0.32f, adjustedRain)
            * SmoothStep(-14f, -2f, adjustedTemp)
            * (1f - SmoothStep(38f, 46f, adjustedTemp))
            * SmoothStep(0.08f, 0.26f, fertilityRel)
            * (1f - SmoothStep(0.78f, 1.0f, relativeHeight));

        forest = rawForest * Math.Clamp(0.08f + forestClimate * 0.92f, 0f, 1f);
        shrub = rawShrub * Math.Clamp(0.12f + shrubClimate * 0.88f, 0f, 1f);
    }

    private float TerrainSamplerRelativeHeight(int height, int seaLevel)
    {
        int mapHeight = Math.Max(seaLevel + 1, capi.World.BlockAccessor.MapSizeY);
        return Math.Clamp((height - seaLevel) / (float)(mapHeight - seaLevel), 0f, 1f);
    }

    private float TerrainSamplerFertility(FastMapTerrainSamplerColumn sample, int height, int seaLevel)
    {
        float adjustedTemp = TerrainSamplerTemperatureCelsius(sample, height, seaLevel);
        float relativeHeight = TerrainSamplerRelativeHeight(height, seaLevel);
        int rain = Math.Clamp((int)MathF.Round(TerrainSamplerAdjustedRainfall(sample, height) * 255f), 0, 255);
        return Climate.GetFertility(rain, adjustedTemp, relativeHeight) / 255f;
    }

    private static float TerrainSamplerAdjustedRainfall(FastMapTerrainSamplerColumn sample, int height)
    {
        int unscaledRain = (sample.ClimateColor >> 8) & 0xFF;
        if (unscaledRain == 0)
        {
            unscaledRain = Math.Clamp((int)MathF.Round(sample.Rainfall * 255f), 0, 255);
        }

        return Climate.GetRainFall(unscaledRain, height) / 255f;
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        if (Math.Abs(edge1 - edge0) < 0.0001f)
        {
            return value >= edge1 ? 1f : 0f;
        }

        float t = Math.Clamp((value - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private int TerrainSamplerColdSnowTintColor(
        int color,
        FastMapTerrainSamplerColumn sample,
        int height,
        int westHeight,
        int northHeight,
        int diagonalHeight,
        int seaLevel,
        int worldX,
        int worldZ)
    {
        if (!IsTerrainSamplerSnow(sample, height, seaLevel))
        {
            return color;
        }

        int snowColor = fallbackPalette.TryGetSnowColor(height, worldX, worldZ, out int paletteSnowColor)
            ? paletteSnowColor
            : unchecked((int)0xFFC0E0E0);
        snowColor = PushSnowColorTowardVanillaTarget(snowColor);
        return TerrainSamplerShadeSnowColor(snowColor, height, westHeight, northHeight, diagonalHeight, seaLevel);
    }

    private bool IsTerrainSamplerSnow(FastMapTerrainSamplerColumn sample, int height, int seaLevel)
    {
        if (height < config.TerrainSamplerFallbackSnowStartHeight || height <= seaLevel || !sample.HasClimate)
        {
            return false;
        }

        return TerrainSamplerTemperatureCelsius(sample, height, seaLevel) < -10f;
    }

    private static float TerrainSamplerTemperatureCelsius(FastMapTerrainSamplerColumn sample, int height, int seaLevel)
    {
        int unscaledTemp = (sample.ClimateColor >> 16) & 0xFF;
        if (unscaledTemp == 0)
        {
            unscaledTemp = Math.Clamp((int)MathF.Round(sample.Temperature * 255f), 0, 255);
        }

        return Climate.GetScaledAdjustedTemperatureFloat(unscaledTemp, height - seaLevel);
    }

    private static int PushSnowColorTowardVanillaTarget(int color)
    {
        const int targetSnowColor = unchecked((int)0xFFC0E0E0); // #e0e0c0 in Vintage Story's 0xAABBGGRR map color order.
        return BlendFallbackBrownColor(color, targetSnowColor, 0.75f);
    }

    private static int TerrainSamplerShadeSnowColor(int color, int height, int westHeight, int northHeight, int diagonalHeight, int seaLevel)
    {
        int diagonalDelta = height - diagonalHeight;
        int westDelta = height - westHeight;
        int northDelta = height - northHeight;
        float signSum = Math.Sign(diagonalDelta) + Math.Sign(westDelta) + Math.Sign(northDelta);
        float maxDelta = Math.Max(Math.Max(Math.Abs(diagonalDelta), Math.Abs(westDelta)), Math.Abs(northDelta));
        float relief = Math.Min(0.65f, maxDelta / 10f);
        float altitude = Math.Clamp((height - seaLevel) / 220f, 0f, 0.22f);
        float shade = signSum > 0f
            ? 0.98f + relief * 0.45f + altitude
            : signSum < 0f
                ? 0.78f - relief * 0.12f + altitude * 0.25f
                : 0.9f + altitude * 0.45f;

        return ColorUtil.ColorMultiply3Clamped(color, shade) | unchecked((int)0xFF000000);
    }

    private static int ApplyVegetationValueNoise(int color, int worldX, int worldZ, int height, uint salt)
    {
        uint hash = MixFallbackNoise((uint)worldX ^ salt, (uint)worldZ, (uint)height);
        float multiplier = 0.7f + (hash & 0xFFFF) / 65535f * 0.6f;
        int r = Math.Clamp((int)MathF.Round((color & 0xFF) * multiplier), 0, 255);
        int g = Math.Clamp((int)MathF.Round(((color >> 8) & 0xFF) * multiplier), 0, 255);
        int b = Math.Clamp((int)MathF.Round(((color >> 16) & 0xFF) * multiplier), 0, 255);
        return unchecked((int)0xFF000000) | (b << 16) | (g << 8) | r;
    }

    private static int AdjustFallbackBrownColor(int color, float contrast, int brightnessOffset)
    {
        int r = AdjustFallbackChannel(color & 0xFF, contrast, brightnessOffset);
        int g = AdjustFallbackChannel((color >> 8) & 0xFF, contrast, brightnessOffset);
        int b = AdjustFallbackChannel((color >> 16) & 0xFF, contrast, brightnessOffset);
        return unchecked((int)0xFF000000) | (b << 16) | (g << 8) | r;
    }

    private static int AdjustFallbackChannel(int value, float contrast, int brightnessOffset)
    {
        return Math.Clamp((int)MathF.Round((value - 128) * contrast + 128 + brightnessOffset), 24, 235);
    }

    private static int BlendFallbackBrownColor(int color, int sourceColor, float sourceWeight)
    {
        int r = BlendFallbackChannel(color & 0xFF, sourceColor & 0xFF, sourceWeight);
        int g = BlendFallbackChannel((color >> 8) & 0xFF, (sourceColor >> 8) & 0xFF, sourceWeight);
        int b = BlendFallbackChannel((color >> 16) & 0xFF, (sourceColor >> 16) & 0xFF, sourceWeight);
        return unchecked((int)0xFF000000) | (b << 16) | (g << 8) | r;
    }

    private static int BlendFallbackChannel(int value, int sourceValue, float sourceWeight)
    {
        return Math.Clamp((int)MathF.Round(value * (1f - sourceWeight) + sourceValue * sourceWeight), 0, 255);
    }

    private static uint MixFallbackNoise(uint x, uint z, uint height)
    {
        uint hash = 2166136261u;
        hash = (hash ^ x) * 16777619u;
        hash = (hash ^ z) * 16777619u;
        hash = (hash ^ height) * 16777619u;
        hash ^= hash >> 15;
        hash *= 2246822519u;
        hash ^= hash >> 13;
        return hash;
    }

    private bool TryBuildPageFromDb(FastVec2i pageKey, out FastMapPageSnapshot snapshot, out bool skippedByIndex)
    {
        snapshot = null!;
        skippedByIndex = false;
        if (!MightMapDbContainPage(pageKey))
        {
            Interlocked.Increment(ref pageDbSkippedByIndex);
            skippedByIndex = true;
            return false;
        }

        if (config.UseBatchedNativeDbPageQueries)
        {
            bool hasPage = TryBuildPageFromDbBatchedQuery(pageKey, out snapshot, out bool completed);
            if (completed)
            {
                return hasPage;
            }
        }

        return TryBuildPageFromDbLegacy(pageKey, out snapshot);
    }

    private bool MightMapDbContainPage(FastVec2i pageKey)
    {
        lock (dbLock)
        {
            GetOrBuildMapDbKnownPositions();
            return mapDbKnownPageKeys == null || mapDbKnownPageKeys.Contains(pageKey);
        }
    }

    private bool TryBuildPageFromDbBatchedQuery(FastVec2i pageKey, out FastMapPageSnapshot snapshot, out bool completed)
    {
        snapshot = null!;
        completed = false;
        if (mapdb == null)
        {
            return false;
        }

        FastVec2i baseCoord = new(pageKey.X * ChunksPerPage, pageKey.Y * ChunksPerPage);
        int[]? pixels = null;
        uint[]? validRows = null;
        bool profile = FastMapProfileRecorder.ClientEnabled;
        long waitStart = profile ? FastMapProfileRecorder.Timestamp() : 0;
        double waitMs = 0;
        double indexMs = 0;
        double queryMs = 0;
        double deserializeMs = 0;
        double copyMs = 0;
        int candidateCount = 0;
        int loadedCount = 0;
        int queryCount = 0;
        double throttleMs = 0;
        bool semaphoreHeld = false;

        try
        {
            List<NativeDbPageCandidate> candidates = new(ChunksPerPage * ChunksPerPage);
            Dictionary<ulong, int> localIndexByPosition = new(ChunksPerPage * ChunksPerPage);

            lock (dbLock)
            {
                HashSet<ulong>? knownPositions;

                if (profile)
                {
                    waitMs = FastMapProfileRecorder.ElapsedMilliseconds(waitStart);
                }

                long indexStart = profile ? FastMapProfileRecorder.Timestamp() : 0;
                knownPositions = GetOrBuildMapDbKnownPositions();
                if (profile)
                {
                    indexMs = FastMapProfileRecorder.ElapsedMilliseconds(indexStart);
                }

                for (int dz = 0; dz < ChunksPerPage; dz++)
                {
                    for (int dx = 0; dx < ChunksPerPage; dx++)
                    {
                        FastVec2i chunkCoord = new(baseCoord.X + dx, baseCoord.Y + dz);
                        ulong chunkIndex = chunkCoord.ToChunkIndex();
                        if (knownPositions != null && !knownPositions.Contains(chunkIndex))
                        {
                            continue;
                        }

                        candidateCount++;
                        int localIndex = dz * ChunksPerPage + dx;
                        candidates.Add(new NativeDbPageCandidate(chunkIndex, dx, dz));
                        localIndexByPosition[chunkIndex] = localIndex;
                    }
                }
            }

            if (candidates.Count > 0)
            {
                long throttleStart = profile ? FastMapProfileRecorder.Timestamp() : 0;
                nativeDbPageBuildSemaphore.Wait();
                semaphoreHeld = true;
                throttleMs = profile ? FastMapProfileRecorder.ElapsedMilliseconds(throttleStart) : 0;
                using DbConnection? connection = TryOpenNativeDbReadConnection();
                if (connection == null)
                {
                    return false;
                }

                using (connection)
                {
                    for (int start = 0; start < candidates.Count; start += NativeDbQueryBatchSize)
                    {
                        int count = Math.Min(NativeDbQueryBatchSize, candidates.Count - start);
                        using DbCommand command = CreateNativeDbPageQueryCommand(connection, candidates, start, count);
                        queryCount++;

                        long queryStart = profile ? FastMapProfileRecorder.Timestamp() : 0;
                        using DbDataReader reader = command.ExecuteReader();
                        if (profile)
                        {
                            queryMs += FastMapProfileRecorder.ElapsedMilliseconds(queryStart);
                        }

                        while (reader.Read())
                        {
                            ulong position = ReadDbPosition(reader["position"]);
                            if (!localIndexByPosition.TryGetValue(position, out int localIndex))
                            {
                                continue;
                            }

                            object dataObject = reader["data"];
                            if (dataObject is not byte[] data)
                            {
                                continue;
                            }

                            long deserializeStart = profile ? FastMapProfileRecorder.Timestamp() : 0;
                            MapPieceDB piece = SerializerUtil.Deserialize<MapPieceDB>(data);
                            if (profile)
                            {
                                deserializeMs += FastMapProfileRecorder.ElapsedMilliseconds(deserializeStart);
                            }

                            if (piece?.Pixels == null)
                            {
                                continue;
                            }

                            loadedCount++;
                            int dx = localIndex % ChunksPerPage;
                            int dz = localIndex / ChunksPerPage;
                            pixels ??= new int[FastMapPageComponent.PixelCount];
                            validRows ??= new uint[ChunksPerPage];
                            long copyStart = profile ? FastMapProfileRecorder.Timestamp() : 0;
                            CopyTileIntoPage(piece.Pixels, pixels, dx, dz);
                            if (profile)
                            {
                                copyMs += FastMapProfileRecorder.ElapsedMilliseconds(copyStart);
                            }

                            validRows[dz] |= 1u << dx;
                            Interlocked.Increment(ref tileDbHits);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            api.World.Logger.Warning("[FastMap] Batched native map DB page query failed for {0}/{1}, falling back to legacy point queries: {2}", pageKey.X, pageKey.Y, ex.Message);
            return false;
        }
        finally
        {
            if (semaphoreHeld)
            {
                nativeDbPageBuildSemaphore.Release();
            }
        }

        bool hasAny = loadedCount > 0 && pixels != null && validRows != null;
        if (hasAny)
        {
            snapshot = new FastMapPageSnapshot(pageKey, validRows!, pixels!);
            MarkMapDbPageKnown(pageKey);
        }

        if (profile)
        {
            string detail = $"mode=batched;queries={queryCount};candidates={candidateCount};loaded={loadedCount};hasAny={hasAny}";
            FastMapProfileRecorder.RecordClient("fastmap_page_db_throttle_wait", pageKey.X, 0, pageKey.Y, throttleMs, detail: detail);
            FastMapProfileRecorder.RecordClient("fastmap_page_db_lock_wait", pageKey.X, 0, pageKey.Y, waitMs, detail: detail);
            FastMapProfileRecorder.RecordClient("fastmap_page_db_index_lookup", pageKey.X, 0, pageKey.Y, indexMs, detail: detail);
            FastMapProfileRecorder.RecordClient("fastmap_page_db_query", pageKey.X, 0, pageKey.Y, queryMs, bytes: loadedCount, detail: detail);
            FastMapProfileRecorder.RecordClient("fastmap_page_db_deserialize", pageKey.X, 0, pageKey.Y, deserializeMs, bytes: loadedCount, detail: detail);
            FastMapProfileRecorder.RecordClient("fastmap_page_db_copy_tiles", pageKey.X, 0, pageKey.Y, copyMs, bytes: loadedCount, detail: detail);
        }

        completed = true;
        return hasAny;
    }

    private bool TryBuildPageFromDbLegacy(FastVec2i pageKey, out FastMapPageSnapshot snapshot)
    {
        snapshot = null!;
        if (mapdb == null)
        {
            return false;
        }

        FastVec2i baseCoord = new(pageKey.X * ChunksPerPage, pageKey.Y * ChunksPerPage);
        int[]? pixels = null;
        uint[]? validRows = null;
        bool profile = FastMapProfileRecorder.ClientEnabled;
        long waitStart = profile ? FastMapProfileRecorder.Timestamp() : 0;
        double waitMs = 0;
        double indexMs = 0;
        double getMs = 0;
        double copyMs = 0;
        int candidateCount = 0;
        int loadedCount = 0;

        lock (dbLock)
        {
            if (profile)
            {
                waitMs = FastMapProfileRecorder.ElapsedMilliseconds(waitStart);
            }

            long indexStart = profile ? FastMapProfileRecorder.Timestamp() : 0;
            HashSet<ulong>? knownPositions = GetOrBuildMapDbKnownPositions();
            if (profile)
            {
                indexMs = FastMapProfileRecorder.ElapsedMilliseconds(indexStart);
            }

            for (int dz = 0; dz < ChunksPerPage; dz++)
            {
                for (int dx = 0; dx < ChunksPerPage; dx++)
                {
                    FastVec2i chunkCoord = new(baseCoord.X + dx, baseCoord.Y + dz);
                    ulong chunkIndex = chunkCoord.ToChunkIndex();
                    if (knownPositions != null && !knownPositions.Contains(chunkIndex))
                    {
                        continue;
                    }

                    candidateCount++;
                    long getStart = profile ? FastMapProfileRecorder.Timestamp() : 0;
                    MapPieceDB piece = mapdb.GetMapPiece(chunkCoord);
                    if (profile)
                    {
                        getMs += FastMapProfileRecorder.ElapsedMilliseconds(getStart);
                    }

                    if (piece?.Pixels == null)
                    {
                        continue;
                    }

                    loadedCount++;
                    pixels ??= new int[FastMapPageComponent.PixelCount];
                    validRows ??= new uint[ChunksPerPage];
                    long copyStart = profile ? FastMapProfileRecorder.Timestamp() : 0;
                    CopyTileIntoPage(piece.Pixels, pixels, dx, dz);
                    if (profile)
                    {
                        copyMs += FastMapProfileRecorder.ElapsedMilliseconds(copyStart);
                    }

                    validRows[dz] |= 1u << dx;
                    Interlocked.Increment(ref tileDbHits);
                }
            }
        }

        bool hasAny = loadedCount > 0 && pixels != null && validRows != null;
        if (hasAny)
        {
            snapshot = new FastMapPageSnapshot(pageKey, validRows!, pixels!);
            MarkMapDbPageKnown(pageKey);
        }

        if (profile)
        {
            string detail = $"mode=legacy;candidates={candidateCount};loaded={loadedCount};hasAny={hasAny}";
            FastMapProfileRecorder.RecordClient("fastmap_page_db_lock_wait", pageKey.X, 0, pageKey.Y, waitMs, detail: detail);
            FastMapProfileRecorder.RecordClient("fastmap_page_db_index_lookup", pageKey.X, 0, pageKey.Y, indexMs, detail: detail);
            FastMapProfileRecorder.RecordClient("fastmap_page_db_get_pieces", pageKey.X, 0, pageKey.Y, getMs, bytes: loadedCount, detail: detail);
            FastMapProfileRecorder.RecordClient("fastmap_page_db_copy_tiles", pageKey.X, 0, pageKey.Y, copyMs, bytes: loadedCount, detail: detail);
        }

        return hasAny;
    }

    private static DbCommand CreateNativeDbPageQueryCommand(DbConnection connection, List<NativeDbPageCandidate> candidates, int start, int count)
    {
        DbCommand command = connection.CreateCommand();
        StringBuilder sql = new("SELECT position, data FROM mappiece WHERE position IN (");
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sql.Append(',');
            }

            string parameterName = "@p" + i;
            sql.Append(parameterName);
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = parameterName;
            parameter.Value = unchecked((long)candidates[start + i].Position);
            command.Parameters.Add(parameter);
        }

        sql.Append(')');
        command.CommandText = sql.ToString();
        return command;
    }

    private HashSet<ulong>? GetOrBuildMapDbKnownPositions()
    {
        if (mapDbKnownPositions != null || mapdb == null)
        {
            return mapDbKnownPositions;
        }

        bool profile = FastMapProfileRecorder.ClientEnabled;
        long indexStart = profile ? FastMapProfileRecorder.Timestamp() : 0;
        if (profile)
        {
            FastMapProfileRecorder.RecordClient(
                "fastmap_page_db_index_attempt",
                0,
                0,
                0,
                detail: mapdb.GetType().FullName ?? "unknown",
                kind: "event");
        }

        DbConnection? connection = TryGetMapDbConnection();
        if (connection == null)
        {
            if (profile)
            {
                FastMapProfileRecorder.RecordClient(
                    "fastmap_page_db_index_unavailable",
                    0,
                    0,
                    0,
                    FastMapProfileRecorder.ElapsedMilliseconds(indexStart),
                    detail: "connection");
            }

            return null;
        }

        HashSet<ulong> positions = new();
        HashSet<FastVec2i> pageKeys = new();
        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT position FROM mappiece";
        using DbDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            ulong position = ReadDbPosition(reader["position"]);
            positions.Add(position);
            ChunkPos chunkPos = ChunkPos.FromChunkIndex_saveGamev2(position);
            pageKeys.Add(PageKey(new FastVec2i(chunkPos.X, chunkPos.Z)));
        }

        mapDbKnownPositions = positions;
        mapDbKnownPageKeys = pageKeys;
        if (profile)
        {
            FastMapProfileRecorder.RecordClient(
                "fastmap_page_db_index_build",
                0,
                0,
                0,
                FastMapProfileRecorder.ElapsedMilliseconds(indexStart),
                positions.Count,
                "positionCount;pageCount=" + pageKeys.Count);
        }

        return mapDbKnownPositions;
    }

    private DbConnection? TryGetMapDbConnection()
    {
        if (mapdb == null)
        {
            return null;
        }

        Type? type = mapdb.GetType();
        while (type != null)
        {
            FieldInfo? field = type.GetField("sqliteConn", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field?.GetValue(mapdb) is DbConnection connection)
            {
                return connection;
            }

            type = type.BaseType;
        }

        FieldInfo? commandField = mapdb.GetType().GetField("getMapPieceCmd", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (commandField?.GetValue(mapdb) is DbCommand command)
        {
            return command.Connection;
        }

        return null;
    }

    private DbConnection? TryOpenNativeDbReadConnection()
    {
        if (mapDbPath == null)
        {
            return null;
        }

        DbConnection? existingConnection = TryGetMapDbConnection();
        Type? connectionType = existingConnection?.GetType();
        if (connectionType == null)
        {
            return null;
        }

        string connectionString = "Data Source=" + mapDbPath + ";Mode=ReadOnly;Cache=Shared";
        if (Activator.CreateInstance(connectionType, connectionString) is not DbConnection connection)
        {
            return null;
        }

        connection.Open();
        using DbCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA query_only = ON; PRAGMA temp_store = MEMORY;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static ulong ReadDbPosition(object value)
    {
        return value switch
        {
            ulong ulongValue => ulongValue,
            long longValue => unchecked((ulong)longValue),
            int intValue => unchecked((ulong)intValue),
            _ => Convert.ToUInt64(value)
        };
    }

    private void ProcessReadyPages(Stopwatch frameStopwatch)
    {
        if (disposed)
        {
            return;
        }

        int count = Math.Min(readyPages.Count, config.MaxPageUploadsPerTick);
        for (int i = 0; i < count && readyPages.TryDequeue(out FastMapPageSnapshot? snapshot); i++)
        {
            if (frameStopwatch.ElapsedMilliseconds >= config.MainThreadUploadBudgetMilliseconds)
            {
                readyPages.Enqueue(snapshot);
                break;
            }

            lock (pageLoadLock)
            {
                queuedPageLoads.Remove(snapshot.PageKey);
            }

            if (snapshot.Synthetic && !TerrainFallbackLayerActive)
            {
                continue;
            }

            FastMapPageComponent page = snapshot.Synthetic
                ? GetOrCreateFallbackPage(snapshot.PageKey)
                : GetOrCreatePage(snapshot.PageKey);
            page.ApplySnapshot(snapshot);
            if (!snapshot.Synthetic)
            {
                MarkSnapshotChunksKnown(snapshot);
            }

            if (visiblePageKeys.Contains(snapshot.PageKey))
            {
                if (snapshot.Synthetic)
                {
                    if (TerrainFallbackLayerActive)
                    {
                        UploadFallbackPage(page);
                    }

                    if (!page.HasGpuTexture && config.LogStats)
                    {
                        api.Logger.Notification("[FastMap] Terrain fallback page {0}/{1} did not have a GPU texture after upload attempt.", snapshot.PageKey.X, snapshot.PageKey.Y);
                    }
                }
                else
                {
                    UploadPage(page);
                }

                pagesNeedingUpload.Remove(snapshot.PageKey);
            }
        }
    }

    private void ProcessReadyPatches(Stopwatch frameStopwatch)
    {
        if (disposed)
        {
            return;
        }

        HashSet<FastVec2i> pagesToSave = new();
        int maxPatches = Math.Max(1, config.MaxBackgroundTilesPerPass);
        int processed = 0;
        while (processed < maxPatches && readyPatches.TryDequeue(out FastMapPagePatch? patch))
        {
            if (frameStopwatch.ElapsedMilliseconds >= config.MainThreadUploadBudgetMilliseconds)
            {
                readyPatches.Enqueue(patch);
                break;
            }

            FastVec2i pageKey = PageKey(patch.ChunkCoord);
            FastMapPageComponent page = GetOrCreatePage(pageKey);
            bool profile = FastMapProfileRecorder.ClientEnabled;
            long patchStart = profile ? FastMapProfileRecorder.Timestamp() : 0;
            if (!page.HasPixelBuffer && pageDiskCache.TryLoad(pageKey, out FastMapPageSnapshot snapshot))
            {
                page.ApplySnapshot(snapshot);
                MarkSnapshotChunksKnown(snapshot);
                Interlocked.Increment(ref pagePixelBufferReloads);
            }

            page.SetChunk(patch.ChunkCoord, patch.Pixels);
            MarkChunkKnownValid(patch.ChunkCoord);
            lock (pageLoadLock)
            {
                knownMissingPages.Remove(pageKey);
            }
            pagesToSave.Add(pageKey);
            QueuePageUpload(pageKey);
            if (profile)
            {
                FastMapProfileRecorder.RecordClient(
                    "fastmap_patch_apply",
                    patch.ChunkCoord.X,
                    0,
                    patch.ChunkCoord.Y,
                    FastMapProfileRecorder.ElapsedMilliseconds(patchStart),
                    patch.Pixels.Length * sizeof(int));
            }

            processed++;
        }

        foreach (FastVec2i pageKey in pagesToSave)
        {
            if (pages.TryGetValue(pageKey, out FastMapPageComponent? page))
            {
                QueuePageSave(page.CreateSnapshot());
            }
        }
    }

    private void QueuePageUpload(FastVec2i pageKey)
    {
        if (disposed)
        {
            return;
        }

        pagesNeedingUpload.Add(pageKey);
        if (queuedPageUploads.Add(pageKey))
        {
            pageUploadQueue.Enqueue(pageKey);
        }
    }

    private void ProcessQueuedPageUploads(Stopwatch frameStopwatch)
    {
        if (disposed)
        {
            return;
        }

        int count = Math.Min(pageUploadQueue.Count, config.MaxPageUploadsPerTick);
        for (int i = 0; i < count; i++)
        {
            if (frameStopwatch.ElapsedMilliseconds >= config.MainThreadUploadBudgetMilliseconds)
            {
                break;
            }

            FastVec2i pageKey = pageUploadQueue.Dequeue();
            queuedPageUploads.Remove(pageKey);

            if (!pagesNeedingUpload.Contains(pageKey))
            {
                continue;
            }

            if (!visiblePageKeys.Contains(pageKey))
            {
                continue;
            }

            if (pages.TryGetValue(pageKey, out FastMapPageComponent? page))
            {
                UploadPage(page);
                pagesNeedingUpload.Remove(pageKey);
            }
        }
    }

    private void EnsureVisiblePagesQueuedOrUploaded(Stopwatch frameStopwatch)
    {
        if (disposed || visiblePageKeys.Count == 0)
        {
            return;
        }

        foreach (FastVec2i pageKey in visiblePageKeys)
        {
            if (frameStopwatch.ElapsedMilliseconds >= config.MainThreadUploadBudgetMilliseconds)
            {
                return;
            }

            bool hasCompleteTruePage = EnsureVisibleTruePageQueuedOrUploaded(pageKey);
            if (!hasCompleteTruePage && TerrainFallbackLayerActive)
            {
                EnsureVisibleFallbackPageQueuedOrUploaded(pageKey);
            }
        }
    }

    private void EnsureVisibleFallbackPageQueuedOrUploaded(FastVec2i pageKey)
    {
        if (!TerrainFallbackLayerActive)
        {
            return;
        }

        if (fallbackPages.TryGetValue(pageKey, out FastMapPageComponent? fallbackPage))
        {
            if (!fallbackPage.HasGpuTexture && fallbackPage.HasPixelBuffer)
            {
                UploadFallbackPage(fallbackPage);
            }

            if (fallbackPage.HasGpuTexture)
            {
                return;
            }
        }

        if (!QueueVisibleTerrainFallbackCacheLoad(pageKey))
        {
            QueueTerrainSamplerLoad(pageKey);
        }
    }

    private bool EnsureVisibleTruePageQueuedOrUploaded(FastVec2i pageKey)
    {
        if (pages.TryGetValue(pageKey, out FastMapPageComponent? page))
        {
            if (page.HasGpuTexture)
            {
                if (!page.HasAllValidChunks)
                {
                    QueueTerrainSamplerLoad(pageKey);
                }

                return page.HasAllValidChunks;
            }

            if (page.HasPixelBuffer)
            {
                UploadPage(page);
                if (page.HasGpuTexture)
                {
                    pagesNeedingUpload.Remove(pageKey);
                    if (!page.HasAllValidChunks)
                    {
                        QueueTerrainSamplerLoad(pageKey);
                    }

                    return page.HasAllValidChunks;
                }
            }
        }

        if (pageDiskCache.MightContain(pageKey) || MightMapDbContainPage(pageKey))
        {
            lock (pageLoadLock)
            {
                knownMissingPages.Remove(pageKey);
            }

            QueuePageLoad(pageKey);
            return false;
        }

        QueueTerrainSamplerLoad(pageKey);
        return false;
    }

    private void UploadPage(FastMapPageComponent page)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        if (textureAtlas != null)
        {
            page.Upload(textureAtlas);
        }
        else
        {
            page.Upload();
        }

        page.LastTouchedMs = capi.ElapsedMilliseconds;
        Interlocked.Increment(ref pageUploads);
        Interlocked.Add(ref pageUploadMs, stopwatch.ElapsedMilliseconds);
        FastMapProfileRecorder.RecordClient("fastmap_page_upload", page.PageKey.X, 0, page.PageKey.Y, stopwatch.Elapsed.TotalMilliseconds);
    }

    private void UploadFallbackPage(FastMapPageComponent page)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        if (fallbackTextureAtlas != null)
        {
            page.Upload(fallbackTextureAtlas);
        }
        else
        {
            page.Upload();
        }

        if (page.HasGpuTexture && page.HasPixelBuffer)
        {
            page.ReleasePixelBuffer();
            Interlocked.Increment(ref pagePixelBuffersReleased);
        }

        page.LastTouchedMs = capi.ElapsedMilliseconds;
        Interlocked.Increment(ref pageUploads);
        Interlocked.Add(ref pageUploadMs, stopwatch.ElapsedMilliseconds);
        FastMapProfileRecorder.RecordClient("fastmap_terrain_fallback_page_upload", page.PageKey.X, 0, page.PageKey.Y, stopwatch.Elapsed.TotalMilliseconds);
    }

    private FastMapPageComponent GetOrCreatePage(FastVec2i pageKey)
    {
        if (!pages.TryGetValue(pageKey, out FastMapPageComponent? page))
        {
            page = new FastMapPageComponent(capi, pageKey);
            pages[pageKey] = page;
        }

        return page;
    }

    private FastMapPageComponent GetOrCreateFallbackPage(FastVec2i pageKey)
    {
        if (!fallbackPages.TryGetValue(pageKey, out FastMapPageComponent? page))
        {
            page = new FastMapPageComponent(capi, pageKey);
            fallbackPages[pageKey] = page;
        }

        return page;
    }

    private void OnChunkDirty(Vec3i chunkCoord, IWorldChunk chunk, EnumChunkDirtyReason reason)
    {
        if (disposed || reason != EnumChunkDirtyReason.MarkedDirty || !config.RegenerateOnChunkDirty)
        {
            return;
        }

        InvalidateSurfaceTile(new FastVec2i(chunkCoord.X, chunkCoord.Z));

        if (config.UseMinimalChunkDirtyRepairFanout)
        {
            for (int i = 0; i < MinimalDirtyRepairOffsets.Length; i++)
            {
                FastVec2i offset = MinimalDirtyRepairOffsets[i];
                QueueChunkRepair(new FastVec2i(chunkCoord.X + offset.X, chunkCoord.Z + offset.Y), force: true, reason: "chunkdirty");
            }

            return;
        }

        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                QueueChunkRepair(new FastVec2i(chunkCoord.X + dx, chunkCoord.Z + dz), force: true, reason: "chunkdirty-legacy3x3");
            }
        }
    }

    private void QueueChunkRepair(FastVec2i chunkCoord, bool force = false, string reason = "unknown")
    {
        if (disposed)
        {
            return;
        }

        if (!IsValidTile(chunkCoord))
        {
            return;
        }

        if (!force && IsChunkKnownValid(chunkCoord))
        {
            return;
        }

        if (force)
        {
            MarkChunkUnknown(chunkCoord);
        }

        lock (repairLock)
        {
            long dueMs = GetRepairDueMs(reason);
            if (queuedRepairs.Add(chunkCoord))
            {
                queuedRepairReasons[chunkCoord] = reason;
                queuedRepairDueMs[chunkCoord] = dueMs;
                EnqueueRepairForDueTime(chunkCoord, dueMs);
#if FASTMAPHITCHDIAGNOSTICS
                IncrementHitchDiagnosticReason(queuedRepairDiagnostics, reason);
#endif
                FastMapProfileRecorder.RecordClient("fastmap_chunk_repair_queued", chunkCoord.X, 0, chunkCoord.Y, detail: RepairDetail(reason, force), kind: "event", category: "fastmap_repair");
            }
            else
            {
                if (queuedRepairReasons.TryGetValue(chunkCoord, out string? existingReason) && !ReasonContains(existingReason, reason))
                {
                    queuedRepairReasons[chunkCoord] = existingReason + "+" + reason;
                }

                if (!queuedRepairDueMs.ContainsKey(chunkCoord))
                {
                    queuedRepairDueMs[chunkCoord] = dueMs;
                }
            }
        }
    }

    private bool TryDequeueRepair(out FastVec2i chunkCoord, out string reason)
    {
        long nowMs = capi.ElapsedMilliseconds;

        lock (repairLock)
        {
            MoveDueRepairsToReadyQueue(nowMs);

            if (repairQueue.Count > 0)
            {
                FastVec2i candidate = repairQueue.Dequeue();
                queuedRepairs.Remove(candidate);
                queuedRepairDueMs.Remove(candidate);
                if (!queuedRepairReasons.Remove(candidate, out reason!))
                {
                    reason = "unknown";
                }

                chunkCoord = candidate;
                return true;
            }
        }

        chunkCoord = default;
        reason = "unknown";
        return false;
    }

    private void EnqueueRepairForDueTime(FastVec2i chunkCoord, long dueMs)
    {
        if (dueMs <= capi.ElapsedMilliseconds)
        {
            repairQueue.Enqueue(chunkCoord);
            return;
        }

        if (!delayedRepairQueue.TryGetValue(dueMs, out Queue<FastVec2i>? delayedAtTime))
        {
            delayedAtTime = new Queue<FastVec2i>();
            delayedRepairQueue[dueMs] = delayedAtTime;
        }

        delayedAtTime.Enqueue(chunkCoord);
    }

    private void MoveDueRepairsToReadyQueue(long nowMs)
    {
        while (delayedRepairQueue.Count > 0)
        {
            KeyValuePair<long, Queue<FastVec2i>> first = FirstDelayedRepairBucket();
            if (first.Key > nowMs)
            {
                return;
            }

            delayedRepairQueue.Remove(first.Key);
            while (first.Value.Count > 0)
            {
                FastVec2i chunkCoord = first.Value.Dequeue();
                if (queuedRepairDueMs.TryGetValue(chunkCoord, out long dueMs) && dueMs <= nowMs)
                {
                    repairQueue.Enqueue(chunkCoord);
                }
            }
        }
    }

    private KeyValuePair<long, Queue<FastVec2i>> FirstDelayedRepairBucket()
    {
        foreach (KeyValuePair<long, Queue<FastVec2i>> entry in delayedRepairQueue)
        {
            return entry;
        }

        throw new InvalidOperationException("Delayed repair queue is empty.");
    }

    private long GetRepairDueMs(string reason)
    {
        if (!ReasonContains(reason, "chunkdirty"))
        {
            return capi.ElapsedMilliseconds;
        }

        return capi.ElapsedMilliseconds + config.ExperimentalChunkDirtyRepairDelayMilliseconds;
    }

    private void ProcessChunkRepairs(int maxChunks)
    {
        if (disposed)
        {
            return;
        }

        for (int i = 0; i < maxChunks && TryDequeueRepair(out FastVec2i chunkCoord, out string repairReason); i++)
        {
#if FASTMAPHITCHDIAGNOSTICS
            IncrementHitchDiagnosticReason(processedRepairDiagnostics, repairReason);
#endif
            IMapChunk mapChunk = api.World.BlockAccessor.GetMapChunk(chunkCoord.X, chunkCoord.Y);
            if (mapChunk == null)
            {
                Interlocked.Increment(ref missingSourceChunks);
#if FASTMAPHITCHDIAGNOSTICS
                IncrementHitchDiagnosticReason(missingRepairDiagnostics, repairReason);
#endif
                FastMapProfileRecorder.RecordClient("fastmap_chunk_repair_missing_mapchunk", chunkCoord.X, 0, chunkCoord.Y, detail: repairReason, category: "fastmap_repair");

                continue;
            }

            bool profile = FastMapProfileRecorder.ClientEnabled;
            long repairStart = profile ? FastMapProfileRecorder.Timestamp() : 0;
            int[]? pixels = GenerateChunkImage(chunkCoord, mapChunk);
            if (pixels == null)
            {
                Interlocked.Increment(ref missingSourceChunks);
#if FASTMAPHITCHDIAGNOSTICS
                IncrementHitchDiagnosticReason(missingRepairDiagnostics, repairReason);
#endif
                if (profile)
                {
                    FastMapProfileRecorder.RecordClient(
                        "fastmap_chunk_repair_missing_source",
                        chunkCoord.X,
                        0,
                        chunkCoord.Y,
                        FastMapProfileRecorder.ElapsedMilliseconds(repairStart),
                        detail: repairReason,
                        kind: "inclusive",
                        category: "fastmap_repair");
                }

                continue;
            }

            if (disposed)
            {
                return;
            }

            Interlocked.Increment(ref generatedChunks);
#if FASTMAPHITCHDIAGNOSTICS
            IncrementHitchDiagnosticReason(generatedRepairDiagnostics, repairReason);
#endif
            readyPatches.Enqueue(new FastMapPagePatch(chunkCoord, pixels));
            QueueTileSave(chunkCoord, pixels);
            if (profile)
            {
                FastMapProfileRecorder.RecordClient(
                    "fastmap_chunk_repair_generated",
                    chunkCoord.X,
                    0,
                    chunkCoord.Y,
                    FastMapProfileRecorder.ElapsedMilliseconds(repairStart),
                    pixels.Length * sizeof(int),
                    detail: repairReason,
                    kind: "inclusive",
                    category: "fastmap_repair");
            }
        }
    }

    private void PrewarmAroundPlayer(float dt)
    {
        if (disposed || !config.EnablePrewarm || config.PrewarmRadiusChunks <= 0)
        {
            return;
        }

        prewarmAccum += dt;
        if (prewarmAccum < config.PrewarmIntervalSeconds)
        {
            return;
        }

        prewarmAccum = 0f;
        BlockPos playerPos = capi.World.Player.Entity.Pos.AsBlockPos;
        int centerX = playerPos.X / ChunkSize;
        int centerZ = playerPos.Z / ChunkSize;
        int radius = config.PrewarmRadiusChunks;

        for (int dz = -radius; dz <= radius; dz++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                FastVec2i chunkCoord = new(centerX + dx, centerZ + dz);
                if (api.World.BlockAccessor.GetMapChunk(chunkCoord.X, chunkCoord.Y) == null)
                {
                    FastMapProfileRecorder.RecordClient("fastmap_chunk_repair_skipped_missing_mapchunk", chunkCoord.X, 0, chunkCoord.Y, detail: "prewarm", kind: "event", category: "fastmap_repair");
                    continue;
                }

                QueueChunkRepair(chunkCoord, reason: "prewarm");
            }
        }
    }

    private void QueueTerrainSamplerBackgroundGeneration(float dt)
    {
        if (disposed
            || !config.EnableTerrainSamplerFallbackBackgroundGeneration
            || visiblePageKeys.Count > 0
            || !CanUseTerrainSamplerFallback(out _))
        {
            return;
        }

        terrainSamplerBackgroundAccum += dt;
        if (terrainSamplerBackgroundAccum < config.PrewarmIntervalSeconds)
        {
            return;
        }

        terrainSamplerBackgroundAccum = 0f;
        EntityPlayer? player = capi.World.Player?.Entity;
        if (player == null)
        {
            return;
        }

        BlockPos playerPos = player.Pos.AsBlockPos;
        int centerChunkX = FloorDiv(playerPos.X, ChunkSize);
        int centerChunkZ = FloorDiv(playerPos.Z, ChunkSize);
        FastVec2i centerPage = PageKey(new FastVec2i(centerChunkX, centerChunkZ));
        int radiusPages = Math.Max(0, (int)Math.Ceiling(config.TerrainSamplerFallbackRadiusChunks / (double)ChunksPerPage));
        int queued = 0;

        for (int ring = 0; ring <= radiusPages && queued < config.TerrainSamplerFallbackBackgroundPagesPerPass; ring++)
        {
            for (int dz = -ring; dz <= ring && queued < config.TerrainSamplerFallbackBackgroundPagesPerPass; dz++)
            {
                for (int dx = -ring; dx <= ring && queued < config.TerrainSamplerFallbackBackgroundPagesPerPass; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring)
                    {
                        continue;
                    }

                    FastVec2i pageKey = new(centerPage.X + dx, centerPage.Y + dz);
                    if (fallbackPages.ContainsKey(pageKey)
                        || CurrentTerrainFallbackDiskCache.MightContain(pageKey)
                        || IsTerrainSamplerFallbackPageReserved(pageKey))
                    {
                        continue;
                    }

                    QueueTerrainSamplerLoad(pageKey);
                    queued++;
                }
            }
        }
    }

    private void MarkSnapshotChunksKnown(FastMapPageSnapshot snapshot)
    {
        MarkMapDbPageKnown(snapshot.PageKey);
        FastVec2i baseCoord = new(snapshot.PageKey.X * ChunksPerPage, snapshot.PageKey.Y * ChunksPerPage);
        for (int dz = 0; dz < ChunksPerPage; dz++)
        {
            uint row = snapshot.ValidRows[dz];
            if (row == 0)
            {
                continue;
            }

            for (int dx = 0; dx < ChunksPerPage; dx++)
            {
                if ((row & (1u << dx)) != 0)
                {
                    MarkChunkKnownValid(new FastVec2i(baseCoord.X + dx, baseCoord.Y + dz));
                }
            }
        }
    }

    private bool IsChunkKnownValid(FastVec2i chunkCoord)
    {
        lock (chunkValidityLock)
        {
            return chunksKnownValid.Contains(chunkCoord);
        }
    }

    private void MarkChunkKnownValid(FastVec2i chunkCoord)
    {
        lock (chunkValidityLock)
        {
            chunksKnownValid.Add(chunkCoord);
        }
    }

    private void MarkChunkUnknown(FastVec2i chunkCoord)
    {
        lock (chunkValidityLock)
        {
            chunksKnownValid.Remove(chunkCoord);
        }
    }

    private void QueuePageSave(FastMapPageSnapshot snapshot)
    {
        if (disposed || !snapshot.HasAnyValidChunks)
        {
            return;
        }

        lock (pageSaveLock)
        {
            pendingPageSaves[snapshot.PageKey] = snapshot;
        }
    }

    private void QueueTileSave(FastVec2i chunkCoord, int[] pixels)
    {
        MarkMapDbPageKnown(PageKey(chunkCoord));
        if (disposed || !mapDbWritable)
        {
            return;
        }

        lock (pageSaveLock)
        {
            pendingTileSaves[chunkCoord] = new MapPieceDB { Pixels = pixels };
        }
    }

    private int PendingPageSaveCount()
    {
        lock (pageSaveLock)
        {
            return pendingPageSaves.Count;
        }
    }

    private void FlushPendingSaves()
    {
        Dictionary<FastVec2i, FastMapPageSnapshot> pagesToSave;
        Dictionary<FastVec2i, MapPieceDB> tilesToSave;
        lock (pageSaveLock)
        {
            pagesToSave = new Dictionary<FastVec2i, FastMapPageSnapshot>(pendingPageSaves);
            tilesToSave = new Dictionary<FastVec2i, MapPieceDB>(pendingTileSaves);
            pendingPageSaves.Clear();
            pendingTileSaves.Clear();
        }

        foreach (FastMapPageSnapshot snapshot in pagesToSave.Values)
        {
            try
            {
                pageDiskCache.Save(snapshot);
                Interlocked.Increment(ref pageSaves);
                FastMapProfileRecorder.RecordClient("fastmap_page_save", snapshot.PageKey.X, 0, snapshot.PageKey.Y);
            }
            catch (Exception ex)
            {
                api.Logger.Warning("[FastMap] Failed saving page {0}/{1}: {2}", snapshot.PageKey.X, snapshot.PageKey.Y, ex.Message);
                QueuePageSave(snapshot);
            }
        }

        if (mapdb != null && mapDbWritable && tilesToSave.Count > 0)
        {
            lock (dbLock)
            {
                try
                {
                    mapdb.SetMapPieces(tilesToSave);
                    AddKnownMapDbPositions(tilesToSave.Keys);
                    FastMapProfileRecorder.RecordClient("fastmap_tile_db_save", 0, 0, 0, bytes: tilesToSave.Count, detail: "tileCount");
                }
                catch (Exception ex)
                {
                    api.Logger.Warning("[FastMap] Failed saving {0} vanilla map tiles: {1}", tilesToSave.Count, ex.Message);
                    lock (pageSaveLock)
                    {
                        foreach (KeyValuePair<FastVec2i, MapPieceDB> entry in tilesToSave)
                        {
                            pendingTileSaves[entry.Key] = entry.Value;
                        }
                    }
                }
            }
        }
    }

    private void EvictPages(float dt)
    {
        if (disposed)
        {
            return;
        }

        evictAccum += dt;
        int textureCount = GpuTexturePageCount();
        int memoryCount = pages.Count + fallbackPages.Count;
        if (evictAccum < 1f || (textureCount <= config.PageTextureBudget && memoryCount <= config.PageMemoryBudget))
        {
            return;
        }

        evictAccum = 0f;
        EvictGpuTextures(textureCount);
        EvictMemoryPages();
    }

    private int GpuTexturePageCount()
    {
        int count = 0;
        foreach (FastMapPageComponent page in pages.Values)
        {
            if (page.HasGpuTexture)
            {
                count++;
            }
        }

        foreach (FastMapPageComponent page in fallbackPages.Values)
        {
            if (page.HasGpuTexture)
            {
                count++;
            }
        }

        return count;
    }

    private void EvictGpuTextures(int textureCount)
    {
        if (textureCount <= config.PageTextureBudget)
        {
            return;
        }

        List<PageEvictionCandidate> candidates = new();
        AddEvictionCandidates(pages, candidates, fallback: false, requireGpuTexture: true);
        AddEvictionCandidates(fallbackPages, candidates, fallback: true, requireGpuTexture: true);
        candidates.Sort((a, b) => a.Page.LastTouchedMs.CompareTo(b.Page.LastTouchedMs));

        foreach (PageEvictionCandidate candidate in candidates)
        {
            if (textureCount <= config.PageTextureBudget)
            {
                break;
            }

            candidate.Page.DisposeTexture();
            textureCount--;
        }
    }

    private void EvictMemoryPages()
    {
        int memoryCount = pages.Count + fallbackPages.Count;
        if (memoryCount <= config.PageMemoryBudget)
        {
            return;
        }

        List<PageEvictionCandidate> candidates = new();
        AddEvictionCandidates(pages, candidates, fallback: false, requireGpuTexture: false);
        AddEvictionCandidates(fallbackPages, candidates, fallback: true, requireGpuTexture: false);
        candidates.Sort((a, b) =>
        {
            int textureCompare = a.Page.HasGpuTexture.CompareTo(b.Page.HasGpuTexture);
            return textureCompare != 0 ? textureCompare : a.Page.LastTouchedMs.CompareTo(b.Page.LastTouchedMs);
        });

        foreach (PageEvictionCandidate candidate in candidates)
        {
            if (memoryCount <= config.PageMemoryBudget)
            {
                break;
            }

            candidate.Page.DisposeTexture();
            candidate.Page.ReleasePixelBuffer();
            Interlocked.Increment(ref pagePixelBuffersReleased);
            if (candidate.Fallback)
            {
                fallbackPages.Remove(candidate.PageKey);
            }
            else
            {
                pages.Remove(candidate.PageKey);
            }

            memoryCount--;
        }
    }

    private void AddEvictionCandidates(
        Dictionary<FastVec2i, FastMapPageComponent> pageDictionary,
        List<PageEvictionCandidate> candidates,
        bool fallback,
        bool requireGpuTexture)
    {
        foreach (KeyValuePair<FastVec2i, FastMapPageComponent> entry in pageDictionary)
        {
            if (visiblePageKeys.Contains(entry.Key))
            {
                continue;
            }

            if (requireGpuTexture && !entry.Value.HasGpuTexture)
            {
                continue;
            }

            candidates.Add(new PageEvictionCandidate(entry.Key, entry.Value, fallback));
        }
    }

    private void LogStats(float dt)
    {
        if (disposed || !config.LogStats)
        {
            return;
        }

        statsAccum += dt;
        if (statsAccum < config.LogStatsIntervalSeconds)
        {
            return;
        }

        statsAccum = 0f;
        GetReadyPageQueueDiagnostics(out int readyFullResPages, out int readyLowResPages, out long readyApproxMb);
        GetResidentPageDiagnostics(
            out int trueGpuPages,
            out int fallbackGpuPages,
            out int truePixelPages,
            out int fallbackPixelPages,
            out long truePixelMb,
            out long fallbackPixelMb
        );
        api.Logger.Notification(
            "[FastMap] pages loaded={0}, fallbackLoaded={1}, visible={2}, queuedPages={3}, activeLoads={4}, readyPages={5}, readyLowRes={6}, readyFullRes={7}, readyApproxMb={8}, trueGpu={9}, fallbackGpu={10}, truePix={11}, fallbackPix={12}, truePixMb={13}, fallbackPixMb={14}, managedMb={15}, readyPatches={16}, diskHits={17}, diskMisses={18}, diskIndexSkips={19}, dbHits={20}, dbMisses={21}, dbIndexSkips={22}, loadBatches={23}, loadItems={24}, pixelReleases={25}, pixelReloads={26}, samplerPages={27}, samplerQueued={28}, samplerSkipped={29}, samplerQueue={30}, samplerActive={31}, samplerCacheQueue={32}, samplerCacheActive={33}, samplerCacheHits={34}, samplerCacheMisses={35}, samplerBuildFails={36}, samplerGenDefers={37}, samplerRetries={38}, samplerRetryExhausted={39}, samplerSamples={40}, samplerSea={41}, samplerNearSea={42}, generated={43}, missing={44}, uploads={45}, saves={46}",
            pages.Count,
            fallbackPages.Count,
            visiblePageKeys.Count,
            QueuedPageLoadCount(),
            Volatile.Read(ref activePageLoadTasks),
            readyPages.Count,
            readyLowResPages,
            readyFullResPages,
            readyApproxMb,
            trueGpuPages,
            fallbackGpuPages,
            truePixelPages,
            fallbackPixelPages,
            truePixelMb,
            fallbackPixelMb,
            GC.GetTotalMemory(false) / (1024 * 1024),
            readyPatches.Count,
            pageDiskHits,
            pageDiskMisses,
            pageDiskSkippedByIndex,
            pageDbHits,
            pageDbMisses,
            pageDbSkippedByIndex,
            pageLoadBatchesStarted,
            pageLoadItemsProcessed,
            pagePixelBuffersReleased,
            pagePixelBufferReloads,
            terrainSamplerFallbackPages,
            terrainSamplerFallbackQueued,
            terrainSamplerFallbackSkipped,
            QueuedTerrainSamplerLoadCount(),
            Volatile.Read(ref activeTerrainSamplerLoadTasks),
            QueuedTerrainFallbackCacheLoadCount(),
            Volatile.Read(ref activeTerrainFallbackCacheLoadTasks),
            terrainSamplerFallbackCacheHits,
            terrainSamplerFallbackCacheMisses,
            terrainSamplerFallbackBuildFailures,
            terrainSamplerFallbackGenerationDeferrals,
            terrainSamplerFallbackRetriesQueued,
            terrainSamplerFallbackRetriesExhausted,
            terrainSamplerFallbackSamples,
            terrainSamplerFallbackSeaSamples,
            terrainSamplerFallbackNearSeaSamples,
            generatedChunks,
            missingSourceChunks,
            pageUploads,
            pageSaves
        );
        api.Logger.Notification(
            "[FastMap] page timings loadMs={0}, uploadMs={1}, generationMs={2}, tileDbHits={3}, atlases={4}, trueAtlases={5}, fallbackAtlases={6}, trueAtlasMb={7}, fallbackAtlasMb={8}, fallbackSlot={9}, samplerSkipDisabled={10}, samplerSkipLimit={11}, samplerSkipRadius={12}, samplerSkipDuplicate={13}, path={14}",
            pageLoadMs,
            pageUploadMs,
            generationMs,
            tileDbHits,
            (textureAtlas?.AtlasCount ?? 0) + (fallbackTextureAtlas?.AtlasCount ?? 0),
            textureAtlas?.AtlasCount ?? 0,
            fallbackTextureAtlas?.AtlasCount ?? 0,
            (textureAtlas?.ApproxTextureBytes ?? 0) / (1024 * 1024),
            (fallbackTextureAtlas?.ApproxTextureBytes ?? 0) / (1024 * 1024),
            fallbackTextureAtlas?.SlotSize ?? FastMapPageComponent.PageSize,
            terrainSamplerFallbackSkippedDisabled,
            terrainSamplerFallbackSkippedLimit,
            terrainSamplerFallbackSkippedRadius,
            terrainSamplerFallbackSkippedDuplicate,
            pageDiskCache.RootPath
        );
        api.Logger.Notification(
            "[FastMap] fallback true-colour probes={0}, hits={1}, misses={2}, missingChunk={3}, invalidHeight={4}, air={5}, errors={6}, enabled={7}, palette={8}, snowStart={9}, stride={10}",
            terrainSamplerFallbackTrueColorProbes,
            terrainSamplerFallbackTrueColorHits,
            terrainSamplerFallbackTrueColorMisses,
            terrainSamplerFallbackTrueColorMissingChunk,
            terrainSamplerFallbackTrueColorInvalidHeight,
            terrainSamplerFallbackTrueColorAir,
            terrainSamplerFallbackTrueColorErrors,
            colorAccurate && config.EnableTerrainSamplerFallbackTrueColor,
            fallbackPalette.HasAny,
            config.TerrainSamplerFallbackSnowStartHeight,
            config.TerrainSamplerFallbackTrueColorProbeStride
        );
        api.Logger.Notification("[FastMap] sampler scheduler {0}", TerrainSamplerSchedulerDetail());
    }

    private void GetReadyPageQueueDiagnostics(out int fullResolutionPages, out int lowResolutionPages, out long approxMegabytes)
    {
        fullResolutionPages = 0;
        lowResolutionPages = 0;
        long bytes = 0;

        foreach (FastMapPageSnapshot snapshot in readyPages)
        {
            if (snapshot.IsLowResolution)
            {
                lowResolutionPages++;
            }
            else
            {
                fullResolutionPages++;
            }

            bytes += (long)snapshot.Pixels.Length * sizeof(int);
        }

        approxMegabytes = bytes / (1024 * 1024);
    }

    private string TerrainSamplerSchedulerDetail()
    {
        bool canUse = CanUseTerrainSamplerFallback(out TerrainSamplerSkipReason skipReason);
        bool pendingViewportFill = HasPendingViewportFillWork();
        int readyQueue;
        int delayedQueue;
        int cacheQueue;
        long nextRetryDelayMs;
        lock (terrainSamplerLoadLock)
        {
            readyQueue = CountTerrainSamplerLoadsLocked();
            delayedQueue = CountDelayedTerrainSamplerLoadsLocked();
            cacheQueue = terrainFallbackCacheLoadQueue.Count;
            nextRetryDelayMs = NextTerrainSamplerRetryDelayMsLocked();
        }

        int trueQueue = QueuedPageLoadCount();
        long fallbackPages = Interlocked.Read(ref terrainSamplerFallbackPages);
        GetReadyPageQueueDiagnostics(out int readyFullResPages, out int readyLowResPages, out long readyApproxMb);
        GetResidentPageDiagnostics(
            out int trueGpuPages,
            out int fallbackGpuPages,
            out int truePixelPages,
            out int fallbackPixelPages,
            out long truePixelMb,
            out long fallbackPixelMb
        );
        int uniquePages;
        lock (terrainSamplerLoadLock)
        {
            uniquePages = terrainSamplerFallbackPageKeys.Count;
        }

        return "canUse=" + canUse
            + ";reason=" + skipReason
            + ";canStart=" + CanStartTerrainSamplerLoads()
            + ";pendingViewportFill=" + pendingViewportFill
            + ";trueQueue=" + trueQueue
            + ";trueActive=" + Volatile.Read(ref activePageLoadTasks)
            + ";readyPages=" + readyPages.Count + "/" + config.ReadyPageQueueBudget
            + ";readyLowRes=" + readyLowResPages
            + ";readyFullRes=" + readyFullResPages
            + ";readyApproxMb=" + readyApproxMb
            + ";fallbackLoaded=" + this.fallbackPages.Count
            + ";trueGpu=" + trueGpuPages
            + ";fallbackGpu=" + fallbackGpuPages
            + ";truePix=" + truePixelPages
            + ";fallbackPix=" + fallbackPixelPages
            + ";truePixMb=" + truePixelMb
            + ";fallbackPixMb=" + fallbackPixelMb
            + ";managedMb=" + (GC.GetTotalMemory(false) / (1024 * 1024))
            + ";cacheQueue=" + cacheQueue
            + ";cacheActive=" + Volatile.Read(ref activeTerrainFallbackCacheLoadTasks)
            + ";samplerQueue=" + readyQueue
            + ";samplerDelayed=" + delayedQueue
            + ";samplerActive=" + Volatile.Read(ref activeTerrainSamplerLoadTasks)
            + ";nextRetryMs=" + nextRetryDelayMs
            + ";reserved=" + uniquePages + "/" + config.TerrainSamplerFallbackMaxPagesPerSession
            + ";generated=" + fallbackPages
            + ";trueColorProbes=" + terrainSamplerFallbackTrueColorProbes
            + ";trueColorHits=" + terrainSamplerFallbackTrueColorHits
            + ";trueColorMisses=" + terrainSamplerFallbackTrueColorMisses
            + ";trueColorMissingChunk=" + terrainSamplerFallbackTrueColorMissingChunk
            + ";trueColorInvalidHeight=" + terrainSamplerFallbackTrueColorInvalidHeight
            + ";trueColorAir=" + terrainSamplerFallbackTrueColorAir
            + ";trueColorErrors=" + terrainSamplerFallbackTrueColorErrors
            + ";colorAccurate=" + colorAccurate
            + ";visible=" + visiblePageKeys.Count;
    }

    private void GetResidentPageDiagnostics(
        out int trueGpuPages,
        out int fallbackGpuPages,
        out int truePixelPages,
        out int fallbackPixelPages,
        out long truePixelMb,
        out long fallbackPixelMb)
    {
        trueGpuPages = 0;
        fallbackGpuPages = 0;
        truePixelPages = 0;
        fallbackPixelPages = 0;
        long truePixelBytes = 0;
        long fallbackPixelBytes = 0;

        foreach (FastMapPageComponent page in pages.Values)
        {
            if (page.HasGpuTexture)
            {
                trueGpuPages++;
            }

            if (page.HasPixelBuffer)
            {
                truePixelPages++;
                truePixelBytes += page.PixelBufferBytes;
            }
        }

        foreach (FastMapPageComponent page in fallbackPages.Values)
        {
            if (page.HasGpuTexture)
            {
                fallbackGpuPages++;
            }

            if (page.HasPixelBuffer)
            {
                fallbackPixelPages++;
                fallbackPixelBytes += page.PixelBufferBytes;
            }
        }

        truePixelMb = truePixelBytes / (1024 * 1024);
        fallbackPixelMb = fallbackPixelBytes / (1024 * 1024);
    }

    private long NextTerrainSamplerRetryDelayMsLocked()
    {
        foreach (long dueMs in delayedTerrainSamplerLoadQueue.Keys)
        {
            return Math.Max(0, dueMs - capi.ElapsedMilliseconds);
        }

        return -1;
    }

    private int QueuedPageLoadCount()
    {
        lock (pageLoadLock)
        {
            return queuedPageLoadPriorities.Count;
        }
    }

    private int QueuedTerrainSamplerLoadCount()
    {
        lock (terrainSamplerLoadLock)
        {
            return CountTerrainSamplerLoadsLocked();
        }
    }

    private int QueuedTerrainFallbackCacheLoadCount()
    {
        lock (terrainSamplerLoadLock)
        {
            return terrainFallbackCacheLoadQueue.Count;
        }
    }

#if FASTMAPHITCHDIAGNOSTICS
    private int QueuedRepairCount()
    {
        lock (repairLock)
        {
            return queuedRepairs.Count;
        }
    }

    private int PendingTileSaveCount()
    {
        lock (pageSaveLock)
        {
            return pendingTileSaves.Count;
        }
    }

    private Stopwatch? StartHitchStopwatch()
    {
        return config.EnableHitchDiagnostics ? Stopwatch.StartNew() : null;
    }

    private void MarkFrameProfiler(string code)
    {
        if (!config.EnableHitchDiagnostics || !api.World.FrameProfiler.Enabled)
        {
            return;
        }

        api.World.FrameProfiler.Mark(code);
    }

    private void LogHitchDiagnostic(Stopwatch? stopwatch, string stage, string? detail = null)
    {
        if (stopwatch == null || stopwatch.ElapsedMilliseconds < config.HitchDiagnosticThresholdMilliseconds)
        {
            return;
        }

        LogHitchDiagnostic(stage, stopwatch.ElapsedMilliseconds, detail);
    }

    private void LogFrameGapDiagnostic(float dt, string stage)
    {
        if (!config.EnableHitchDiagnostics)
        {
            return;
        }

        long elapsedMs = (long)Math.Round(dt * 1000f);
        if (elapsedMs < config.HitchDiagnosticThresholdMilliseconds)
        {
            return;
        }

        LogHitchDiagnostic(stage, elapsedMs, "dt");
    }

    private void LogWallClockGapDiagnostic(ref long lastTimestamp, string stage)
    {
        if (!config.EnableHitchDiagnostics)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        long previous = lastTimestamp;
        lastTimestamp = now;
        if (previous == 0)
        {
            return;
        }

        long elapsedMs = (long)((now - previous) * 1000.0 / Stopwatch.Frequency);
        if (elapsedMs < config.HitchDiagnosticThresholdMilliseconds)
        {
            return;
        }

        LogHitchDiagnostic(stage, elapsedMs, "wallclock");
    }

    private void IncrementHitchDiagnosticReason(Dictionary<string, long> counters, string reason)
    {
        if (!config.EnableHitchDiagnostics)
        {
            return;
        }

        lock (hitchDiagnosticsLock)
        {
            counters.TryGetValue(reason, out long count);
            counters[reason] = count + 1;
        }
    }

    private string HitchDiagnosticReasonSummary()
    {
        if (!config.EnableHitchDiagnostics)
        {
            return string.Empty;
        }

        lock (hitchDiagnosticsLock)
        {
            return "queued=" + FormatReasonCounts(queuedRepairDiagnostics)
                + ";processed=" + FormatReasonCounts(processedRepairDiagnostics)
                + ";generated=" + FormatReasonCounts(generatedRepairDiagnostics)
                + ";missing=" + FormatReasonCounts(missingRepairDiagnostics);
        }
    }

    private static string FormatReasonCounts(Dictionary<string, long> counters)
    {
        if (counters.Count == 0)
        {
            return "none";
        }

        StringBuilder summary = new();
        int emitted = 0;
        foreach (KeyValuePair<string, long> entry in counters)
        {
            if (emitted > 0)
            {
                summary.Append('|');
            }

            summary.Append(entry.Key);
            summary.Append(':');
            summary.Append(entry.Value);
            emitted++;
        }

        return summary.ToString();
    }

    private void LogHitchDiagnostic(string stage, long elapsedMs, string? detail)
    {
        long nowMs = capi.ElapsedMilliseconds;
        if (nowMs - lastHitchDiagnosticLogMs < 1000)
        {
            return;
        }

        lastHitchDiagnosticLogMs = nowMs;
        int gen0 = GC.CollectionCount(0);
        int gen1 = GC.CollectionCount(1);
        int gen2 = GC.CollectionCount(2);
        int gen0Delta = gen0 - lastHitchGen0Collections;
        int gen1Delta = gen1 - lastHitchGen1Collections;
        int gen2Delta = gen2 - lastHitchGen2Collections;
        lastHitchGen0Collections = gen0;
        lastHitchGen1Collections = gen1;
        lastHitchGen2Collections = gen2;
        string repairReasons = HitchDiagnosticReasonSummary();
        api.Logger.Warning(
            "[FastMap] Hitch diagnostic stage={0}, elapsedMs={1}, detail={2}, pages={3}, visiblePages={4}, queuedPageLoads={5}, activeLoads={6}, readyPages={7}, readyPatches={8}, queuedRepairs={9}, queuedUploads={10}, pendingPageSaves={11}, pendingTileSaves={12}, diskHits={13}, diskMisses={14}, diskIndexSkips={15}, dbHits={16}, dbMisses={17}, dbIndexSkips={18}, loadBatches={19}, loadItems={20}, pixelReleases={21}, pixelReloads={22}, generated={23}, missing={24}, uploads={25}, saves={26}, gc0Delta={27}, gc1Delta={28}, gc2Delta={29}, managedMemoryMb={30}, repairReasons={31}",
            stage,
            elapsedMs,
            detail ?? string.Empty,
            pages.Count,
            visiblePageKeys.Count,
            QueuedPageLoadCount(),
            Volatile.Read(ref activePageLoadTasks),
            readyPages.Count,
            readyPatches.Count,
            QueuedRepairCount(),
            pageUploadQueue.Count,
            PendingPageSaveCount(),
            PendingTileSaveCount(),
            pageDiskHits,
            pageDiskMisses,
            pageDiskSkippedByIndex,
            pageDbHits,
            pageDbMisses,
            pageDbSkippedByIndex,
            pageLoadBatchesStarted,
            pageLoadItemsProcessed,
            pagePixelBuffersReleased,
            pagePixelBufferReloads,
            generatedChunks,
            missingSourceChunks,
            pageUploads,
            pageSaves,
            gen0Delta,
            gen1Delta,
            gen2Delta,
            GC.GetTotalMemory(false) / (1024 * 1024),
            repairReasons);
    }
#endif

    private void ClearQueues()
    {
        lock (pageLoadLock)
        {
            pageLoadQueue.Clear();
            queuedPageLoadPriorities.Clear();
            queuedPageLoads.Clear();
            knownMissingPages.Clear();
        }

        lock (terrainSamplerLoadLock)
        {
            terrainFallbackCacheLoadQueue.Clear();
            queuedTerrainFallbackCacheLoads.Clear();
            terrainSamplerLoadQueue.Clear();
            delayedTerrainSamplerLoadQueue.Clear();
            queuedTerrainSamplerLoads.Clear();
            terrainSamplerFallbackPageKeys.Clear();
            terrainSamplerLoadAttempts.Clear();
        }

        while (readyPages.TryDequeue(out _))
        {
        }

        lock (repairLock)
        {
            repairQueue.Clear();
            delayedRepairQueue.Clear();
            queuedRepairs.Clear();
            queuedRepairReasons.Clear();
            queuedRepairDueMs.Clear();
        }

        while (readyPatches.TryDequeue(out _))
        {
        }

#if FASTMAPHITCHDIAGNOSTICS
        lock (hitchDiagnosticsLock)
        {
            queuedRepairDiagnostics.Clear();
            processedRepairDiagnostics.Clear();
            generatedRepairDiagnostics.Clear();
            missingRepairDiagnostics.Clear();
        }
#endif

        pageUploadQueue.Clear();
        queuedPageUploads.Clear();
        pagesNeedingUpload.Clear();

        lock (surfaceTileCacheLock)
        {
            surfaceTileCache.Clear();
        }

        lock (pageSaveLock)
        {
            pendingPageSaves.Clear();
            pendingTileSaves.Clear();
        }
    }

    private void BuildColorLookup()
    {
        colorsByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> entry in ChunkMapLayer.hexColorsByCode)
        {
            colorsByCode[entry.Key] = ColorUtil.ReverseColorBytes(ColorUtil.Hex2Int(entry.Value));
        }

        fallbackLandColor = colorsByCode.TryGetValue("land", out int landColor) ? landColor : unchecked((int)0xFFAC8858);
        trueColorAirFallbackColor = ParseMapColor(config.TrueColorAirFallbackColor, unchecked((int)0xFF282828));

        IList<Block> blocks = api.World.Blocks;
        blockColorByBlockId = new int[blocks.Count];
        blockIsLakeByBlockId = new bool[blocks.Count];
        blockIsSnowByBlockId = new bool[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
        {
            Block? block = blocks[i];
            string? colorCode = "land";
            blockIsLakeByBlockId[i] = block != null && IsLake(block);
            blockIsSnowByBlockId[i] = block?.BlockMaterial == EnumBlockMaterial.Snow;

            if (block?.Attributes != null)
            {
                colorCode = block.Attributes["mapColorCode"].AsString(null);
            }

            if (colorCode == null && (block == null || !ChunkMapLayer.defaultMapColorCodes.TryGetValue(block.BlockMaterial, out colorCode)))
            {
                colorCode = "land";
            }

            if (!colorsByCode.TryGetValue(colorCode ?? "land", out int color))
            {
                throw new Exception("No world map color exists for color code " + colorCode);
            }

            blockColorByBlockId[i] = color;
        }
    }

    private static int ParseMapColor(string? hexColor, int fallback)
    {
        if (string.IsNullOrWhiteSpace(hexColor))
        {
            return fallback;
        }

        try
        {
            return ColorUtil.ReverseColorBytes(ColorUtil.Hex2Int(hexColor)) | unchecked((int)0xFF000000);
        }
        catch
        {
            return fallback;
        }
    }

    private int[]? GenerateChunkImage(FastVec2i chunkPos, IMapChunk mapChunk)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        bool success = false;
        bool profile = FastMapProfileRecorder.ClientEnabled;
        try
        {
            long prefetchStart = profile ? FastMapProfileRecorder.Timestamp() : 0;
            for (int cy = 0; cy < chunksTmp.Length; cy++)
            {
                IWorldChunk chunk = capi.World.BlockAccessor.GetChunk(chunkPos.X, cy, chunkPos.Y);
                if (chunk is not IClientChunk clientChunk || !clientChunk.LoadedFromServer)
                {
                    if (profile)
                    {
                        FastMapProfileRecorder.RecordClient(
                            "fastmap_generate_prefetch_chunks",
                            chunkPos.X,
                            0,
                            chunkPos.Y,
                            FastMapProfileRecorder.ElapsedMilliseconds(prefetchStart),
                            detail: "missing_or_unloaded",
                            category: "fastmap_image");
                    }

                    ClearChunkScratch();
                    return null;
                }

                chunk.Unpack_ReadOnly();
                chunksTmp[cy] = chunk;
            }

            if (profile)
            {
                FastMapProfileRecorder.RecordClient(
                    "fastmap_generate_prefetch_chunks",
                    chunkPos.X,
                    0,
                    chunkPos.Y,
                    FastMapProfileRecorder.ElapsedMilliseconds(prefetchStart),
                    category: "fastmap_image");
            }

            int[] pixels = new int[TilePixelCount];
            long mapChunkStart = profile ? FastMapProfileRecorder.Timestamp() : 0;
            IMapChunk northwestMapChunk = capi.World.BlockAccessor.GetMapChunk(chunkPos.X - 1, chunkPos.Y - 1);
            IMapChunk westMapChunk = capi.World.BlockAccessor.GetMapChunk(chunkPos.X - 1, chunkPos.Y);
            IMapChunk northMapChunk = capi.World.BlockAccessor.GetMapChunk(chunkPos.X, chunkPos.Y - 1);
            if (profile)
            {
                FastMapProfileRecorder.RecordClient(
                    "fastmap_generate_fetch_mapchunks",
                    chunkPos.X,
                    0,
                    chunkPos.Y,
                    FastMapProfileRecorder.ElapsedMilliseconds(mapChunkStart),
                    category: "fastmap_image");
            }

            shadowMapReusable ??= new byte[TilePixelCount];
            shadowMapCopyReusable ??= new byte[TilePixelCount];
            surfaceHeightReusable ??= new int[TilePixelCount];
            surfaceChunkYReusable ??= new int[TilePixelCount];
            surfaceBlockIdReusable ??= new int[TilePixelCount];
            byte[] shadowMap = shadowMapReusable;
            byte[] shadowMapCopy = shadowMapCopyReusable;
            int[] surfaceHeights = surfaceHeightReusable;
            int[] surfaceChunkYs = surfaceChunkYReusable;
            int[] surfaceBlockIds = surfaceBlockIdReusable;
            Array.Fill(shadowMap, (byte)128);

            BlockPos blockPos = new(0);
            long pixelLoopStart = profile ? FastMapProfileRecorder.Timestamp() : 0;
            if (!TryGetCachedSurfaceTile(chunkPos, out FastMapSurfaceTile? surfaceTile))
            {
                long surfaceExtractStart = pixelLoopStart;
                ExtractSurfaceTile(mapChunk, surfaceHeights, surfaceChunkYs, surfaceBlockIds);
                if (profile)
                {
                    FastMapProfileRecorder.RecordClient(
                        "fastmap_generate_surface_extract",
                        chunkPos.X,
                        0,
                        chunkPos.Y,
                        FastMapProfileRecorder.ElapsedMilliseconds(surfaceExtractStart),
                        detail: colorAccurate ? "coloraccurate" : "cache_miss",
                        category: "fastmap_image");
                }

                surfaceTile = StoreSurfaceTile(chunkPos, surfaceHeights, surfaceChunkYs, surfaceBlockIds);
            }
            else
            {
                surfaceHeights = surfaceTile!.Heights;
                surfaceChunkYs = surfaceTile.ChunkYs;
                surfaceBlockIds = surfaceTile.BlockIds;
            }

            long surfaceRenderStart = profile ? FastMapProfileRecorder.Timestamp() : 0;
            SurfaceRenderProfile surfaceRenderProfile = default;
            RenderSurfaceTile(chunkPos, mapChunk, northwestMapChunk, westMapChunk, northMapChunk, pixels, shadowMap, surfaceHeights, surfaceChunkYs, surfaceBlockIds, blockPos, profile, ref surfaceRenderProfile);
            if (profile)
            {
                string detail = surfaceRenderProfile.Detail(colorAccurate);
                FastMapProfileRecorder.RecordClient(
                    "fastmap_generate_surface_render",
                    chunkPos.X,
                    0,
                    chunkPos.Y,
                    FastMapProfileRecorder.ElapsedMilliseconds(surfaceRenderStart),
                    TilePixelCount,
                    detail,
                    category: "fastmap_image");
                FastMapProfileRecorder.RecordClient(
                    "fastmap_render_shade",
                    chunkPos.X,
                    0,
                    chunkPos.Y,
                    surfaceRenderProfile.ShadeMs,
                    surfaceRenderProfile.ShadedPixels,
                    detail,
                    category: "fastmap_image");
                FastMapProfileRecorder.RecordClient(
                    "fastmap_render_lake",
                    chunkPos.X,
                    0,
                    chunkPos.Y,
                    surfaceRenderProfile.LakeMs,
                    surfaceRenderProfile.LakePixels,
                    detail,
                    category: "fastmap_image");
                FastMapProfileRecorder.RecordClient(
                    "fastmap_render_coloraccurate",
                    chunkPos.X,
                    0,
                    chunkPos.Y,
                    surfaceRenderProfile.ColorAccurateMs,
                    surfaceRenderProfile.ColorAccuratePixels,
                    detail,
                    category: "fastmap_image");
                FastMapProfileRecorder.RecordClient(
                    "fastmap_render_normal",
                    chunkPos.X,
                    0,
                    chunkPos.Y,
                    surfaceRenderProfile.NormalMs,
                    surfaceRenderProfile.NormalPixels,
                    detail,
                    category: "fastmap_image");
            }

            if (profile)
            {
                FastMapProfileRecorder.RecordClient(
                    "fastmap_generate_pixel_loop",
                    chunkPos.X,
                    0,
                    chunkPos.Y,
                    FastMapProfileRecorder.ElapsedMilliseconds(pixelLoopStart),
                    kind: "inclusive",
                    category: "fastmap_image");
            }

            long blurStart = profile ? FastMapProfileRecorder.Timestamp() : 0;
            Array.Copy(shadowMap, shadowMapCopy, TilePixelCount);
            BlurTool.Blur(shadowMap, ChunkSize, ChunkSize, 2);
            if (profile)
            {
                FastMapProfileRecorder.RecordClient(
                    "fastmap_generate_blur",
                    chunkPos.X,
                    0,
                    chunkPos.Y,
                    FastMapProfileRecorder.ElapsedMilliseconds(blurStart),
                    category: "fastmap_image");
            }

            long colorMultiplyStart = profile ? FastMapProfileRecorder.Timestamp() : 0;
            for (int i = 0; i < TilePixelCount; i++)
            {
                float blurred = ((shadowMap[i] / 128f) - 1f) * 5f;
                float detail = (((shadowMapCopy[i] / 128f) - 1f) * 5f) % 1f;
                float multiplier = blurred / 5f + detail / 5f + 1f;
                pixels[i] = ColorUtil.ColorMultiply3Clamped(pixels[i], multiplier) | unchecked((int)0xFF000000);
            }
            if (profile)
            {
                FastMapProfileRecorder.RecordClient(
                    "fastmap_generate_color_multiply",
                    chunkPos.X,
                    0,
                    chunkPos.Y,
                    FastMapProfileRecorder.ElapsedMilliseconds(colorMultiplyStart),
                    category: "fastmap_image");
            }

            ClearChunkScratch();
            success = true;
            return pixels;
        }
        finally
        {
            Interlocked.Add(ref generationMs, stopwatch.ElapsedMilliseconds);
            FastMapProfileRecorder.RecordClient(
                "fastmap_generate_chunk_image",
                chunkPos.X,
                0,
                chunkPos.Y,
                stopwatch.Elapsed.TotalMilliseconds,
                success ? TilePixelCount * sizeof(int) : 0,
                success ? "success" : "missing_source",
                kind: "inclusive",
                category: "fastmap_image");
        }
    }

    private void AddKnownMapDbPositions(IEnumerable<FastVec2i> chunkCoords)
    {
        foreach (FastVec2i chunkCoord in chunkCoords)
        {
            mapDbKnownPositions?.Add(chunkCoord.ToChunkIndex());
            MarkMapDbPageKnown(PageKey(chunkCoord));
        }
    }

    private void MarkMapDbPageKnown(FastVec2i pageKey)
    {
        if (mapDbKnownPageKeys == null)
        {
            return;
        }

        lock (dbLock)
        {
            mapDbKnownPageKeys.Add(pageKey);
        }

        lock (pageLoadLock)
        {
            knownMissingPages.Remove(pageKey);
        }
    }

    private bool TryGetCachedSurfaceTile(FastVec2i chunkPos, out FastMapSurfaceTile? surfaceTile)
    {
        surfaceTile = null;
        if (config.SurfaceTileCacheBudget <= 0)
        {
            return false;
        }

        bool profile = FastMapProfileRecorder.ClientEnabled;
        long lookupStart = profile ? FastMapProfileRecorder.Timestamp() : 0;
        bool hit;
        lock (surfaceTileCacheLock)
        {
            hit = surfaceTileCache.TryGetValue(chunkPos, out surfaceTile);
            if (hit && surfaceTile != null)
            {
                if (surfaceTile.ColorAccurate == colorAccurate)
                {
                    surfaceTile.LastTouchedMs = capi.ElapsedMilliseconds;
                }
                else
                {
                    hit = false;
                    surfaceTile = null;
                }
            }
        }

        if (profile)
        {
            FastMapProfileRecorder.RecordClient(
                "fastmap_surface_cache_lookup",
                chunkPos.X,
                0,
                chunkPos.Y,
                FastMapProfileRecorder.ElapsedMilliseconds(lookupStart),
                detail: hit ? "hit" : "miss",
                category: "fastmap_image");
        }

        return hit;
    }

    private FastMapSurfaceTile? StoreSurfaceTile(FastVec2i chunkPos, int[] surfaceHeights, int[] surfaceChunkYs, int[] surfaceBlockIds)
    {
        if (config.SurfaceTileCacheBudget <= 0)
        {
            return null;
        }

        bool profile = FastMapProfileRecorder.ClientEnabled;
        long storeStart = profile ? FastMapProfileRecorder.Timestamp() : 0;
        FastMapSurfaceTile surfaceTile = new(chunkPos, colorAccurate, surfaceHeights, surfaceChunkYs, surfaceBlockIds)
        {
            LastTouchedMs = capi.ElapsedMilliseconds
        };

        int cacheCount;
        lock (surfaceTileCacheLock)
        {
            surfaceTileCache[chunkPos] = surfaceTile;
            EvictSurfaceTileCacheLocked();
            cacheCount = surfaceTileCache.Count;
        }

        if (profile)
        {
            FastMapProfileRecorder.RecordClient(
                "fastmap_surface_cache_store",
                chunkPos.X,
                0,
                chunkPos.Y,
                FastMapProfileRecorder.ElapsedMilliseconds(storeStart),
                TilePixelCount * sizeof(int) * 3,
                detail: cacheCount.ToString(),
                category: "fastmap_image");
        }

        return surfaceTile;
    }

    private void InvalidateSurfaceTile(FastVec2i chunkCoord)
    {
        lock (surfaceTileCacheLock)
        {
            surfaceTileCache.Remove(chunkCoord);
        }
    }

    private void EvictSurfaceTileCacheLocked()
    {
        int budget = config.SurfaceTileCacheBudget;
        while (surfaceTileCache.Count > budget)
        {
            FastVec2i oldestKey = default;
            long oldestTouchedMs = long.MaxValue;
            bool found = false;
            foreach (KeyValuePair<FastVec2i, FastMapSurfaceTile> entry in surfaceTileCache)
            {
                if (!found || entry.Value.LastTouchedMs < oldestTouchedMs)
                {
                    oldestKey = entry.Key;
                    oldestTouchedMs = entry.Value.LastTouchedMs;
                    found = true;
                }
            }

            if (!found)
            {
                return;
            }

            surfaceTileCache.Remove(oldestKey);
        }
    }

    private void ExtractSurfaceTile(IMapChunk mapChunk, int[] surfaceHeights, int[] surfaceChunkYs, int[] surfaceBlockIds)
    {
        for (int localZ = 0; localZ < ChunkSize; localZ++)
        {
            int rowOffset = localZ * ChunkSize;
            for (int localX = 0; localX < ChunkSize; localX++)
            {
                int index = rowOffset + localX;
                int height = mapChunk.RainHeightMap[index];
                int chunkY = height / ChunkSize;
                int blockId = 0;

                if (chunkY >= 0 && chunkY < chunksTmp.Length)
                {
                    blockId = ReadBlockId(chunksTmp[chunkY], localX, height % ChunkSize, localZ);
                    if (colorAccurate && config.EnableTrueColorAirSurfaceRepair && IsAir(blockId))
                    {
                        TryRepairTrueColorAirSurface(localX, localZ, ref height, ref chunkY, ref blockId);
                    }

                    if (IsSnow(blockId) && !colorAccurate && height > 0)
                    {
                        height--;
                        chunkY = height / ChunkSize;
                        if (chunkY >= 0 && chunkY < chunksTmp.Length)
                        {
                            blockId = ReadBlockId(chunksTmp[chunkY], localX, height % ChunkSize, localZ);
                        }
                    }
                }

                surfaceHeights[index] = height;
                surfaceChunkYs[index] = chunkY;
                surfaceBlockIds[index] = blockId;
            }
        }
    }

    private bool TryRepairTrueColorAirSurface(int localX, int localZ, ref int height, ref int chunkY, ref int blockId)
    {
        int minHeight = Math.Max(0, height - config.TrueColorAirSurfaceRepairDepth);
        for (int sampleY = height - 1; sampleY >= minHeight; sampleY--)
        {
            int sampleChunkY = sampleY / ChunkSize;
            if ((uint)sampleChunkY >= (uint)chunksTmp.Length)
            {
                continue;
            }

            int sampleBlockId = ReadBlockId(chunksTmp[sampleChunkY], localX, sampleY % ChunkSize, localZ);
            if (IsAir(sampleBlockId))
            {
                continue;
            }

            height = sampleY;
            chunkY = sampleChunkY;
            blockId = sampleBlockId;
            return true;
        }

        return false;
    }

    private void RenderSurfaceTile(
        FastVec2i chunkPos,
        IMapChunk mapChunk,
        IMapChunk northwestMapChunk,
        IMapChunk westMapChunk,
        IMapChunk northMapChunk,
        int[] pixels,
        byte[] shadowMap,
        int[] surfaceHeights,
        int[] surfaceChunkYs,
        int[] surfaceBlockIds,
        BlockPos blockPos,
        bool profile,
        ref SurfaceRenderProfile renderProfile)
    {
        if (colorAccurate)
        {
            RenderSurfaceTileColorAccurate(chunkPos, mapChunk, northwestMapChunk, westMapChunk, northMapChunk, pixels, shadowMap, surfaceHeights, surfaceChunkYs, surfaceBlockIds, blockPos, profile, ref renderProfile);
            return;
        }

        RenderSurfaceTileFast(chunkPos, mapChunk, northwestMapChunk, westMapChunk, northMapChunk, pixels, shadowMap, surfaceHeights, surfaceChunkYs, surfaceBlockIds, profile, ref renderProfile);
    }

    private void RenderSurfaceTileFast(
        FastVec2i chunkPos,
        IMapChunk mapChunk,
        IMapChunk northwestMapChunk,
        IMapChunk westMapChunk,
        IMapChunk northMapChunk,
        int[] pixels,
        byte[] shadowMap,
        int[] surfaceHeights,
        int[] surfaceChunkYs,
        int[] surfaceBlockIds,
        bool profile,
        ref SurfaceRenderProfile renderProfile)
    {
        LakeNeighborChunkCache? lakeNeighborCache = null;
        for (int localZ = 0; localZ < ChunkSize; localZ++)
        {
            int rowOffset = localZ * ChunkSize;
            for (int localX = 0; localX < ChunkSize; localX++)
            {
                int index = rowOffset + localX;
                int height = surfaceHeights[index];
                int chunkY = surfaceChunkYs[index];
                if (chunkY < 0 || chunkY >= chunksTmp.Length)
                {
                    if (profile)
                    {
                        renderProfile.InvalidPixels++;
                    }

                    continue;
                }

                int blockId = surfaceBlockIds[index];
                long shadeStart = profile ? FastMapProfileRecorder.Timestamp() : 0;
                float shade = CalculateShade(mapChunk, northwestMapChunk, westMapChunk, northMapChunk, localX, localZ, height, index);
                if (profile)
                {
                    renderProfile.ShadeMs += FastMapProfileRecorder.ElapsedMilliseconds(shadeStart);
                    renderProfile.ShadedPixels++;
                }

                if (IsLake(blockId))
                {
                    long lakeStart = profile ? FastMapProfileRecorder.Timestamp() : 0;
                    lakeNeighborCache ??= new LakeNeighborChunkCache(chunksTmp.Length);
                    pixels[index] = GetLakeColor(chunkPos, lakeNeighborCache, chunkY, localX, localZ, height, blockId);
                    if (profile)
                    {
                        renderProfile.LakeMs += FastMapProfileRecorder.ElapsedMilliseconds(lakeStart);
                        renderProfile.LakePixels++;
                    }
                }
                else
                {
                    long normalStart = profile ? FastMapProfileRecorder.Timestamp() : 0;
                    shadowMap[index] = (byte)Math.Clamp((int)(shadowMap[index] * shade), 0, 255);
                    pixels[index] = (uint)blockId < (uint)blockColorByBlockId.Length ? blockColorByBlockId[blockId] : fallbackLandColor;
                    if (profile)
                    {
                        renderProfile.NormalMs += FastMapProfileRecorder.ElapsedMilliseconds(normalStart);
                        renderProfile.NormalPixels++;
                    }
                }
            }
        }
    }

    private void RenderSurfaceTileColorAccurate(
        FastVec2i chunkPos,
        IMapChunk mapChunk,
        IMapChunk northwestMapChunk,
        IMapChunk westMapChunk,
        IMapChunk northMapChunk,
        int[] pixels,
        byte[] shadowMap,
        int[] surfaceHeights,
        int[] surfaceChunkYs,
        int[] surfaceBlockIds,
        BlockPos blockPos,
        bool profile,
        ref SurfaceRenderProfile renderProfile)
    {
        for (int localZ = 0; localZ < ChunkSize; localZ++)
        {
            int rowOffset = localZ * ChunkSize;
            for (int localX = 0; localX < ChunkSize; localX++)
            {
                int index = rowOffset + localX;
                int height = surfaceHeights[index];
                int chunkY = surfaceChunkYs[index];
                if (chunkY < 0 || chunkY >= chunksTmp.Length)
                {
                    if (profile)
                    {
                        renderProfile.InvalidPixels++;
                    }

                    continue;
                }

                int blockId = surfaceBlockIds[index];
                long shadeStart = profile ? FastMapProfileRecorder.Timestamp() : 0;
                float shade = CalculateShade(mapChunk, northwestMapChunk, westMapChunk, northMapChunk, localX, localZ, height, index);
                if (profile)
                {
                    renderProfile.ShadeMs += FastMapProfileRecorder.ElapsedMilliseconds(shadeStart);
                    renderProfile.ShadedPixels++;
                }

                long colorStart = profile ? FastMapProfileRecorder.Timestamp() : 0;
                if (IsAir(blockId))
                {
                    pixels[index] = trueColorAirFallbackColor;
                }
                else
                {
                    Block block = api.World.Blocks[blockId];
                    blockPos.Set(ChunkSize * chunkPos.X + localX, height, ChunkSize * chunkPos.Y + localZ);
                    pixels[index] = GetColorAccurateMapColor(block, blockId, blockPos, chunkPos);
                }

                shadowMap[index] = (byte)Math.Clamp((int)(shadowMap[index] * shade), 0, 255);
                if (profile)
                {
                    renderProfile.ColorAccurateMs += FastMapProfileRecorder.ElapsedMilliseconds(colorStart);
                    renderProfile.ColorAccuratePixels++;
                }
            }
        }
    }

    private int GetColorAccurateMapColor(Block block, int blockId, BlockPos blockPos, FastVec2i chunkPos)
    {
        try
        {
            int color = block.GetColor(capi, blockPos);
            int randomColor = block.GetRandomColor(capi, blockPos, BlockFacing.UP, GameMath.MurmurHash3Mod(blockPos.X, blockPos.Y, blockPos.Z, 30));
            randomColor = ((randomColor & 0xFF) << 16) | (((randomColor >> 8) & 0xFF) << 8) | ((randomColor >> 16) & 0xFF);
            int finalColor = ColorUtil.ColorOverlay(color, randomColor, colorRandomizationWeight);
            LogTrueColorBrightSampleIfNeeded(block, blockId, blockPos, color, randomColor, finalColor);
            return finalColor;
        }
        catch (Exception ex)
        {
            string blockCode = block.Code?.ToShortString() ?? blockId.ToString();
            FastMapProfileRecorder.RecordClient(
                "fastmap_coloraccurate_fallback",
                chunkPos.X,
                blockPos.Y,
                chunkPos.Y,
                detail: blockCode,
                kind: "event",
                category: "fastmap_image");
            if (loggedColorAccurateFallbacks.TryAdd(blockCode, 0))
            {
                api.Logger.Warning(
                    "[FastMap] Falling back to cached map color for block {0} after color-accurate lookup failed at {1}/{2}/{3}: {4}",
                    blockCode,
                    blockPos.X,
                    blockPos.Y,
                    blockPos.Z,
                    ex.Message);
            }

            return GetMapColor(blockId);
        }
    }

    private void LogTrueColorBrightSampleIfNeeded(Block block, int blockId, BlockPos blockPos, int baseColor, int randomColor, int finalColor)
    {
        if (!config.LogTrueColorBrightSamples || IsLake(blockId) || !IsNearWhite(finalColor))
        {
            return;
        }

        string blockCode = block.Code?.ToShortString() ?? blockId.ToString();
        if (!loggedTrueColorBrightSamples.TryAdd(blockCode, 0))
        {
            return;
        }

        api.Logger.Notification(
            "[FastMap] Bright true-colour sample block={0}, material={1}, pos={2}/{3}/{4}, base={5}, random={6}, final={7}, textureSubId={8}, climateMap={9}, seasonMap={10}, shapeUsesColormap={11}",
            blockCode,
            block.BlockMaterial,
            blockPos.X,
            blockPos.Y,
            blockPos.Z,
            FormatColor(baseColor),
            FormatColor(randomColor),
            FormatColor(finalColor),
            block.TextureSubIdForBlockColor,
            block.ClimateColorMapResolved != null,
            block.SeasonColorMapResolved != null,
            block.ShapeUsesColormap);
    }

    private static bool IsNearWhite(int color)
    {
        int r = color & 0xFF;
        int g = (color >> 8) & 0xFF;
        int b = (color >> 16) & 0xFF;
        return r >= 245 && g >= 245 && b >= 245;
    }

    private static string FormatColor(int color)
    {
        int r = color & 0xFF;
        int g = (color >> 8) & 0xFF;
        int b = (color >> 16) & 0xFF;
        int a = (color >> 24) & 0xFF;
        return $"#{r:X2}{g:X2}{b:X2}{a:X2}";
    }

    private static float CalculateShade(IMapChunk center, IMapChunk northwest, IMapChunk west, IMapChunk north, int localX, int localZ, int height, int index)
    {
        if (localX > 0 && localZ > 0)
        {
            ushort[] heightMap = center.RainHeightMap;
            return ShadeFromDeltas(
                height - heightMap[index - ChunkSize - 1],
                height - heightMap[index - 1],
                height - heightMap[index - ChunkSize]);
        }

        return CalculateEdgeShade(center, northwest, west, north, localX, localZ, height);
    }

    private static float CalculateEdgeShade(IMapChunk center, IMapChunk northwest, IMapChunk west, IMapChunk north, int localX, int localZ, int height)
    {
        IMapChunk diagonal = center;
        IMapChunk westSample = center;
        IMapChunk northSample = center;
        int westX = localX - 1;
        int northZ = localZ - 1;

        if (westX < 0 && northZ < 0)
        {
            diagonal = northwest;
            westSample = west;
            northSample = north;
        }
        else
        {
            if (westX < 0)
            {
                diagonal = west;
                westSample = west;
            }

            if (northZ < 0)
            {
                diagonal = north;
                northSample = north;
            }
        }

        westX = westX < 0 ? ChunkSize - 1 : westX;
        northZ = northZ < 0 ? ChunkSize - 1 : northZ;
        int diagonalDelta = diagonal != null ? height - diagonal.RainHeightMap[northZ * ChunkSize + westX] : 0;
        int westDelta = westSample != null ? height - westSample.RainHeightMap[localZ * ChunkSize + westX] : 0;
        int northDelta = northSample != null ? height - northSample.RainHeightMap[northZ * ChunkSize + localX] : 0;
        return ShadeFromDeltas(diagonalDelta, westDelta, northDelta);
    }

    private static float ShadeFromDeltas(int diagonalDelta, int westDelta, int northDelta)
    {
        int signSum = Sign(diagonalDelta) + Sign(westDelta) + Sign(northDelta);
        int maxDelta = Math.Max(Math.Max(Math.Abs(diagonalDelta), Math.Abs(westDelta)), Math.Abs(northDelta));

        if (signSum > 0)
        {
            return 1.08f + Math.Min(0.5f, maxDelta / 10f) / 1.25f;
        }

        if (signSum < 0)
        {
            return 0.92f - Math.Min(0.5f, maxDelta / 10f) / 1.25f;
        }

        return 1f;
    }

    private static int Sign(int value)
    {
        return value > 0 ? 1 : value < 0 ? -1 : 0;
    }

    private int GetLakeColor(FastVec2i chunkPos, LakeNeighborChunkCache cache, int chunkY, int localX, int localZ, int height, int blockId)
    {
        IWorldChunk? westChunk = chunksTmp[chunkY];
        IWorldChunk? eastChunk = chunksTmp[chunkY];
        IWorldChunk? northChunk = chunksTmp[chunkY];
        IWorldChunk? southChunk = chunksTmp[chunkY];

        int westX = localX - 1;
        int eastX = localX + 1;
        int northZ = localZ - 1;
        int southZ = localZ + 1;

        if (westX < 0)
        {
            westChunk = GetCachedLakeNeighborChunk(cache.WestChunks, cache.WestLoaded, chunkPos.X - 1, chunkY, chunkPos.Y);
        }

        if (eastX >= ChunkSize)
        {
            eastChunk = GetCachedLakeNeighborChunk(cache.EastChunks, cache.EastLoaded, chunkPos.X + 1, chunkY, chunkPos.Y);
        }

        if (northZ < 0)
        {
            northChunk = GetCachedLakeNeighborChunk(cache.NorthChunks, cache.NorthLoaded, chunkPos.X, chunkY, chunkPos.Y - 1);
        }

        if (southZ >= ChunkSize)
        {
            southChunk = GetCachedLakeNeighborChunk(cache.SouthChunks, cache.SouthLoaded, chunkPos.X, chunkY, chunkPos.Y + 1);
        }

        if (westChunk != null && eastChunk != null && northChunk != null && southChunk != null)
        {
            westX = westX < 0 ? ChunkSize - 1 : westX;
            eastX = eastX >= ChunkSize ? 0 : eastX;
            northZ = northZ < 0 ? ChunkSize - 1 : northZ;
            southZ = southZ >= ChunkSize ? 0 : southZ;
            int localY = height % ChunkSize;

            if (IsLake(ReadBlockId(westChunk, westX, localY, localZ))
                && IsLake(ReadBlockId(eastChunk, eastX, localY, localZ))
                && IsLake(ReadBlockId(northChunk, localX, localY, northZ))
                && IsLake(ReadBlockId(southChunk, localX, localY, southZ)))
            {
                return GetMapColor(blockId);
            }

            return colorsByCode["wateredge"];
        }

        return GetMapColor(blockId);
    }

    private IWorldChunk? GetCachedLakeNeighborChunk(IWorldChunk?[] chunks, bool[] loaded, int chunkX, int chunkY, int chunkZ)
    {
        if ((uint)chunkY >= (uint)chunks.Length)
        {
            return null;
        }

        if (!loaded[chunkY])
        {
            loaded[chunkY] = true;
            IWorldChunk chunk = capi.World.BlockAccessor.GetChunk(chunkX, chunkY, chunkZ);
            chunk?.Unpack_ReadOnly();
            chunks[chunkY] = chunk;
        }

        return chunks[chunkY];
    }

    private int GetMapColor(int blockId)
    {
        if (blockId >= 0 && blockId < blockColorByBlockId.Length)
        {
            return blockColorByBlockId[blockId];
        }

        return fallbackLandColor;
    }

    private void ClearChunkScratch()
    {
        Array.Clear(chunksTmp, 0, chunksTmp.Length);
    }

    private static int ReadBlockId(IWorldChunk chunk, int localX, int localY, int localZ)
    {
        return chunk.Data.GetBlockId(ChunkIndex3d(localX, localY, localZ), 3);
    }

    private bool IsValidTile(FastVec2i coord)
    {
        return api.World.BlockAccessor.IsValidPos(new BlockPos(coord.X * ChunkSize, 1, coord.Y * ChunkSize));
    }

    private static bool IsLake(Block block)
    {
        return IsWaterMaterial(block.BlockMaterial)
            || (block.BlockMaterial == EnumBlockMaterial.Ice && block.Code?.Path != "glacierice");
    }

    private static bool IsWaterMaterial(EnumBlockMaterial material)
    {
        return material.ToString() == "Water" || (int)material == 8;
    }

    private bool IsLake(int blockId)
    {
        return blockId >= 0 && blockId < blockIsLakeByBlockId.Length && blockIsLakeByBlockId[blockId];
    }

    private bool IsSnow(int blockId)
    {
        return blockId >= 0 && blockId < blockIsSnowByBlockId.Length && blockIsSnowByBlockId[blockId];
    }

    private static bool IsAir(int blockId)
    {
        return blockId <= 0;
    }

    private static FastVec2i PageKey(FastVec2i chunkCoord)
    {
        return new FastVec2i(FloorDiv(chunkCoord.X, ChunksPerPage), FloorDiv(chunkCoord.Y, ChunksPerPage));
    }

    private static int FloorDiv(int value, int divisor)
    {
        int result = value / divisor;
        int remainder = value % divisor;
        return remainder != 0 && ((remainder < 0) != (divisor < 0)) ? result - 1 : result;
    }

    private static int PositiveMod(int value, int divisor)
    {
        int result = value % divisor;
        return result < 0 ? result + divisor : result;
    }

    private static bool ReasonContains(string existingReasons, string reason)
    {
        string[] parts = existingReasons.Split('+');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] == reason)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCompletePage(uint[] validRows)
    {
        if (validRows.Length != ChunksPerPage)
        {
            return false;
        }

        for (int i = 0; i < validRows.Length; i++)
        {
            if (validRows[i] != uint.MaxValue)
            {
                return false;
            }
        }

        return true;
    }

    private static string RepairDetail(string reason, bool force)
    {
        return reason + ";mode=" + (force ? "force" : "normal");
    }

    private static int ChunkIndex3d(int x, int y, int z)
    {
        return (y * ChunkSize + z) * ChunkSize + x;
    }

    private struct SurfaceRenderProfile
    {
        public double ShadeMs;
        public double LakeMs;
        public double ColorAccurateMs;
        public double NormalMs;
        public int ShadedPixels;
        public int LakePixels;
        public int ColorAccuratePixels;
        public int NormalPixels;
        public int InvalidPixels;

        public readonly string Detail(bool colorAccurate)
        {
            return "mode=" + (colorAccurate ? "coloraccurate" : "normal")
                + ";shadePixels=" + ShadedPixels
                + ";normalPixels=" + NormalPixels
                + ";lakePixels=" + LakePixels
                + ";colorAccuratePixels=" + ColorAccuratePixels
                + ";invalidPixels=" + InvalidPixels;
        }
    }

    private sealed class LakeNeighborChunkCache
    {
        public LakeNeighborChunkCache(int chunkCount)
        {
            WestChunks = new IWorldChunk?[chunkCount];
            EastChunks = new IWorldChunk?[chunkCount];
            NorthChunks = new IWorldChunk?[chunkCount];
            SouthChunks = new IWorldChunk?[chunkCount];
            WestLoaded = new bool[chunkCount];
            EastLoaded = new bool[chunkCount];
            NorthLoaded = new bool[chunkCount];
            SouthLoaded = new bool[chunkCount];
        }

        public IWorldChunk?[] WestChunks { get; }
        public IWorldChunk?[] EastChunks { get; }
        public IWorldChunk?[] NorthChunks { get; }
        public IWorldChunk?[] SouthChunks { get; }
        public bool[] WestLoaded { get; }
        public bool[] EastLoaded { get; }
        public bool[] NorthLoaded { get; }
        public bool[] SouthLoaded { get; }
    }

    private static void CopyTileIntoPage(int[] tilePixels, int[] pagePixels, int localChunkX, int localChunkZ)
    {
        int dstX = localChunkX * ChunkSize;
        int dstY = localChunkZ * ChunkSize;
        for (int row = 0; row < ChunkSize; row++)
        {
            Array.Copy(tilePixels, row * ChunkSize, pagePixels, (dstY + row) * FastMapPageComponent.PageSize + dstX, ChunkSize);
        }
    }

    private readonly record struct NativeDbPageCandidate(ulong Position, int LocalChunkX, int LocalChunkZ);
}
