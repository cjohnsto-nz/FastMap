using FastMap.Cache;
using FastMap.Config;
using FastMap.Map;
using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace FastMap;

public sealed class FastMapModSystem : ModSystem
{
    private const string ModId = "fastmap";
    private const string ConfigLibConfigSavedEvent = "configlib:fastmap:config-saved";
    private const string ConfigLibConfigReloadEvent = "configlib:config-reload";

    private ICoreClientAPI? capi;
    private Action? levelFinalizeHandler;

    public static FastMapModSystem? Instance { get; private set; }

    public FastMapConfig Config { get; private set; } = new();

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override double ExecuteOrder() => 1.0;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        Instance = this;
        Config = FastMapConfig.Load(api);
        RegisterConfigReloadListeners(api);
        RegisterClientCommands(api);
        FastMapWorldMapGuard.Install(api.Logger);

        ReplaceTerrainLayerRegistration();

        levelFinalizeHandler = () => ReplaceTerrainLayerRegistration();
        api.Event.LevelFinalize += levelFinalizeHandler;
    }

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

        ReloadConfigAndRecreateTerrainLayer();
    }

    private void OnConfigLibConfigReload(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (!IsOwnConfigEvent(data))
        {
            return;
        }

        ReloadConfigAndRecreateTerrainLayer();
    }

    private static bool IsOwnConfigEvent(IAttribute data)
    {
        return (data as ITreeAttribute)?.GetAsString("domain") == ModId;
    }

    private void ReloadConfigAndRecreateTerrainLayer()
    {
        if (capi == null)
        {
            return;
        }

        Config = FastMapConfig.Load(capi);
        ReplaceTerrainLayerRegistration(recreateFastMapLayer: true);
    }

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

        worldMapManager.MapLayerRegistry["chunks"] = typeof(FastPageMapLayer);
        worldMapManager.LayerGroupPositions["chunks"] = 0.0;

        for (int i = 0; i < worldMapManager.MapLayers.Count; i++)
        {
            MapLayer layer = worldMapManager.MapLayers[i];
            if (layer is ChunkMapLayer || (recreateFastMapLayer && layer is FastPageMapLayer))
            {
                layer.OnShutDown();
                layer.Dispose();

                FastPageMapLayer replacement = new(capi, worldMapManager);
                replacement.OnLoaded();
                worldMapManager.MapLayers[i] = replacement;
            }
        }
    }

    public override void Dispose()
    {
        if (capi != null && levelFinalizeHandler != null)
        {
            capi.Event.LevelFinalize -= levelFinalizeHandler;
        }

        levelFinalizeHandler = null;
        capi = null;
        if (Instance == this)
        {
            Instance = null;
        }
    }
}
