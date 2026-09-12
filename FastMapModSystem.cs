using FastMap.Cache;
using FastMap.Config;
using FastMap.Map;
using FastMap.Network;
using System;
using System.Collections.Generic;
#if FASTMAPHITCHDIAGNOSTICS
using System.Diagnostics;
#endif
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace FastMap;

public sealed class FastMapModSystem : ModSystem
{
    private const string ModId = "fastmap";
    private const string TerrainLayerRegistryCode = "chunks";
    private const string MapperChunkMapLayerFullName = "Mapper.WorldMap.MapperChunkMapLayer";
    private const string ConfigLibConfigSavedEvent = "configlib:fastmap:config-saved";
    private const string ConfigLibConfigReloadEvent = "configlib:config-reload";

    private ICoreClientAPI? capi;
    private Action? levelFinalizeHandler;
    private bool terrainSamplerUnavailableLogged;
    private bool terrainSamplerAvailableLogged;
    private FastMapTerrainSamplingSystem? samplingSystem;
#if FASTMAPHITCHDIAGNOSTICS
    private long hitchDiagnosticListenerId;
    private long lastHitchTickTimestamp;
    private long lastHitchDiagnosticLogMs;
    private int lastHitchGen0Collections;
    private int lastHitchGen1Collections;
    private int lastHitchGen2Collections;
#endif

    public static FastMapModSystem? Instance { get; private set; }

    public FastMapConfig Config { get; private set; } = new();

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override double ExecuteOrder() => 1.0;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        Instance = this;
        samplingSystem = api.ModLoader.GetModSystem<FastMapTerrainSamplingSystem>();
        if (samplingSystem != null) samplingSystem.AvailabilityChanged += OnSamplingAvailabilityChanged;
        Config = FastMapConfig.Load(api);
        FastMapStoragePaths.MigrateLegacyRootIfNeeded(api);
        RegisterConfigReloadListeners(api);
        RegisterClientCommands(api);
#if FASTMAPHITCHDIAGNOSTICS
        RegisterHitchDiagnosticListener(api);
#endif
        FastMapWorldMapGuard.Install(api.Logger);
        FastMapCartographyTableCompatibility.Install(api);

        ReplaceTerrainLayerRegistration();

        levelFinalizeHandler = () => ReplaceTerrainLayerRegistration();
        api.Event.LevelFinalize += levelFinalizeHandler;
    }

#if FASTMAPHITCHDIAGNOSTICS
    private void RegisterHitchDiagnosticListener(ICoreClientAPI api)
    {
        if (!Config.EnableHitchDiagnostics || hitchDiagnosticListenerId != 0)
        {
            return;
        }

        hitchDiagnosticListenerId = api.Event.RegisterGameTickListener(OnHitchDiagnosticTick, 1);
    }
#endif

    private void RegisterConfigReloadListeners(ICoreAPI api)
    {
        api.Event.RegisterEventBusListener(OnConfigLibConfigSaved, filterByEventName: ConfigLibConfigSavedEvent);
        api.Event.RegisterEventBusListener(OnConfigLibConfigReload, filterByEventName: ConfigLibConfigReloadEvent);
    }

    private void RegisterClientCommands(ICoreClientAPI api)
    {
        RegisterVanillaMapCommands(api);

        api.ChatCommands.Create("fastmap")
            .WithDescription("FastMap cache tools")
            .RequiresPlayer()
            .RequiresPrivilege(Privilege.chat)
            .BeginSubCommand("cleanupcache")
                .WithDescription("Delete old FastMap page-version cache folders")
                .WithAdditionalInformation("Uses fastmap.json CleanupKeepLatestPageVersion. When enabled, the newest pages-vN folder in each world cache is preserved.")
                .HandleWith(OnCleanupCacheCommand)
            .EndSubCommand()
            .BeginSubCommand("sealevel")
                .WithDescription("Report FastMap sea-level diagnostics")
                .WithAdditionalInformation("Shows the world sea level FastMap uses for pregen, plus the terrain sampler height at your current position when available.")
                .HandleWith(OnSeaLevelCommand)
            .EndSubCommand();
    }

    private void RegisterVanillaMapCommands(ICoreClientAPI api)
    {
        api.ChatCommands.GetOrCreate("map")
            .BeginSubCommand("purgedb")
                .WithDescription("Purge the vanilla map DB and FastMap terrain page cache")
                .HandleWith(OnMapPurgeDbCommand)
            .EndSubCommand()
            .BeginSubCommand("redraw")
                .WithDescription("Redraw visible FastMap terrain map chunks")
                .HandleWith(OnMapRedrawCommand)
            .EndSubCommand();
    }

    private TextCommandResult OnCleanupCacheCommand(TextCommandCallingArgs args)
    {
        bool keepLatest = Config.CleanupKeepLatestPageVersion;
        FastMapCacheCleanupResult result = FastMapCacheCleanup.CleanupVersionedPageCaches(keepLatest);
        string summary = result.ToSummary(keepLatest);

        capi?.Logger.Notification("[FastMap] {0}", summary);
        foreach (string failure in result.FailureMessages)
        {
            capi?.Logger.Warning("[FastMap] Cache cleanup failed for {0}", failure);
        }

        return result.Failures == 0
            ? TextCommandResult.Success(summary)
            : TextCommandResult.Error(summary);
    }

    private TextCommandResult OnMapPurgeDbCommand(TextCommandCallingArgs args)
    {
        FastPageMapLayer? layer = FindFastMapLayer();
        if (layer == null)
        {
            return TextCommandResult.Error("FastMap terrain layer is not available.");
        }

        string message = layer.PurgeVanillaMapDatabaseAndFastMapCache();
        capi?.Logger.Notification("[FastMap] {0}", message);
        return TextCommandResult.Success(message);
    }

    private TextCommandResult OnMapRedrawCommand(TextCommandCallingArgs args)
    {
        FastPageMapLayer? layer = FindFastMapLayer();
        if (layer == null)
        {
            return TextCommandResult.Error("FastMap terrain layer is not available.");
        }

        string message = layer.RedrawVisibleMapPages();
        capi?.Logger.Notification("[FastMap] {0}", message);
        return TextCommandResult.Success(message);
    }

    private TextCommandResult OnSeaLevelCommand(TextCommandCallingArgs args)
    {
        if (capi == null)
        {
            return TextCommandResult.Error("FastMap client API is not available.");
        }

        int seaLevel = capi.World.SeaLevel;
        int mapHeight = capi.World.BlockAccessor.MapSizeY;
        FastMapModDetectionResult terraPretyDetection = FastMapModDetection.DetectMod(capi, "terraprety");
        bool terraPretyLoaded = terraPretyDetection.Present;
        int terrainSamplerHeightOffset = Config.TerrainSamplerFallbackHeightOffset
            + (terraPretyLoaded ? Config.TerrainSamplerFallbackTerraPretyHeightOffset : 0);
        int waterHeightThreshold = FastMapTerrainWater.WaterHeightThreshold(seaLevel, Config.TerrainSamplerFallbackWaterLevelOffset);
        string message = $"FastMap sea level: {seaLevel}; pregen water cutoff: < {waterHeightThreshold}; world height: {mapHeight}; pregen height offset: {terrainSamplerHeightOffset}"
            + $" (global {Config.TerrainSamplerFallbackHeightOffset}, Terra Prety loaded {terraPretyLoaded} via {terraPretyDetection.Source}, Terra Prety {Config.TerrainSamplerFallbackTerraPretyHeightOffset}; water level offset {Config.TerrainSamplerFallbackWaterLevelOffset}).";
        if (!terraPretyLoaded)
        {
            message += $" Terra Prety detection details: {terraPretyDetection.Details}.";
        }

        EntityPlayer? playerEntity = capi.World.Player?.Entity;
        if (playerEntity == null)
        {
            return TextCommandResult.Success(message);
        }

        int x = (int)Math.Floor(playerEntity.Pos.X);
        int y = (int)Math.Floor(playerEntity.Pos.Y);
        int z = (int)Math.Floor(playerEntity.Pos.Z);
        message += $" Player block position: {x}, {y}, {z}.";

        FastMapTerrainSamplerAdapter? sampler = FastMapTerrainSamplerAdapter.TryCreate(capi);
        if (sampler == null)
        {
            if (!capi.IsSinglePlayer)
            {
                message += " Server terrain sampling is unavailable. The server needs Fast Map, Terrain Sampler 1.3.0+, and permission to share samples.";
                return TextCommandResult.Success(message);
            }
            string? installedVersion = FastMapTerrainSamplerAdapter.InstalledTerrainSamplerVersion(capi);
            message += installedVersion == null
                ? " Terrain Sampler: not installed or not loaded."
                : $" Terrain Sampler: installed version {installedVersion}, requires {FastMapTerrainSamplerAdapter.MinimumSupportedVersion}+.";
            return TextCommandResult.Success(message);
        }

        try
        {
            FastMapTerrainSamplerColumn sample = sampler.SampleColumn(x, z);
            int adjustedHeight = Math.Clamp(sample.Height + terrainSamplerHeightOffset, 0, Math.Max(0, mapHeight - 1));
            int delta = adjustedHeight - seaLevel;
            string deltaText = delta > 0 ? $"+{delta}" : delta.ToString();
            message += $" Terrain Sampler height here: raw {sample.Height}, pregen {adjustedHeight} ({deltaText} vs sea level).";
        }
        catch (TerrainSamplesPendingException)
        {
            message += " Terrain Sampler: requesting server data; run this command again shortly.";
        }
        catch (Exception ex)
        {
            message += $" Terrain Sampler: sample failed ({ex.GetType().Name}: {ex.Message}).";
        }

        return TextCommandResult.Success(message);
    }

    public FastMapMapPieceImportResult ImportMapPieces(IReadOnlyDictionary<FastVec2i, MapPieceDB> pieces, string source = "external")
    {
        FastPageMapLayer? layer = FindFastMapLayer();
        return layer == null
            ? FastMapMapPieceImportResult.NotHandled
            : layer.ImportMapPieces(pieces, source);
    }

    public bool TryGetMapPieces(IEnumerable<FastVec2i> coords, out Dictionary<FastVec2i, MapPieceDB> pieces)
    {
        pieces = new Dictionary<FastVec2i, MapPieceDB>();
        FastPageMapLayer? layer = FindFastMapLayer();
        return layer != null && layer.TryGetMapPieces(coords, out pieces);
    }

    public bool InvalidateMapPieces(IEnumerable<FastVec2i> coords, string source = "external")
    {
        FastPageMapLayer? layer = FindFastMapLayer();
        return layer != null && layer.InvalidateMapPieces(coords, source);
    }

    private void OnConfigLibConfigSaved(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (!IsOwnConfigEvent(data))
        {
            return;
        }

        ReloadConfigSafely();
    }

    private void OnConfigLibConfigReload(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (!IsOwnConfigEvent(data))
        {
            return;
        }

        ReloadConfigSafely();
    }

    private static bool IsOwnConfigEvent(IAttribute data)
    {
        return (data as ITreeAttribute)?.GetAsString("domain") == ModId;
    }

    private void ReloadConfigSafely()
    {
        if (capi == null)
        {
            return;
        }

        Config = FastMapConfig.Load(capi);
#if FASTMAPHITCHDIAGNOSTICS
        if (Config.EnableHitchDiagnostics)
        {
            RegisterHitchDiagnosticListener(capi);
        }
        else if (hitchDiagnosticListenerId != 0)
        {
            capi.Event.UnregisterGameTickListener(hitchDiagnosticListenerId);
            hitchDiagnosticListenerId = 0;
        }
#endif

        RefreshWorldMapRegistrationsAfterConfigReload();
        capi.Logger.Notification("[FastMap] Reloaded config. Live map layer instances were left intact; constructor-only settings apply after the next world load.");
    }

    private void RefreshWorldMapRegistrationsAfterConfigReload()
    {
        if (capi == null)
        {
            return;
        }

        WorldMapManager? worldMapManager = capi.ModLoader.GetModSystem<WorldMapManager>(true);
        if (worldMapManager == null)
        {
            return;
        }

        worldMapManager.MapLayerRegistry[TerrainLayerRegistryCode] = typeof(FastPageMapLayer);
        worldMapManager.LayerGroupPositions[TerrainLayerRegistryCode] = 0.0;

        bool terrainSamplerAvailable = IsTerrainSamplerIntegrationAvailable();
        RefreshTerrainSamplerOverlayRegistration<FastMapTerrainFallbackLayer>(
            worldMapManager,
            "fastmap-terrain-fallback",
            2.0,
            terrainSamplerAvailable && !Config.DisableTerrainSamplerFallbackLayer);
        RefreshTerrainSamplerOverlayRegistration<FastMapRainfallLayer>(worldMapManager, "fastmap-rainfall", 0.15, terrainSamplerAvailable && Config.EnableTerrainSamplerRainfallLayer);
        RefreshTerrainSamplerOverlayRegistration<FastMapTemperatureLayer>(worldMapManager, "fastmap-temperature", 0.16, terrainSamplerAvailable && Config.EnableTerrainSamplerTemperatureLayer);
        RefreshTerrainSamplerOverlayRegistration<FastMapForestDensityLayer>(worldMapManager, "fastmap-forest-density", 0.17, terrainSamplerAvailable && Config.EnableTerrainSamplerForestDensityLayer);
        RefreshTerrainSamplerOverlayRegistration<FastMapShrubDensityLayer>(worldMapManager, "fastmap-shrub-density", 0.18, terrainSamplerAvailable && Config.EnableTerrainSamplerShrubDensityLayer);

        if (worldMapManager.MapLayers.Count == 0)
        {
            capi.Logger.Warning("[FastMap] World map layer list was empty after config reload; rebuilding layer registrations to avoid an empty world map dialog.");
            ReplaceTerrainLayerRegistration(recreateFastMapLayer: false);
        }
    }

    private static void RefreshTerrainSamplerOverlayRegistration<T>(
        WorldMapManager worldMapManager,
        string code,
        double position,
        bool enabled) where T : MapLayer
    {
        if (enabled)
        {
            worldMapManager.MapLayerRegistry[code] = typeof(T);
            worldMapManager.LayerGroupPositions[code] = position;
        }
        else if (FindLayerIndex<T>(worldMapManager) < 0)
        {
            worldMapManager.MapLayerRegistry.Remove(code);
            worldMapManager.LayerGroupPositions.Remove(code);
        }
    }

#if FASTMAPHITCHDIAGNOSTICS
    private void OnHitchDiagnosticTick(float dt)
    {
        if (capi == null || !Config.EnableHitchDiagnostics)
        {
            return;
        }

        MarkFrameProfiler("fastmap-app-hitch-check-begin");
        long dtMs = (long)Math.Round(dt * 1000f);
        long now = Stopwatch.GetTimestamp();
        long previous = lastHitchTickTimestamp;
        lastHitchTickTimestamp = now;

        long wallMs = previous == 0 ? 0 : (long)((now - previous) * 1000.0 / Stopwatch.Frequency);
        long elapsedMs = Math.Max(dtMs, wallMs);
        if (elapsedMs < Config.HitchDiagnosticThresholdMilliseconds)
        {
            MarkFrameProfiler("fastmap-app-hitch-check-end");
            return;
        }

        long nowMs = capi.ElapsedMilliseconds;
        if (nowMs - lastHitchDiagnosticLogMs < 1000)
        {
            MarkFrameProfiler("fastmap-app-hitch-check-end");
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

        capi.Logger.Warning(
            "[FastMap] App hitch diagnostic elapsedMs={0}, dtMs={1}, wallMs={2}, gc0Delta={3}, gc1Delta={4}, gc2Delta={5}, managedMemoryMb={6}",
            elapsedMs,
            dtMs,
            wallMs,
            gen0Delta,
            gen1Delta,
            gen2Delta,
            GC.GetTotalMemory(false) / (1024 * 1024));
        MarkFrameProfiler("fastmap-app-hitch-check-end");
    }

    private void MarkFrameProfiler(string code)
    {
        if (capi == null || !Config.EnableHitchDiagnostics || !capi.World.FrameProfiler.Enabled)
        {
            return;
        }

        capi.World.FrameProfiler.Mark(code);
    }
#endif

    private void ReplaceTerrainLayerRegistration(bool recreateFastMapLayer = false)
    {
        if (capi == null)
        {
            return;
        }

        WorldMapManager? worldMapManager = capi.ModLoader.GetModSystem<WorldMapManager>(true);
        if (worldMapManager == null)
        {
            capi.Logger.Warning("[FastMap] WorldMapManager was not available; terrain layer replacement skipped.");
            return;
        }

        if (TryInstallMapperRenderLayer(worldMapManager, recreateFastMapLayer))
        {
            SyncTerrainSamplerOverlayLayers(worldMapManager, recreateFastMapLayer);
            return;
        }

        worldMapManager.MapLayerRegistry[TerrainLayerRegistryCode] = typeof(FastPageMapLayer);
        worldMapManager.LayerGroupPositions[TerrainLayerRegistryCode] = 0.0;

        for (int i = 0; i < worldMapManager.MapLayers.Count; i++)
        {
            MapLayer layer = worldMapManager.MapLayers[i];
            if (layer.GetType() == typeof(ChunkMapLayer) || (recreateFastMapLayer && layer is FastPageMapLayer))
            {
                layer.OnShutDown();
                layer.Dispose();

                FastPageMapLayer replacement = new(capi, worldMapManager);
                replacement.OnLoaded();
                worldMapManager.MapLayers[i] = replacement;
            }
        }

        SyncTerrainSamplerOverlayLayers(worldMapManager, recreateFastMapLayer);
    }

    private void SyncTerrainSamplerOverlayLayers(WorldMapManager worldMapManager, bool recreateExisting)
    {
        if (capi == null)
        {
            return;
        }

        bool terrainSamplerAvailable = IsTerrainSamplerIntegrationAvailable();
        SyncTerrainSamplerOverlayLayer<FastMapTerrainFallbackLayer>(
            worldMapManager,
            "fastmap-terrain-fallback",
            2.0,
            enabled: terrainSamplerAvailable && !Config.DisableTerrainSamplerFallbackLayer,
            recreateExisting);
        SyncTerrainSamplerOverlayLayer<FastMapRainfallLayer>(
            worldMapManager,
            "fastmap-rainfall",
            0.15,
            terrainSamplerAvailable && Config.EnableTerrainSamplerRainfallLayer,
            recreateExisting);
        SyncTerrainSamplerOverlayLayer<FastMapTemperatureLayer>(
            worldMapManager,
            "fastmap-temperature",
            0.16,
            terrainSamplerAvailable && Config.EnableTerrainSamplerTemperatureLayer,
            recreateExisting);
        SyncTerrainSamplerOverlayLayer<FastMapForestDensityLayer>(
            worldMapManager,
            "fastmap-forest-density",
            0.17,
            terrainSamplerAvailable && Config.EnableTerrainSamplerForestDensityLayer,
            recreateExisting);
        SyncTerrainSamplerOverlayLayer<FastMapShrubDensityLayer>(
            worldMapManager,
            "fastmap-shrub-density",
            0.18,
            terrainSamplerAvailable && Config.EnableTerrainSamplerShrubDensityLayer,
            recreateExisting);
    }

    private bool IsTerrainSamplerIntegrationAvailable()
    {
        string? installedVersion = capi != null ? FastMapTerrainSamplerAdapter.InstalledTerrainSamplerVersion(capi) : null;
        bool versionSupported = capi == null || FastMapTerrainSamplerAdapter.IsInstalledVersionSupported(capi);
        bool available = FastMapTerrainSamplerAdapter.TryCreate(capi) != null;
        if (available)
        {
            if (!terrainSamplerAvailableLogged)
            {
                string pregenStatus = Config.DisableTerrainSamplerFallbackLayer
                    ? "Pregen map layer is disabled by config"
                    : "Pregen map layer is available";
                capi?.Logger.Notification(
                    "[FastMap] Terrain Sampler integration detected ({0}); {1}; sampler overlay map layers are available.",
                    capi?.IsSinglePlayer == false ? "server" : installedVersion ?? "unknown version",
                    pregenStatus);
                terrainSamplerAvailableLogged = true;
            }

            return true;
        }

        if (!terrainSamplerUnavailableLogged)
        {
            if (installedVersion == null)
            {
                capi?.Logger.Notification("[FastMap] Terrain Sampler integration not detected; Pregen and sampler overlay map layers are hidden.");
            }
            else if (!versionSupported)
            {
                capi?.Logger.Notification(
                    "[FastMap] Terrain Sampler {0} detected, but FastMap requires {1}+; Pregen and sampler overlay map layers are hidden.",
                    installedVersion,
                    FastMapTerrainSamplerAdapter.MinimumSupportedVersion);
            }
            else
            {
                capi?.Logger.Notification("[FastMap] Terrain Sampler integration was detected but not ready; Pregen and sampler overlay map layers are hidden.");
            }

            terrainSamplerUnavailableLogged = true;
        }

        return false;
    }

    private void SyncTerrainSamplerOverlayLayer<T>(
        WorldMapManager worldMapManager,
        string code,
        double position,
        bool enabled,
        bool recreateExisting) where T : MapLayer
    {
        if (enabled)
        {
            worldMapManager.MapLayerRegistry[code] = typeof(T);
            worldMapManager.LayerGroupPositions[code] = position;
            int existingIndex = FindLayerIndex<T>(worldMapManager);

            if (existingIndex >= 0)
            {
                if (!recreateExisting)
                {
                    return;
                }

                MapLayer oldLayer = worldMapManager.MapLayers[existingIndex];
                oldLayer.OnShutDown();
                oldLayer.Dispose();
                MapLayer replacement = (MapLayer)Activator.CreateInstance(typeof(T), capi!, worldMapManager)!;
                replacement.OnLoaded();
                var replacementLayers = new List<MapLayer>(worldMapManager.MapLayers);
                replacementLayers[existingIndex] = replacement;
                worldMapManager.MapLayers = replacementLayers;
                return;
            }

            if (worldMapManager.MapLayers.Count > 0)
            {
                MapLayer layer = (MapLayer)Activator.CreateInstance(typeof(T), capi!, worldMapManager)!;
                layer.OnLoaded();
                worldMapManager.MapLayers = new List<MapLayer>(worldMapManager.MapLayers) { layer };
            }

            return;
        }

        worldMapManager.MapLayerRegistry.Remove(code);
        worldMapManager.LayerGroupPositions.Remove(code);

        int index = FindLayerIndex<T>(worldMapManager);
        if (index >= 0)
        {
            MapLayer oldLayer = worldMapManager.MapLayers[index];
            oldLayer.OnShutDown();
            oldLayer.Dispose();
            var remainingLayers = new List<MapLayer>(worldMapManager.MapLayers);
            remainingLayers.RemoveAt(index);
            worldMapManager.MapLayers = remainingLayers;
        }
    }

    private bool TryInstallMapperRenderLayer(WorldMapManager worldMapManager, bool recreateFastMapLayer)
    {
        if (!worldMapManager.MapLayerRegistry.TryGetValue(TerrainLayerRegistryCode, out Type? terrainLayerType)
            || terrainLayerType.FullName != MapperChunkMapLayerFullName)
        {
            return false;
        }

        int fastMapIndex = FindFastMapLayerIndex(worldMapManager);
        if (fastMapIndex >= 0)
        {
            if (!recreateFastMapLayer)
            {
                return true;
            }

            MapLayer oldLayer = worldMapManager.MapLayers[fastMapIndex];
            oldLayer.OnShutDown();
            oldLayer.Dispose();
            FastPageMapLayer replacement = new(capi!, worldMapManager);
            replacement.OnLoaded();
            worldMapManager.MapLayers[fastMapIndex] = replacement;
            return true;
        }

        if (worldMapManager.MapLayers.Count == 0)
        {
            return true;
        }

        int mapperIndex = FindLayerIndexByFullName(worldMapManager, MapperChunkMapLayerFullName);
        int insertIndex = mapperIndex >= 0 ? mapperIndex + 1 : worldMapManager.MapLayers.Count;
        FastPageMapLayer fastMapLayer = new(capi!, worldMapManager);
        fastMapLayer.OnLoaded();
        worldMapManager.MapLayers.Insert(insertIndex, fastMapLayer);
        capi!.Logger.Notification("[FastMap] Mapper chunk layer detected; FastMap installed as terrain render layer while Mapper keeps map state.");
        return true;
    }

    private static int FindFastMapLayerIndex(WorldMapManager worldMapManager)
    {
        for (int i = 0; i < worldMapManager.MapLayers.Count; i++)
        {
            if (worldMapManager.MapLayers[i] is FastPageMapLayer)
            {
                return i;
            }
        }

        return -1;
    }

    private FastPageMapLayer? FindFastMapLayer()
    {
        if (capi == null)
        {
            return null;
        }

        WorldMapManager? worldMapManager = capi.ModLoader.GetModSystem<WorldMapManager>(true);
        if (worldMapManager == null)
        {
            return null;
        }

        for (int i = 0; i < worldMapManager.MapLayers.Count; i++)
        {
            if (worldMapManager.MapLayers[i] is FastPageMapLayer layer)
            {
                return layer;
            }
        }

        return null;
    }

    private static int FindLayerIndexByFullName(WorldMapManager worldMapManager, string fullName)
    {
        for (int i = 0; i < worldMapManager.MapLayers.Count; i++)
        {
            if (worldMapManager.MapLayers[i].GetType().FullName == fullName)
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindLayerIndex<T>(WorldMapManager worldMapManager) where T : MapLayer
    {
        for (int i = 0; i < worldMapManager.MapLayers.Count; i++)
        {
            if (worldMapManager.MapLayers[i] is T)
            {
                return i;
            }
        }

        return -1;
    }

    public override void Dispose()
    {
        if (samplingSystem != null) samplingSystem.AvailabilityChanged -= OnSamplingAvailabilityChanged;
        if (capi != null && levelFinalizeHandler != null)
        {
            capi.Event.LevelFinalize -= levelFinalizeHandler;
        }

#if FASTMAPHITCHDIAGNOSTICS
        if (capi != null && hitchDiagnosticListenerId != 0)
        {
            capi.Event.UnregisterGameTickListener(hitchDiagnosticListenerId);
        }

        hitchDiagnosticListenerId = 0;
#endif
        levelFinalizeHandler = null;
        capi = null;
        if (Instance == this)
        {
            Instance = null;
        }
    }

    private void OnSamplingAvailabilityChanged()
    {
        if (capi == null || capi.IsSinglePlayer) return;
        WorldMapManager? manager = capi.ModLoader.GetModSystem<WorldMapManager>(true);
        GuiDialogWorldMap? dialog = manager?.worldMapDlg;
        bool reopen = dialog?.IsOpened() == true;
        EnumDialogType dialogType = dialog?.DialogType ?? EnumDialogType.HUD;
        // Vanilla captures both the layer list and tab codes when creating this dialog.
        // Recreate it after a late handshake. A new rendering identity also needs
        // new immutable fallback caches, so stale in-flight work stays in the old layer.
        bool renderingChanged = false;
        if (manager != null)
            foreach (var layer in manager.MapLayers)
                if (layer is FastPageMapLayer pages && pages.ServerRenderingFingerprint != samplingSystem?.ClientRenderingFingerprint)
                    renderingChanged = true;
        if (dialog != null)
        {
            dialog.TryClose();
            dialog.Dispose();
            manager!.worldMapDlg = null!;
        }
        terrainSamplerAvailableLogged = false;
        terrainSamplerUnavailableLogged = false;
        ReplaceTerrainLayerRegistration(recreateFastMapLayer: renderingChanged);
        if (reopen) manager?.ToggleMap(dialogType);
    }
}
