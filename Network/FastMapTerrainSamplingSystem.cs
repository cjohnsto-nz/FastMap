using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FastMap.Config;
using FastMap.Map;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace FastMap.Network;

public sealed class FastMapTerrainSamplingSystem : ModSystem
{
    private const string ChannelName = "fastmap-terrain-v7";
    private string serverRenderingFingerprint = "";
    private ICoreClientAPI? capi;
    private ICoreServerAPI? sapi;
    private IClientNetworkChannel? clientChannel;
    private IServerNetworkChannel? serverChannel;
    private RemoteTerrainSampler? remote;
    private RemoteTerrainTiles? remoteTiles;
    private TerrainTileService? tileService;
    private long previousTileSamples, previousTileWire;
    private FastMapTerrainSamplerAdapter? local;
    private TerrainSamplingService? service;
    private FastMapServerConfig config = new();
    private long tickId;
    private long lastHello = -5000;
    private long lastClientStats;
    private bool disposed;
    private readonly AdaptiveSamplingBudget budget = new();
    private Process? process;
    private long previousTick, previousCpuTime, statsTime;
    private double previousCpuMilliseconds, processCpuPercent;
    private long previousSamples, previousWire, previousRaw;
    private double previousWork;
    private double previousWorkerWork;
    private long nextPrewarmUpdate;

    internal FastMapTerrainSamplerAdapter? ClientAdapter { get; private set; }
    internal string? ClientRenderingFingerprint { get; private set; }
    public event Action? AvailabilityChanged;

    public override double ExecuteOrder() => 0.9;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        remote = new RemoteTerrainSampler(() => api.ElapsedMilliseconds);
        remoteTiles = new RemoteTerrainTiles(() => api.ElapsedMilliseconds);
        ClientAdapter = FastMapTerrainSamplerAdapter.CreateRemote(remote, remoteTiles);
        clientChannel = api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<TerrainSamplingHello>().RegisterMessageType<TerrainSamplingStatus>()
            .RegisterMessageType<TerrainSamplingRequest>().RegisterMessageType<TerrainSamplingBatch>()
            .RegisterMessageType<TerrainTileRequest>().RegisterMessageType<TerrainTileBatch>()
            .SetMessageHandler<TerrainTileBatch>(batch => remoteTiles.Receive(batch))
            .SetMessageHandler<TerrainSamplingStatus>(OnStatus)
            .SetMessageHandler<TerrainSamplingBatch>(batch => remote.Receive(batch));
        tickId = api.Event.RegisterGameTickListener(ClientTick, 50);
    }

    private void OnStatus(TerrainSamplingStatus status)
    {
        if (disposed || remote == null) return;
        bool available = status.CanUseTiles;
        string? fingerprint = available ? status.RenderingFingerprint : null;
        bool identityChanged = ClientRenderingFingerprint != fingerprint;
        bool changed = remote.Available != available || identityChanged;
        if (identityChanged)
        {
            remote.SetAvailable(false);
            remoteTiles?.SetAvailable(false);
        }
        ClientRenderingFingerprint = fingerprint;
        remote.SetAvailable(available);
        remoteTiles?.SetAvailable(available);
        if (changed)
        {
            capi?.Logger.Notification("[FastMap] Server terrain sampling: {0}", available ? "available" : status.Reason);
            AvailabilityChanged?.Invoke();
        }
    }

    private void ClientTick(float dt)
    {
        if (disposed || capi == null || clientChannel == null || remote == null) return;
        if (!clientChannel.Connected)
        {
            if (remote.Available) { remote.SetAvailable(false); remoteTiles?.SetAvailable(false); AvailabilityChanged?.Invoke(); }
            return;
        }
        if (capi.ElapsedMilliseconds - lastHello >= 5000)
        {
            lastHello = capi.ElapsedMilliseconds;
            clientChannel.SendPacket(new TerrainSamplingHello());
        }
        remote.Pump(request => clientChannel.SendPacket(request));
        remoteTiles?.Pump(request => clientChannel.SendPacket(request));
        if(FastMapModSystem.Instance?.Config.LogStats==true&&capi.ElapsedMilliseconds-lastClientStats>=5000)
        {
            lastClientStats=capi.ElapsedMilliseconds;
            capi.Logger.Notification("[FastMap] client tiles {0}",remoteTiles?.Diagnostics);
        }
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        config = api.LoadModConfig<FastMapServerConfig>("fastmap-server.json") ?? new FastMapServerConfig();
        config.SamplingBudgetMilliseconds = Math.Clamp(config.SamplingBudgetMilliseconds, 1, 10);
        config.MaxSamplesPerTick = Math.Clamp(config.MaxSamplesPerTick, 1, 65536);
        config.AdaptiveMaxBudgetMilliseconds = Math.Clamp(config.AdaptiveMaxBudgetMilliseconds, config.SamplingBudgetMilliseconds, 20);
        config.AdaptiveMaxSamplesPerTick = Math.Clamp(config.AdaptiveMaxSamplesPerTick, 256, 65536);
        config.SampleCacheMegabytes = Math.Clamp(config.SampleCacheMegabytes, 1, 256);
        config.TileCacheMegabytes = Math.Clamp(config.TileCacheMegabytes, 1, 256);
        config.TileDiskCacheMegabytes = Math.Clamp(config.TileDiskCacheMegabytes, 1, 16384);
        config.MaxTransferKilobytesPerTick = Math.Clamp(config.MaxTransferKilobytesPerTick, 32, 1024);
        config.PrewarmRadiusPages = Math.Clamp(config.PrewarmRadiusPages, 0, 16);
        config.PrewarmSampleStep = Math.Clamp(config.PrewarmSampleStep, 1, 32);
        process = Process.GetCurrentProcess();
        previousCpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds;
        previousTick = previousCpuTime = statsTime = Environment.TickCount64;
        api.StoreModConfig(config, "fastmap-server.json");
        serverChannel = api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<TerrainSamplingHello>().RegisterMessageType<TerrainSamplingStatus>()
            .RegisterMessageType<TerrainSamplingRequest>().RegisterMessageType<TerrainSamplingBatch>()
            .RegisterMessageType<TerrainTileRequest>().RegisterMessageType<TerrainTileBatch>()
            .SetMessageHandler<TerrainTileRequest>((player, request) => tileService?.Request(player.PlayerUID, request))
            .SetMessageHandler<TerrainSamplingHello>((player, hello) =>
            {
                bool available = hello.Version == TerrainSamplingHello.ProtocolVersion && IsAllowed(player.PlayerUID);
                serverChannel!.SendPacket(new TerrainSamplingStatus
                {
                    Available = available,
                    RenderingFingerprint = serverRenderingFingerprint,
                    Reason = available ? "" : "Server requires Terrain Sampler 1.3.0+, enabled sampling, and permission."
                }, player);
            })
            .SetMessageHandler<TerrainSamplingRequest>((player, request) => service?.Request(player.PlayerUID, request));
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () =>
        {
            local = FastMapTerrainSamplerAdapter.TryCreate(api);
            service = new TerrainSamplingService(api.WorldManager.MapSizeX, api.WorldManager.MapSizeZ,
                IsAllowed, (x, z) => local!.SampleColumn(x, z), SendBatch,
                config.SampleCacheMegabytes, config.MaxTransferKilobytesPerTick,
                message => { if (config.LogSamplingStats) api.Logger.Notification("[FastMap] " + message); },
                backgroundSampling: false);
            var renderingConfig = api.LoadModConfig<FastMapConfig>("fastmap.json") ?? new FastMapConfig();
            renderingConfig.Normalize();
            var palette = FastMapFallbackPalette.Load(api, renderingConfig);
            int heightOffset = renderingConfig.TerrainSamplerFallbackHeightOffset;
            if (FastMapModDetection.DetectMod(api,"terraprety").Present)
                heightOffset += renderingConfig.TerrainSamplerFallbackTerraPretyHeightOffset;
            var renderer = new FastMapTerrainRenderer(renderingConfig,palette,api.WorldManager.MapSizeY,heightOffset);
            int Color(string code) => ColorUtil.ReverseColorBytes(ColorUtil.Hex2Int(ChunkMapLayer.hexColorsByCode[code]));
            int land=Color("land"), ocean=Color("ocean"), edge=Color("wateredge"), seaLevel=api.World.SeaLevel;
            serverRenderingFingerprint = renderer.RenderingFingerprint(seaLevel, land, ocean, edge,
                FastMapTerrainSamplerAdapter.InstalledTerrainSamplerVersion(api) ?? "unavailable");
            string worldConfiguration = TerrainRenderingIdentity.Hash(writer => WriteCacheAttribute(writer, api.World.Config));
            string modVersions = TerrainRenderingIdentity.Hash(writer =>
            {
                writer.Write(GameVersion.ShortGameVersion);
                foreach (var mod in api.ModLoader.Mods.OrderBy(m => m.Info?.ModID, StringComparer.Ordinal))
                {
                    writer.Write(mod.Info?.ModID ?? ""); writer.Write(mod.Info?.Version ?? "");
                }
            });
            serverRenderingFingerprint = new TerrainTileCacheIdentity(api.World.SavegameIdentifier, api.World.Seed,
                api.WorldManager.MapSizeX, api.WorldManager.MapSizeZ, worldConfiguration, modVersions,
                serverRenderingFingerprint, config.TileCacheRevision).Fingerprint;
            TerrainTileDiskCache? disk = null;
            if (config.ShouldPersist(api.Server.IsDedicated) && local != null)
            {
                string worldKey = TerrainRenderingIdentity.Hash(writer => writer.Write(api.World.SavegameIdentifier));
                disk = new TerrainTileDiskCache(Path.Combine(GamePaths.DataPath, "ModData", "FastMapServer", worldKey),
                    serverRenderingFingerprint, config.TileDiskCacheMegabytes,
                    message => api.Logger.Notification("[FastMap] " + message));
            }
            tileService = new TerrainTileService(api.WorldManager.MapSizeX,api.WorldManager.MapSizeZ,IsAllowed,
                (x,z)=>local!.SampleColumn(x,z),
                (request,grid,style)=>renderer.Render(grid,request.PageX*1024,request.PageZ*1024,request.Step,seaLevel,land,ocean,edge,style),
                (uid,batch)=> {if(api.World.PlayerByUid(uid) is IServerPlayer player)serverChannel!.SendPacket(batch,player);},
                config.TileCacheMegabytes,config.MaxTransferKilobytesPerTick,
                background:config.BackgroundSampling && local?.SupportsBackgroundSampling==true,
                log:message=>{if(config.LogSamplingStats)api.Logger.Notification("[FastMap] "+message);}, diskCache:disk);
            api.Logger.Notification("[FastMap] Server terrain bridge ready; sampler available: {0}; enabled: {1}; background: {2}; serverPrewarm: {3}.", local != null, config.EnableTerrainSampling, tileService.Background, config.EnableServerPrewarm);
            RefreshPrewarmTargets();
        });
        api.Event.ServerRunPhase(EnumServerRunPhase.Shutdown, () => { tileService?.Dispose(); service?.Dispose(); });
        api.Event.PlayerDisconnect += OnPlayerDisconnect;
        tickId = api.Event.RegisterGameTickListener(ServerTick, 50);
    }

    private void ServerTick(float dt)
    {
        if (disposed || service == null || process == null) return;
        long now = Environment.TickCount64;
        long interval = now - previousTick;
        previousTick = now;
        if (now - previousCpuTime >= 1000)
        {
            double cpu = process.TotalProcessorTime.TotalMilliseconds;
            processCpuPercent = Math.Clamp((cpu - previousCpuMilliseconds) * 100
                / ((now - previousCpuTime) * (double)Environment.ProcessorCount), 0, 100);
            previousCpuMilliseconds = cpu;
            previousCpuTime = now;
        }
        double milliseconds = budget.Update(interval, processCpuPercent, config.SamplingBudgetMilliseconds,
            config.AdaptiveMaxBudgetMilliseconds, config.AdaptiveSampling);
        service.UpdateLoad(processCpuPercent, interval);
        tileService?.UpdateLoad(processCpuPercent, interval);
        if (now >= nextPrewarmUpdate)
        {
            RefreshPrewarmTargets();
            nextPrewarmUpdate = now + 2000;
        }
        service.Tick(milliseconds, config.AdaptiveSampling ? config.AdaptiveMaxSamplesPerTick : config.MaxSamplesPerTick);
        tileService?.Tick(milliseconds,config.AdaptiveSampling ? config.AdaptiveMaxSamplesPerTick : config.MaxSamplesPerTick);
        if (now - statsTime >= 10000)
        {
            if(config.LogSamplingStats && tileService!=null && (tileService.Samples!=previousTileSamples || tileService.WireBytes!=previousTileWire || tileService.IsWorking))
                sapi!.Logger.Notification("[FastMap] tile stats processCpuPct={0:0.0} intervalMs={1} samples={2} wireBytes={3} cached={4} poolBytes={5} prewarmed={6} prewarmPending={7} pending={8} cacheHits={9} background={10} diskCached={11} diskBytes={12} diskHits={13}",
                    processCpuPercent,interval,tileService.Samples-previousTileSamples,tileService.WireBytes-previousTileWire,tileService.CachedPages,tileService.PoolBytes,
                    tileService.Prewarmed,tileService.PendingPrewarm,tileService.PendingRequests,tileService.CacheHits,tileService.Background,
                    tileService.DiskCachedPages,tileService.DiskBytes,tileService.DiskHits);
            previousTileSamples=tileService?.Samples??0;previousTileWire=tileService?.WireBytes??0;
            if (config.LogSamplingStats && (service.Samples != previousSamples || service.WireBytes != previousWire || service.PendingRequests > 0))
                sapi!.Logger.Notification(
                    "[FastMap] sampling stats processCpuPct={0:0.0} cores={1} budgetMs={2:0.0} intervalMs={3} samples={4} workMs={5:0} rawBytes={6} wireBytes={7} pending={8} cached={9} poolBytes={10} cacheHits={11} shared={12} workerMs={13:0}",
                    processCpuPercent, Environment.ProcessorCount, milliseconds, interval,
                    service.Samples - previousSamples, service.WorkMilliseconds - previousWork,
                    service.RawBytes - previousRaw, service.WireBytes - previousWire,
                    service.PendingRequests, service.CachedGrids, service.PoolBytes, service.CacheHits, service.SharedRequests,
                    service.WorkerMilliseconds - previousWorkerWork);
            statsTime = now; previousSamples = service.Samples; previousWire = service.WireBytes;
            previousRaw = service.RawBytes; previousWork = service.WorkMilliseconds;
            previousWorkerWork = service.WorkerMilliseconds;
        }
    }

    private bool IsAllowed(string uid)
    {
        return !disposed && local != null && config.EnableTerrainSampling
            && sapi?.World.PlayerByUid(uid) is IServerPlayer player
            && (string.IsNullOrWhiteSpace(config.RequiredPrivilege) || player.HasPrivilege(config.RequiredPrivilege));
    }

    private static void WriteCacheAttribute(BinaryWriter writer, IAttribute attribute)
    {
        writer.Write(attribute.GetAttributeId());
        if (attribute is ITreeAttribute tree)
        {
            writer.Write(tree.Count);
            foreach (var item in tree.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.Write(item.Key);
                WriteCacheAttribute(writer, item.Value);
            }
        }
        else attribute.ToBytes(writer);
    }

    private void RefreshPrewarmTargets()
    {
        if (tileService == null || sapi == null) return;
        if (!config.ShouldPrewarm(sapi.Server.IsDedicated) || local == null)
        {
            tileService.SetPrewarmTargets(Array.Empty<TerrainSamplingRequest>());
            return;
        }
        var centres = new List<(int X, int Z)>();
        foreach (var player in sapi.World.AllOnlinePlayers)
            if (IsAllowed(player.PlayerUID) && player.Entity != null)
                centres.Add(((int)player.Entity.Pos.X, (int)player.Entity.Pos.Z));
        var spawn = sapi.World.DefaultSpawnPosition;
        centres.Add(((int)spawn.X, (int)spawn.Z));
        tileService.SetPrewarmTargets(TerrainPrewarmPlanner.Around(centres, config.PrewarmRadiusPages,
            config.PrewarmSampleStep, sapi.WorldManager.MapSizeX, sapi.WorldManager.MapSizeZ));
    }

    private void SendBatch(string uid, TerrainSamplingBatch batch)
    {
        if (!disposed && sapi?.World.PlayerByUid(uid) is IServerPlayer player) serverChannel?.SendPacket(batch, player);
    }

    private void OnPlayerDisconnect(IServerPlayer player) {service?.Remove(player.PlayerUID);tileService?.Remove(player.PlayerUID);}

    public override void Dispose()
    {
        disposed = true;
        tileService?.Dispose();
        service?.Dispose();
        remote?.Dispose();
        remoteTiles?.Dispose();
        capi?.Event.UnregisterGameTickListener(tickId);
        if (sapi != null)
        {
            sapi.Event.UnregisterGameTickListener(tickId);
            sapi.Event.PlayerDisconnect -= OnPlayerDisconnect;
        }
        process?.Dispose();
        service = null;
        local = null;
        ClientAdapter = null;
        AvailabilityChanged = null;
    }
}
