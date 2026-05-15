using FastMap.Cache;
using FastMap.Config;
using FastMap.Map;
using System;
#if FASTMAPHITCHDIAGNOSTICS
using System.Diagnostics;
#endif
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
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
        Config = FastMapConfig.Load(api);
        FastMapStoragePaths.MigrateLegacyRootIfNeeded(api);
        RegisterConfigReloadListeners(api);
        RegisterClientCommands(api);
#if FASTMAPHITCHDIAGNOSTICS
        RegisterHitchDiagnosticListener(api);
#endif
        FastMapWorldMapGuard.Install(api.Logger);

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
        api.ChatCommands.Create("fastmap")
            .WithDescription("FastMap cache tools")
            .RequiresPlayer()
            .RequiresPrivilege(Privilege.chat)
            .BeginSubCommand("cleanupcache")
                .WithDescription("Delete old FastMap page-version cache folders")
                .WithAdditionalInformation("Uses fastmap.json CleanupKeepLatestPageVersion. When enabled, the newest pages-vN folder in each world cache is preserved.")
                .HandleWith(OnCleanupCacheCommand)
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
        RefreshTerrainSamplerOverlayRegistration<FastMapTerrainFallbackLayer>(worldMapManager, "fastmap-terrain-fallback", 2.0, terrainSamplerAvailable);
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
            enabled: terrainSamplerAvailable,
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
        bool available = versionSupported && FastMapTerrainSamplerAdapter.TryCreate(capi) != null;
        if (available)
        {
            if (!terrainSamplerAvailableLogged)
            {
                capi?.Logger.Notification(
                    "[FastMap] Terrain Sampler integration detected ({0}); Pregen and sampler overlay map layers are available.",
                    installedVersion ?? "unknown version");
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
                worldMapManager.MapLayers[existingIndex] = replacement;
                return;
            }

            if (worldMapManager.MapLayers.Count > 0)
            {
                MapLayer layer = (MapLayer)Activator.CreateInstance(typeof(T), capi!, worldMapManager)!;
                layer.OnLoaded();
                worldMapManager.MapLayers.Add(layer);
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
            worldMapManager.MapLayers.RemoveAt(index);
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
}
