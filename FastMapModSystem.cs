using FastMap.Cache;
using FastMap.Config;
using FastMap.Map;
using FastMap.PageSync;
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
    private ICoreServerAPI? sapi;
    private Action? levelFinalizeHandler;

    public static FastMapModSystem? Instance { get; private set; }

    public FastMapConfig Config { get; private set; } = new();

    internal FastMapPageSyncClient? PageSyncClient { get; private set; }

    public FastMapPageSyncServer? PageSyncServer { get; private set; }

    public override bool ShouldLoad(EnumAppSide forSide) => true;

    public override double ExecuteOrder() => 1.0;

    public override void Start(ICoreAPI api)
    {
        Instance = this;
        Config = FastMapConfig.Load(api);
        RegisterPageSyncMessages(api);
        RegisterConfigReloadListeners(api);
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        Instance = this;
        PageSyncClient = new FastMapPageSyncClient(api, Config);
        RegisterClientCommands(api);

        ReplaceTerrainLayerRegistration();

        levelFinalizeHandler = () => ReplaceTerrainLayerRegistration();
        api.Event.LevelFinalize += levelFinalizeHandler;
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        Instance = this;
        PageSyncServer = new FastMapPageSyncServer(api, Config);
        PageSyncServer.Start();
        RegisterServerCommands(api);
    }

    private static void RegisterPageSyncMessages(ICoreAPI api)
    {
        api.Network.RegisterChannel(FastMapPageSyncNetwork.ChannelName)
            .RegisterMessageType<FastMapPageSyncRequestPacket>()
            .RegisterMessageType<FastMapPageSyncUploadPacket>()
            .RegisterMessageType<FastMapPageSyncPagePacket>()
            .RegisterMessageType<FastMapPageSyncResetPacket>();
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

    private void RegisterServerCommands(ICoreServerAPI api)
    {
        api.ChatCommands.GetOrCreate("fastmap")
            .RequiresPlayer()
            .RequiresPrivilege(Privilege.chat)
            .BeginSubCommand("share")
                .WithDescription("Toggle sharing uploaded FastMap pages with subscribed players")
                .WithArgs(api.ChatCommands.Parsers.Bool("enabled"))
                .HandleWith(OnServerShareCommand)
            .EndSubCommand()
            .BeginSubCommand("sources")
                .WithDescription("List FastMap page-sync sources")
                .HandleWith(OnServerSourcesCommand)
            .EndSubCommand()
            .BeginSubCommand("subscribe")
                .WithDescription("Subscribe to an online player's shared FastMap pages")
                .WithArgs(api.ChatCommands.Parsers.Word("playerName"))
                .HandleWith(OnServerSubscribeCommand)
            .EndSubCommand()
            .BeginSubCommand("unsubscribe")
                .WithDescription("Unsubscribe from an online player's shared FastMap pages")
                .WithArgs(api.ChatCommands.Parsers.Word("playerName"))
                .HandleWith(OnServerUnsubscribeCommand)
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

    private TextCommandResult OnServerShareCommand(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer player || PageSyncServer == null)
        {
            return TextCommandResult.Error("FastMap page sync is not ready.");
        }

        bool enabled = (bool)args[0];
        PageSyncServer.SetSharingEnabled(player, enabled);
        return TextCommandResult.Success(enabled
            ? "FastMap page sharing enabled."
            : "FastMap page sharing disabled.");
    }

    private TextCommandResult OnServerSourcesCommand(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer player || PageSyncServer == null)
        {
            return TextCommandResult.Error("FastMap page sync is not ready.");
        }

        string summary = PageSyncServer.GetSourcesSummary(player);
        return TextCommandResult.Success(summary);
    }

    private TextCommandResult OnServerSubscribeCommand(TextCommandCallingArgs args)
    {
        return SetServerSourceSelection(args, selected: true);
    }

    private TextCommandResult OnServerUnsubscribeCommand(TextCommandCallingArgs args)
    {
        return SetServerSourceSelection(args, selected: false);
    }

    private TextCommandResult SetServerSourceSelection(TextCommandCallingArgs args, bool selected)
    {
        if (args.Caller.Player is not IServerPlayer player || PageSyncServer == null)
        {
            return TextCommandResult.Error("FastMap page sync is not ready.");
        }

        string playerName = (string)args[0];
        return PageSyncServer.SetSourceSelected(player, playerName, selected);
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
        PageSyncServer?.Dispose();
        PageSyncServer = null;
        PageSyncClient = null;
        sapi = null;
        capi = null;
        if (Instance == this)
        {
            Instance = null;
        }
    }
}
