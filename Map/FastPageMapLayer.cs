using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using FastMap.Config;
using FastMap.Profiling;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace FastMap.Map;

public sealed class FastPageMapLayer : RGBMapLayer
{
    private const int ChunkSize = FastMapPageComponent.ChunkSize;
    private const int ChunksPerPage = FastMapPageComponent.ChunksPerPage;
    private const int TilePixelCount = ChunkSize * ChunkSize;
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
    private readonly FastMapTextureAtlas? textureAtlas;
    private readonly Dictionary<FastVec2i, FastMapPageComponent> pages = new();
    private readonly HashSet<FastVec2i> visibleChunks = new();
    private readonly HashSet<FastVec2i> visiblePageKeys = new();
    private readonly Queue<FastVec2i> pageUploadQueue = new();
    private readonly HashSet<FastVec2i> queuedPageUploads = new();
    private readonly HashSet<FastVec2i> pagesNeedingUpload = new();

    private readonly object pageLoadLock = new();
    private readonly Queue<FastVec2i> pageLoadQueue = new();
    private readonly HashSet<FastVec2i> queuedPageLoads = new();
    private readonly HashSet<FastVec2i> knownMissingPages = new();
    private readonly ConcurrentQueue<FastMapPageSnapshot> readyPages = new();

    private readonly object repairLock = new();
    private readonly Queue<FastVec2i> repairQueue = new();
    private readonly SortedDictionary<long, Queue<FastVec2i>> delayedRepairQueue = new();
    private readonly HashSet<FastVec2i> queuedRepairs = new();
    private readonly Dictionary<FastVec2i, string> queuedRepairReasons = new();
    private readonly Dictionary<FastVec2i, long> queuedRepairDueMs = new();
    private readonly ConcurrentQueue<FastMapPagePatch> readyPatches = new();
    private readonly HashSet<FastVec2i> chunksKnownValid = new();
    private readonly object chunkValidityLock = new();

    private readonly object dbLock = new();
    private readonly object pageSaveLock = new();
    private readonly Dictionary<FastVec2i, FastMapPageSnapshot> pendingPageSaves = new();
    private readonly Dictionary<FastVec2i, MapPieceDB> pendingTileSaves = new();

    private MapDB? mapdb;
    private IWorldChunk[] chunksTmp = Array.Empty<IWorldChunk>();
    private Dictionary<string, int> colorsByCode = new();
    private int[] blockColorByBlockId = Array.Empty<int>();
    private bool[] blockIsLakeByBlockId = Array.Empty<bool>();
    private bool[] blockIsSnowByBlockId = Array.Empty<bool>();
    private bool colorAccurate;
    private float colorRandomizationWeight = 0.6f;
    private float workerAccum;
    private float prewarmAccum;
    private float flushAccum;
    private float evictAccum;
    private float statsAccum;
    private int activePageLoadTasks;

    private long pageDiskHits;
    private long pageDiskMisses;
    private long pageDbHits;
    private long pageDbMisses;
    private long pageUploads;
    private long pageSaves;
    private long generatedChunks;
    private long missingSourceChunks;
    private long tileDbHits;
    private long pageLoadMs;
    private long pageUploadMs;
    private long generationMs;
    private bool disposed;

    [ThreadStatic]
    private static byte[]? shadowMapReusable;

    [ThreadStatic]
    private static byte[]? shadowMapCopyReusable;

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
        pageDiskCache = new FastMapPageDiskCache(api.World.SavegameIdentifier, config.EnableCompressedCache, config.UseFilteredCache, config.UseHighCompressionCache);
        textureAtlas = config.EnableTextureAtlas ? new FastMapTextureAtlas(capi) : null;

        OpenMapDatabase();
        api.Event.ChunkDirty += OnChunkDirty;
        api.Logger.Notification("[FastMap] Page cache: {0}", pageDiskCache.RootPath);
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

        workerAccum += dt;
        flushAccum += dt;

        if (workerAccum >= config.BackgroundWorkIntervalSeconds)
        {
            workerAccum = 0f;
            StartPageLoadTasks();
            ProcessChunkRepairs(config.MaxBackgroundTilesPerPass);
        }

        if (flushAccum >= config.PageFlushIntervalSeconds || PendingPageSaveCount() >= config.PageFlushThreshold)
        {
            flushAccum = 0f;
            FlushPendingSaves();
        }
    }

    public override void OnTick(float dt)
    {
        if (disposed)
        {
            return;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        ProcessReadyPages(stopwatch);
        ProcessReadyPatches(stopwatch);
        ProcessQueuedPageUploads(stopwatch);
        PrewarmAroundPlayer(dt);
        EvictPages(dt);
        LogStats(dt);
    }

    public override void Render(GuiElementMap mapElem, float dt)
    {
        if (disposed || !Active)
        {
            return;
        }

        foreach (FastVec2i pageKey in visiblePageKeys)
        {
            if (pages.TryGetValue(pageKey, out FastMapPageComponent? page) && page.HasGpuTexture)
            {
                page.LastTouchedMs = capi.ElapsedMilliseconds;
                page.Render(mapElem, dt);
            }
        }
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
        FlushPendingSaves();
        mapdb?.Dispose();
        mapdb = null;

        foreach (FastMapPageComponent page in pages.Values)
        {
            page.DisposeTexture();
        }

        textureAtlas?.Dispose();
        pages.Clear();
        ClearQueues();
        visibleChunks.Clear();
        visiblePageKeys.Clear();
        chunksKnownValid.Clear();
        colorsByCode.Clear();
        blockColorByBlockId = Array.Empty<int>();
        blockIsLakeByBlockId = Array.Empty<bool>();
        blockIsSnowByBlockId = Array.Empty<bool>();
        chunksTmp = Array.Empty<IWorldChunk>();
    }

    private void OpenMapDatabase()
    {
        mapdb = new MapDB(api.World.Logger);
        string? error = null;
        string mapsDir = Path.Combine(GamePaths.DataPath, "Maps");
        GamePaths.EnsurePathExists(mapsDir);
        string path = Path.Combine(mapsDir, api.World.SavegameIdentifier + ".db");
        if (!mapdb.OpenOrCreate(path, ref error, requireWriteAccess: true, corruptionProtection: true, doIntegrityCheck: false))
        {
            throw new Exception(error ?? $"Cannot open {path}");
        }
    }

    private void RebuildVisiblePages()
    {
        visiblePageKeys.Clear();
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

        for (int pageZ = minPage.Y; pageZ <= maxPage.Y; pageZ++)
        {
            for (int pageX = minPage.X; pageX <= maxPage.X; pageX++)
            {
                visiblePageKeys.Add(new FastVec2i(pageX, pageZ));
            }
        }

        foreach (FastVec2i pageKey in visiblePageKeys)
        {
            QueuePageLoad(pageKey);
            if (pagesNeedingUpload.Contains(pageKey))
            {
                QueuePageUpload(pageKey);
            }
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

            if (queuedPageLoads.Add(pageKey))
            {
                pageLoadQueue.Enqueue(pageKey);
            }
        }
    }

    private void StartPageLoadTasks()
    {
        if (disposed)
        {
            return;
        }

        while (Volatile.Read(ref activePageLoadTasks) < config.MaxParallelPageLoads && TryDequeuePageLoad(out FastVec2i pageKey))
        {
            Interlocked.Increment(ref activePageLoadTasks);
            Task.Run(() =>
            {
                try
                {
                    ProcessPageLoad(pageKey);
                }
                finally
                {
                    Interlocked.Decrement(ref activePageLoadTasks);
                }
            });
        }
    }

    private bool TryDequeuePageLoad(out FastVec2i pageKey)
    {
        lock (pageLoadLock)
        {
            if (pageLoadQueue.Count > 0)
            {
                pageKey = pageLoadQueue.Dequeue();
                return true;
            }
        }

        pageKey = default;
        return false;
    }

    private void ProcessPageLoad(FastVec2i pageKey)
    {
        if (disposed)
        {
            return;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        if (pageDiskCache.TryLoad(pageKey, out FastMapPageSnapshot diskSnapshot))
        {
            if (disposed)
            {
                return;
            }

            Interlocked.Increment(ref pageDiskHits);
            Interlocked.Add(ref pageLoadMs, stopwatch.ElapsedMilliseconds);
            FastMapProfileRecorder.RecordClient("fastmap_page_disk_load", pageKey.X, 0, pageKey.Y, stopwatch.Elapsed.TotalMilliseconds, detail: "hit");
            readyPages.Enqueue(diskSnapshot);
            return;
        }

        Interlocked.Increment(ref pageDiskMisses);

        if (TryBuildPageFromDb(pageKey, out FastMapPageSnapshot dbSnapshot))
        {
            if (disposed)
            {
                return;
            }

            Interlocked.Increment(ref pageDbHits);
            Interlocked.Add(ref pageLoadMs, stopwatch.ElapsedMilliseconds);
            FastMapProfileRecorder.RecordClient("fastmap_page_db_build", pageKey.X, 0, pageKey.Y, stopwatch.Elapsed.TotalMilliseconds, detail: "hit");
            QueuePageSave(dbSnapshot);
            readyPages.Enqueue(dbSnapshot);
            return;
        }

        FastMapProfileRecorder.RecordClient("fastmap_page_load", pageKey.X, 0, pageKey.Y, stopwatch.Elapsed.TotalMilliseconds, detail: "miss");

        Interlocked.Increment(ref pageDbMisses);
        Interlocked.Add(ref pageLoadMs, stopwatch.ElapsedMilliseconds);
        if (disposed)
        {
            return;
        }

        lock (pageLoadLock)
        {
            knownMissingPages.Add(pageKey);
        }
    }

    private bool TryBuildPageFromDb(FastVec2i pageKey, out FastMapPageSnapshot snapshot)
    {
        snapshot = null!;
        if (mapdb == null)
        {
            return false;
        }

        FastVec2i baseCoord = new(pageKey.X * ChunksPerPage, pageKey.Y * ChunksPerPage);
        int[] pixels = new int[FastMapPageComponent.PixelCount];
        uint[] validRows = new uint[ChunksPerPage];

        lock (dbLock)
        {
            for (int dz = 0; dz < ChunksPerPage; dz++)
            {
                for (int dx = 0; dx < ChunksPerPage; dx++)
                {
                    FastVec2i chunkCoord = new(baseCoord.X + dx, baseCoord.Y + dz);
                    MapPieceDB piece = mapdb.GetMapPiece(chunkCoord);
                    if (piece?.Pixels == null)
                    {
                        continue;
                    }

                    CopyTileIntoPage(piece.Pixels, pixels, dx, dz);
                    validRows[dz] |= 1u << dx;
                    Interlocked.Increment(ref tileDbHits);
                }
            }
        }

        snapshot = new FastMapPageSnapshot(pageKey, validRows, pixels);
        return snapshot.HasAnyValidChunks;
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

            FastMapPageComponent page = GetOrCreatePage(snapshot.PageKey);
            page.ApplySnapshot(snapshot);
            MarkSnapshotChunksKnown(snapshot);

            if (visiblePageKeys.Contains(snapshot.PageKey))
            {
                UploadPage(page);
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
            long patchStart = FastMapProfileRecorder.Timestamp();
            if (!page.HasAnyValidChunks && pageDiskCache.TryLoad(pageKey, out FastMapPageSnapshot snapshot))
            {
                page.ApplySnapshot(snapshot);
                MarkSnapshotChunksKnown(snapshot);
            }

            page.SetChunk(patch.ChunkCoord, patch.Pixels);
            MarkChunkKnownValid(patch.ChunkCoord);
            lock (pageLoadLock)
            {
                knownMissingPages.Remove(pageKey);
            }
            pagesToSave.Add(pageKey);
            QueuePageUpload(pageKey);
            FastMapProfileRecorder.RecordClient(
                "fastmap_patch_apply",
                patch.ChunkCoord.X,
                0,
                patch.ChunkCoord.Y,
                FastMapProfileRecorder.ElapsedMilliseconds(patchStart),
                patch.Pixels.Length * sizeof(int));
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

    private FastMapPageComponent GetOrCreatePage(FastVec2i pageKey)
    {
        if (!pages.TryGetValue(pageKey, out FastMapPageComponent? page))
        {
            page = new FastMapPageComponent(capi, pageKey);
            pages[pageKey] = page;
        }

        return page;
    }

    private void OnChunkDirty(Vec3i chunkCoord, IWorldChunk chunk, EnumChunkDirtyReason reason)
    {
        if (disposed || reason != EnumChunkDirtyReason.MarkedDirty || !config.RegenerateOnChunkDirty)
        {
            return;
        }

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
            IMapChunk mapChunk = api.World.BlockAccessor.GetMapChunk(chunkCoord.X, chunkCoord.Y);
            if (mapChunk == null)
            {
                Interlocked.Increment(ref missingSourceChunks);
                FastMapProfileRecorder.RecordClient("fastmap_chunk_repair_missing_mapchunk", chunkCoord.X, 0, chunkCoord.Y, detail: repairReason, category: "fastmap_repair");
                continue;
            }

            long repairStart = FastMapProfileRecorder.Timestamp();
            int[]? pixels = GenerateChunkImage(chunkCoord, mapChunk);
            if (pixels == null)
            {
                Interlocked.Increment(ref missingSourceChunks);
                FastMapProfileRecorder.RecordClient(
                    "fastmap_chunk_repair_missing_source",
                    chunkCoord.X,
                    0,
                    chunkCoord.Y,
                    FastMapProfileRecorder.ElapsedMilliseconds(repairStart),
                    detail: repairReason,
                    kind: "inclusive",
                    category: "fastmap_repair");
                continue;
            }

            if (disposed)
            {
                return;
            }

            Interlocked.Increment(ref generatedChunks);
            readyPatches.Enqueue(new FastMapPagePatch(chunkCoord, pixels));
            QueueTileSave(chunkCoord, pixels);
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

    private void MarkSnapshotChunksKnown(FastMapPageSnapshot snapshot)
    {
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
        if (disposed)
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

        if (mapdb != null && tilesToSave.Count > 0)
        {
            lock (dbLock)
            {
                try
                {
                    mapdb.SetMapPieces(tilesToSave);
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
        if (evictAccum < 1f || pages.Count <= config.PageTextureBudget)
        {
            return;
        }

        evictAccum = 0f;
        List<KeyValuePair<FastVec2i, FastMapPageComponent>> candidates = new();
        foreach (KeyValuePair<FastVec2i, FastMapPageComponent> entry in pages)
        {
            if (!visiblePageKeys.Contains(entry.Key))
            {
                candidates.Add(entry);
            }
        }

        candidates.Sort((a, b) => a.Value.LastTouchedMs.CompareTo(b.Value.LastTouchedMs));
        foreach (KeyValuePair<FastVec2i, FastMapPageComponent> candidate in candidates)
        {
            if (pages.Count <= config.PageTextureBudget)
            {
                break;
            }

            candidate.Value.DisposeTexture();
            pages.Remove(candidate.Key);
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
        api.Logger.Notification(
            "[FastMap] pages loaded={0}, visible={1}, queuedPages={2}, activeLoads={3}, readyPages={4}, readyPatches={5}, diskHits={6}, diskMisses={7}, dbHits={8}, dbMisses={9}, generated={10}, missing={11}, uploads={12}, saves={13}",
            pages.Count,
            visiblePageKeys.Count,
            QueuedPageLoadCount(),
            Volatile.Read(ref activePageLoadTasks),
            readyPages.Count,
            readyPatches.Count,
            pageDiskHits,
            pageDiskMisses,
            pageDbHits,
            pageDbMisses,
            generatedChunks,
            missingSourceChunks,
            pageUploads,
            pageSaves
        );
        api.Logger.Notification(
            "[FastMap] page timings loadMs={0}, uploadMs={1}, generationMs={2}, tileDbHits={3}, atlases={4}, path={5}",
            pageLoadMs,
            pageUploadMs,
            generationMs,
            tileDbHits,
            textureAtlas?.AtlasCount ?? 0,
            pageDiskCache.RootPath
        );
    }

    private int QueuedPageLoadCount()
    {
        lock (pageLoadLock)
        {
            return pageLoadQueue.Count;
        }
    }

    private void ClearQueues()
    {
        lock (pageLoadLock)
        {
            pageLoadQueue.Clear();
            queuedPageLoads.Clear();
            knownMissingPages.Clear();
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

        pageUploadQueue.Clear();
        queuedPageUploads.Clear();
        pagesNeedingUpload.Clear();

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
            byte[] shadowMap = shadowMapReusable;
            byte[] shadowMapCopy = shadowMapCopyReusable;
            Array.Fill(shadowMap, (byte)128);

            BlockPos blockPos = new(0);
            long pixelLoopStart = profile ? FastMapProfileRecorder.Timestamp() : 0;
            for (int index = 0; index < TilePixelCount; index++)
            {
                int height = mapChunk.RainHeightMap[index];
                int chunkY = height / ChunkSize;
                if (chunkY < 0 || chunkY >= chunksTmp.Length)
                {
                    continue;
                }

                int localX = index % ChunkSize;
                int localZ = index / ChunkSize;
                float shade = CalculateShade(mapChunk, northwestMapChunk, westMapChunk, northMapChunk, localX, localZ, height);
                int blockId = ReadBlockId(chunksTmp[chunkY], localX, height % ChunkSize, localZ);

                if (IsSnow(blockId) && !colorAccurate && height > 0)
                {
                    height--;
                    chunkY = height / ChunkSize;
                    if (chunkY >= 0 && chunkY < chunksTmp.Length)
                    {
                        blockId = ReadBlockId(chunksTmp[chunkY], localX, height % ChunkSize, localZ);
                    }
                }

                if (colorAccurate)
                {
                    Block block = api.World.Blocks[blockId];
                    blockPos.Set(ChunkSize * chunkPos.X + localX, height, ChunkSize * chunkPos.Y + localZ);
                    pixels[index] = GetColorAccurateMapColor(block, blockId, blockPos, chunkPos);
                    shadowMap[index] = (byte)Math.Clamp((int)(shadowMap[index] * shade), 0, 255);
                }
                else if (IsLake(blockId))
                {
                    pixels[index] = GetLakeColor(chunkPos, chunkY, localX, localZ, height, blockId);
                }
                else
                {
                    shadowMap[index] = (byte)Math.Clamp((int)(shadowMap[index] * shade), 0, 255);
                    pixels[index] = GetMapColor(blockId);
                }
            }
            if (profile)
            {
                FastMapProfileRecorder.RecordClient(
                    "fastmap_generate_pixel_loop",
                    chunkPos.X,
                    0,
                    chunkPos.Y,
                    FastMapProfileRecorder.ElapsedMilliseconds(pixelLoopStart),
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

    private int GetColorAccurateMapColor(Block block, int blockId, BlockPos blockPos, FastVec2i chunkPos)
    {
        try
        {
            int color = block.GetColor(capi, blockPos);
            int randomColor = block.GetRandomColor(capi, blockPos, BlockFacing.UP, GameMath.MurmurHash3Mod(blockPos.X, blockPos.Y, blockPos.Z, 30));
            randomColor = ((randomColor & 0xFF) << 16) | (((randomColor >> 8) & 0xFF) << 8) | ((randomColor >> 16) & 0xFF);
            return ColorUtil.ColorOverlay(color, randomColor, colorRandomizationWeight);
        }
        catch (Exception ex)
        {
            FastMapProfileRecorder.RecordClient(
                "fastmap_coloraccurate_fallback",
                chunkPos.X,
                blockPos.Y,
                chunkPos.Y,
                detail: block.Code?.ToShortString() ?? blockId.ToString(),
                kind: "event",
                category: "fastmap_image");
            api.Logger.Warning(
                "[FastMap] Falling back to cached map color for block {0} at {1}/{2}/{3}: {4}",
                block.Code?.ToShortString() ?? blockId.ToString(),
                blockPos.X,
                blockPos.Y,
                blockPos.Z,
                ex.Message);
            return GetMapColor(blockId);
        }
    }

    private float CalculateShade(IMapChunk center, IMapChunk northwest, IMapChunk west, IMapChunk north, int localX, int localZ, int height)
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

        westX = PositiveMod(westX, ChunkSize);
        northZ = PositiveMod(northZ, ChunkSize);
        int diagonalDelta = diagonal != null ? height - diagonal.RainHeightMap[northZ * ChunkSize + westX] : 0;
        int westDelta = westSample != null ? height - westSample.RainHeightMap[localZ * ChunkSize + westX] : 0;
        int northDelta = northSample != null ? height - northSample.RainHeightMap[northZ * ChunkSize + localX] : 0;
        float signSum = Math.Sign(diagonalDelta) + Math.Sign(westDelta) + Math.Sign(northDelta);
        float maxDelta = Math.Max(Math.Max(Math.Abs(diagonalDelta), Math.Abs(westDelta)), Math.Abs(northDelta));

        if (signSum > 0f)
        {
            return 1.08f + Math.Min(0.5f, maxDelta / 10f) / 1.25f;
        }

        if (signSum < 0f)
        {
            return 0.92f - Math.Min(0.5f, maxDelta / 10f) / 1.25f;
        }

        return 1f;
    }

    private int GetLakeColor(FastVec2i chunkPos, int chunkY, int localX, int localZ, int height, int blockId)
    {
        IWorldChunk westChunk = chunksTmp[chunkY];
        IWorldChunk eastChunk = chunksTmp[chunkY];
        IWorldChunk northChunk = chunksTmp[chunkY];
        IWorldChunk southChunk = chunksTmp[chunkY];

        int westX = localX - 1;
        int eastX = localX + 1;
        int northZ = localZ - 1;
        int southZ = localZ + 1;

        if (westX < 0)
        {
            westChunk = capi.World.BlockAccessor.GetChunk(chunkPos.X - 1, chunkY, chunkPos.Y);
        }

        if (eastX >= ChunkSize)
        {
            eastChunk = capi.World.BlockAccessor.GetChunk(chunkPos.X + 1, chunkY, chunkPos.Y);
        }

        if (northZ < 0)
        {
            northChunk = capi.World.BlockAccessor.GetChunk(chunkPos.X, chunkY, chunkPos.Y - 1);
        }

        if (southZ >= ChunkSize)
        {
            southChunk = capi.World.BlockAccessor.GetChunk(chunkPos.X, chunkY, chunkPos.Y + 1);
        }

        if (westChunk != null && eastChunk != null && northChunk != null && southChunk != null)
        {
            westX = PositiveMod(westX, ChunkSize);
            eastX = PositiveMod(eastX, ChunkSize);
            northZ = PositiveMod(northZ, ChunkSize);
            southZ = PositiveMod(southZ, ChunkSize);
            int localY = height % ChunkSize;

            westChunk.Unpack_ReadOnly();
            eastChunk.Unpack_ReadOnly();
            northChunk.Unpack_ReadOnly();
            southChunk.Unpack_ReadOnly();

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

    private int GetMapColor(int blockId)
    {
        if (blockId >= 0 && blockId < blockColorByBlockId.Length)
        {
            return blockColorByBlockId[blockId];
        }

        return colorsByCode.TryGetValue("land", out int color) ? color : unchecked((int)0xFFAC8858);
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
        return block.BlockMaterial == EnumBlockMaterial.Water
            || (block.BlockMaterial == EnumBlockMaterial.Ice && block.Code?.Path != "glacierice");
    }

    private bool IsLake(int blockId)
    {
        return blockId >= 0 && blockId < blockIsLakeByBlockId.Length && blockIsLakeByBlockId[blockId];
    }

    private bool IsSnow(int blockId)
    {
        return blockId >= 0 && blockId < blockIsSnowByBlockId.Length && blockIsSnowByBlockId[blockId];
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

    private static string RepairDetail(string reason, bool force)
    {
        return reason + ";mode=" + (force ? "force" : "normal");
    }

    private static int ChunkIndex3d(int x, int y, int z)
    {
        return (y * ChunkSize + z) * ChunkSize + x;
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
}
