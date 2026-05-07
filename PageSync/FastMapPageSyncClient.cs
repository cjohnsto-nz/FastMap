using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using FastMap.Config;
using FastMap.Map;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace FastMap.PageSync;

internal sealed class FastMapPageSyncClient
{
    private readonly FastMapConfig config;
    private readonly IClientNetworkChannel channel;
    private readonly ConcurrentQueue<FastMapPageSnapshot> uploadQueue = new();
    private readonly ConcurrentQueue<FastMapPageSnapshot> receivedPages = new();
    private readonly Queue<FastVec2i> requestQueue = new();
    private readonly HashSet<FastVec2i> queuedRequests = new();
    private readonly HashSet<FastVec2i> queuedUploads = new();
    private readonly object requestLock = new();
    private readonly object uploadLock = new();

    public FastMapPageSyncClient(ICoreClientAPI capi, FastMapConfig config)
    {
        this.config = config;
        channel = capi.Network.GetChannel(FastMapPageSyncNetwork.ChannelName);
        channel.SetMessageHandler<FastMapPageSyncPagePacket>(OnPageReceived);
        channel.SetMessageHandler<FastMapPageSyncResetPacket>(OnResetReceived);
    }

    public void QueueUpload(FastMapPageSnapshot snapshot)
    {
        if (!config.EnablePageSync || !snapshot.HasAnyValidChunks)
        {
            return;
        }

        lock (uploadLock)
        {
            if (!queuedUploads.Add(snapshot.PageKey))
            {
                return;
            }
        }

        uploadQueue.Enqueue(snapshot);
    }

    public void RequestPages(IEnumerable<FastVec2i> pageKeys)
    {
        if (!config.EnablePageSync)
        {
            return;
        }

        lock (requestLock)
        {
            foreach (FastVec2i pageKey in pageKeys)
            {
                if (queuedRequests.Add(pageKey))
                {
                    requestQueue.Enqueue(pageKey);
                }
            }
        }
    }

    public void OnClientTick()
    {
        if (!config.EnablePageSync)
        {
            return;
        }

        ProcessUploads();
        ProcessRequests();
    }

    public bool TryDequeueReceivedPage(out FastMapPageSnapshot snapshot)
    {
        return receivedPages.TryDequeue(out snapshot!);
    }

    private void ProcessUploads()
    {
        int count = Math.Min(config.PageSyncMaxUploadsPerTick, uploadQueue.Count);
        for (int i = 0; i < count && uploadQueue.TryDequeue(out FastMapPageSnapshot? snapshot); i++)
        {
            lock (uploadLock)
            {
                queuedUploads.Remove(snapshot.PageKey);
            }

            byte[] payload = FastMapPagePayloadCodec.Encode(snapshot, config.UseHighCompressionCache);
            if (payload.Length == 0)
            {
                continue;
            }

            channel.SendPacket(new FastMapPageSyncUploadPacket
            {
                Payload = payload
            });
        }
    }

    private void ProcessRequests()
    {
        for (int packetIndex = 0; packetIndex < config.PageSyncMaxRequestPacketsPerTick; packetIndex++)
        {
            List<FastMapPageSyncKey> keys = new();
            lock (requestLock)
            {
                while (keys.Count < config.PageSyncMaxPagesPerRequest && requestQueue.Count > 0)
                {
                    FastVec2i pageKey = requestQueue.Dequeue();
                    keys.Add(new FastMapPageSyncKey
                    {
                        X = pageKey.X,
                        Y = pageKey.Y
                    });
                }
            }

            if (keys.Count == 0)
            {
                return;
            }

            channel.SendPacket(new FastMapPageSyncRequestPacket
            {
                Pages = keys.ToArray()
            });
        }
    }

    private void OnPageReceived(FastMapPageSyncPagePacket packet)
    {
        if (FastMapPagePayloadCodec.TryDecode(packet.Payload, out FastMapPageSnapshot snapshot))
        {
            receivedPages.Enqueue(snapshot);
        }
    }

    private void OnResetReceived(FastMapPageSyncResetPacket packet)
    {
        lock (requestLock)
        {
            requestQueue.Clear();
            queuedRequests.Clear();
        }

    }
}
