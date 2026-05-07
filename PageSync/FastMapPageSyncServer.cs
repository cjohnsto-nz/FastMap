using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FastMap.Config;
using FastMap.Map;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace FastMap.PageSync;

public sealed class FastMapPageSyncServer
{
    private const string SaveKey = "fastmap:pagesync";
    private readonly ICoreServerAPI sapi;
    private readonly FastMapConfig config;
    private readonly IServerNetworkChannel channel;
    private readonly Dictionary<string, bool> shareEnabledByPlayerUid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> selectedSourcesByPlayerUid = new(StringComparer.Ordinal);
    private readonly object storageLock = new();
    private bool saveLoaded;

    public FastMapPageSyncServer(ICoreServerAPI sapi, FastMapConfig config)
    {
        this.sapi = sapi;
        this.config = config;
        channel = sapi.Network.GetChannel(FastMapPageSyncNetwork.ChannelName);
    }

    public void Start()
    {
        channel.SetMessageHandler<FastMapPageSyncUploadPacket>(OnPageUpload);
        channel.SetMessageHandler<FastMapPageSyncRequestPacket>(OnPageRequest);
        sapi.Event.SaveGameLoaded += OnSaveGameLoaded;
        sapi.Event.GameWorldSave += Save;
        sapi.Event.PlayerJoin += OnPlayerJoin;
    }

    public void Dispose()
    {
        Save();
        sapi.Event.SaveGameLoaded -= OnSaveGameLoaded;
        sapi.Event.GameWorldSave -= Save;
        sapi.Event.PlayerJoin -= OnPlayerJoin;
    }

    public void SetSharingEnabled(IServerPlayer player, bool enabled)
    {
        shareEnabledByPlayerUid[player.PlayerUID] = enabled;
        Save();
    }

    public string GetSourcesSummary(IServerPlayer player)
    {
        IServerPlayer[] sources = GetOnlineSources(player);
        HashSet<string> selectedSources = GetSelectedSources(player.PlayerUID);
        if (sources.Length == 0 && selectedSources.Count == 0)
        {
            return "No online FastMap page-sync sources are available.";
        }

        List<string> parts = new();
        foreach (IServerPlayer source in sources)
        {
            bool sharing = shareEnabledByPlayerUid.TryGetValue(source.PlayerUID, out bool enabled) && enabled;
            bool selected = selectedSources.Contains(source.PlayerUID);
            parts.Add($"{source.PlayerName} {(sharing ? "sharing" : "not sharing")}{(selected ? " selected" : "")}");
        }

        foreach (string selectedUid in selectedSources)
        {
            if (sources.All(source => source.PlayerUID != selectedUid))
            {
                parts.Add($"{selectedUid} offline selected");
            }
        }

        return string.Join(", ", parts);
    }

    public TextCommandResult SetSourceSelected(IServerPlayer player, string playerName, bool selected)
    {
        IServerPlayer? source = GetOnlineSources(player)
            .FirstOrDefault(candidate => string.Equals(candidate.PlayerName, playerName, StringComparison.OrdinalIgnoreCase));
        if (source == null)
        {
            return TextCommandResult.Error($"No online FastMap page-sync source named '{playerName}' was found.");
        }

        HashSet<string> selectedSources = GetSelectedSources(player.PlayerUID);
        if (selected)
        {
            selectedSources.Add(source.PlayerUID);
        }
        else
        {
            selectedSources.Remove(source.PlayerUID);
        }

        Save();
        channel.SendPacket(new FastMapPageSyncResetPacket(), player);
        return TextCommandResult.Success(selected
            ? $"Subscribed to {source.PlayerName}'s shared FastMap pages."
            : $"Unsubscribed from {source.PlayerName}'s shared FastMap pages.");
    }

    private void OnPageUpload(IServerPlayer fromPlayer, FastMapPageSyncUploadPacket packet)
    {
        if (!config.EnablePageSync || !FastMapPagePayloadCodec.TryDecode(packet.Payload, out FastMapPageSnapshot snapshot))
        {
            return;
        }

        StorePagePayload(fromPlayer.PlayerUID, snapshot.PageKey, packet.Payload);
    }

    private void OnPageRequest(IServerPlayer fromPlayer, FastMapPageSyncRequestPacket packet)
    {
        if (!config.EnablePageSync || packet.Pages.Length == 0)
        {
            return;
        }

        HashSet<string> selectedSources = GetSelectedSources(fromPlayer.PlayerUID);
        if (selectedSources.Count == 0)
        {
            return;
        }

        int sent = 0;
        foreach (FastMapPageSyncKey key in packet.Pages.Take(config.PageSyncMaxPagesPerRequest))
        {
            if (sent >= config.PageSyncMaxDownloadsPerRequest)
            {
                return;
            }

            FastVec2i pageKey = new(key.X, key.Y);
            FastMapPageSnapshot? union = null;

            foreach (string sourceUid in selectedSources)
            {
                if (!shareEnabledByPlayerUid.TryGetValue(sourceUid, out bool enabled) || !enabled)
                {
                    continue;
                }

                if (!TryLoadPageSnapshot(sourceUid, pageKey, out FastMapPageSnapshot sourceSnapshot))
                {
                    continue;
                }

                union = union == null ? sourceSnapshot : union.Merge(sourceSnapshot);
            }

            if (union == null)
            {
                continue;
            }

            byte[] payload = FastMapPagePayloadCodec.Encode(union, useHighCompression: false);
            if (payload.Length == 0)
            {
                continue;
            }

            channel.SendPacket(new FastMapPageSyncPagePacket
            {
                Payload = payload
            }, fromPlayer);
            sent++;
        }
    }

    private void OnSaveGameLoaded()
    {
        Load();
        saveLoaded = true;
    }

    private void OnPlayerJoin(IServerPlayer player)
    {
        shareEnabledByPlayerUid.TryAdd(player.PlayerUID, false);
        GetSelectedSources(player.PlayerUID);
    }

    private void Load()
    {
        FastMapPageSyncSave save = sapi.WorldManager.SaveGame.GetData(SaveKey, new FastMapPageSyncSave());

        shareEnabledByPlayerUid.Clear();
        foreach (KeyValuePair<string, bool> entry in save.ShareEnabledByPlayerUid)
        {
            shareEnabledByPlayerUid[entry.Key] = entry.Value;
        }

        selectedSourcesByPlayerUid.Clear();
        foreach (KeyValuePair<string, string[]> entry in save.SelectedSourcesByPlayerUid)
        {
            selectedSourcesByPlayerUid[entry.Key] = new HashSet<string>(entry.Value ?? [], StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        if (!saveLoaded)
        {
            return;
        }

        FastMapPageSyncSave save = new()
        {
            ShareEnabledByPlayerUid = new Dictionary<string, bool>(shareEnabledByPlayerUid, StringComparer.Ordinal),
            SelectedSourcesByPlayerUid = selectedSourcesByPlayerUid.ToDictionary(
                entry => entry.Key,
                entry => entry.Value.ToArray(),
                StringComparer.Ordinal
            )
        };

        sapi.WorldManager.SaveGame.StoreData(SaveKey, save);
    }

    private IServerPlayer[] GetOnlineSources(IServerPlayer player)
    {
        return sapi.World.AllOnlinePlayers
            .OfType<IServerPlayer>()
            .Where(source => source.PlayerUID != player.PlayerUID)
            .OrderBy(source => source.PlayerName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private HashSet<string> GetSelectedSources(string playerUid)
    {
        if (!selectedSourcesByPlayerUid.TryGetValue(playerUid, out HashSet<string>? selectedSources))
        {
            selectedSources = new HashSet<string>(StringComparer.Ordinal);
            selectedSourcesByPlayerUid[playerUid] = selectedSources;
        }

        return selectedSources;
    }

    private void StorePagePayload(string playerUid, FastVec2i pageKey, byte[] payload)
    {
        lock (storageLock)
        {
            string path = GetPagePath(playerUid, pageKey);
            GamePaths.EnsurePathExists(Path.GetDirectoryName(path)!);
            string tmpPath = path + ".tmp";
            File.WriteAllBytes(tmpPath, payload);
            File.Move(tmpPath, path, overwrite: true);
        }
    }

    private bool TryLoadPageSnapshot(string playerUid, FastVec2i pageKey, out FastMapPageSnapshot snapshot)
    {
        snapshot = null!;
        string path = GetPagePath(playerUid, pageKey);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            byte[] payload;
            lock (storageLock)
            {
                payload = File.ReadAllBytes(path);
            }

            return FastMapPagePayloadCodec.TryDecode(payload, out snapshot);
        }
        catch (Exception ex)
        {
            sapi.Logger.Warning("[FastMap] Failed reading synced page {0}/{1} for {2}: {3}", pageKey.X, pageKey.Y, playerUid, ex.Message);
            return false;
        }
    }

    private string GetPagePath(string playerUid, FastVec2i pageKey)
    {
        string root = Path.Combine(GamePaths.DataPath, "FastMapSync", SanitizePathPart(sapi.World.SavegameIdentifier), SanitizePathPart(playerUid));
        return Path.Combine(root, pageKey.X + "_" + pageKey.Y + ".fmps");
    }

    private static string SanitizePathPart(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        char[] chars = value.ToCharArray();

        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }
}
