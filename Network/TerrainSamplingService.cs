using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FastMap.Map;

namespace FastMap.Network;

// Pool and subscription access is on the server thread. Worker results are adopted only on Tick.
internal sealed class TerrainSamplingService : IDisposable
{
    private readonly Dictionary<(int, int, int, int, int), Job> pool = new();
    private readonly List<Subscriber> subscribers = new();
    private readonly Func<string, bool> allowed;
    private readonly Func<int, int, FastMapTerrainSamplerColumn> sample;
    private readonly Action<string, TerrainSamplingBatch> send;
    private readonly Action<string>? log;
    private readonly int sizeX, sizeZ;
    private readonly long cacheLimit;
    private readonly int transferLimit;
    private long serial;
    private int producerCursor, deliveryCursor;
    private readonly TerrainSamplingWorker? worker;
    private Job? workingJob;
    private Task<TerrainSamplingWorker.Result>? work;
    private bool disposed;
    public bool UsesBackgroundWorker => worker != null;
    public double WorkerMilliseconds { get; private set; }
    public long Samples { get; private set; }
    public long WireBytes { get; private set; }
    public long RawBytes { get; private set; }
    public long CacheHits { get; private set; }
    public long SharedRequests { get; private set; }
    public long CompletedRequests { get; private set; }
    public double WorkMilliseconds { get; private set; }
    public long PoolBytes => pool.Values.Sum(j => j.ReservedBytes);
    public int PendingRequests => subscribers.Count;
    public int CachedGrids => pool.Values.Count(j => j.Complete);

    public TerrainSamplingService(int sizeX, int sizeZ, Func<string, bool> allowed,
        Func<int, int, FastMapTerrainSamplerColumn> sample, Action<string, TerrainSamplingBatch> send,
        int cacheMegabytes = 64, int transferKilobytes = 256, Action<string>? log = null, bool backgroundSampling = false)
    {
        this.sizeX = sizeX; this.sizeZ = sizeZ; this.allowed = allowed; this.sample = sample;
        this.send = send; this.log = log;
        cacheLimit = Math.Clamp(cacheMegabytes, 1, 256) * 1024L * 1024;
        transferLimit = Math.Clamp(transferKilobytes, 32, 1024) * 1024;
        if (backgroundSampling) worker = new TerrainSamplingWorker(sizeX, sizeZ, sample);
    }

    public void Request(string owner, TerrainSamplingRequest request)
    {
        if (disposed) return;
        if (request.Cancel) { Remove(owner, request.Id); return; }
        if (!allowed(owner) || !request.IsValid(sizeX, sizeZ))
        { Fail(owner, request.Id, "Terrain sampling is unavailable, denied, or outside the world."); return; }
        if (subscribers.Any(s => s.Owner == owner && s.Id == request.Id)) return;
        if (subscribers.Count >= 32 || subscribers.Count(s => s.Owner == owner) >= 4)
        { Fail(owner, request.Id, "Terrain sampling server is busy; retry later."); return; }
        var key = (request.X, request.Z, request.Width, request.Height, request.Step);
        bool cached = false;
        if (pool.TryGetValue(key, out Job? job))
        {
            cached = job.Complete;
            if (cached) CacheHits++; else SharedRequests++;
        }
        else
        {
            // Reserve worst-case raw size plus block/object overhead before accepting work.
            long needed = (long)request.Width * request.Height * (TerrainSamplingCodec.ColumnBytes + 1) + 131072;
            Evict(needed);
            if (PoolBytes + needed > cacheLimit)
            { Fail(owner, request.Id, "Terrain sample pool is busy; retry later."); return; }
            job = new Job(request, needed);
            pool.Add(key, job);
        }
        job.LastUsed = ++serial;
        subscribers.Add(new Subscriber(owner, request.Id, job, cached));
    }

    private void Evict(long needed)
    {
        while (pool.Count >= 128 || PoolBytes + needed > cacheLimit)
        {
            var candidate = pool.Where(p => p.Value.Complete && !subscribers.Any(s => s.Job == p.Value))
                .OrderBy(p => p.Value.LastUsed).FirstOrDefault();
            if (candidate.Value == null) break;
            candidate.Value.Cancellation.Dispose();
            pool.Remove(candidate.Key);
        }
    }

    public void Remove(string owner, int? id = null)
    {
        subscribers.RemoveAll(s => s.Owner == owner && (!id.HasValue || s.Id == id.Value));
        DropAbandoned();
    }

    private void DropAbandoned()
    {
        foreach (var item in pool.Where(p => !p.Value.Complete && !subscribers.Any(s => s.Job == p.Value)).ToArray())
        {
            // Keep the reservation until the worker acknowledges cancellation. A new subscriber
            // may join during that interval; it will restart from the last committed block.
            if (item.Value == workingJob) { item.Value.Cancellation.Cancel(); continue; }
            item.Value.Buffer.Dispose();
            item.Value.Cancellation.Dispose();
            pool.Remove(item.Key);
        }
    }

    public void Tick(double budgetMilliseconds, int maxSamples)
    {
        if (disposed) return;
        var timer = Stopwatch.StartNew();
        CollectWork();
        double budget = Math.Clamp(budgetMilliseconds, 1, 20);
        int remaining = Math.Clamp(maxSamples, 1, 65536);
        foreach (var sub in subscribers.Where(s => !allowed(s.Owner)).ToArray())
        {
            Fail(sub.Owner, sub.Id, "Terrain sampling permission is unavailable.");
            subscribers.Remove(sub);
        }
        DropAbandoned();

        // Delivery has a separate byte/packet bound. Cache replay never invokes the sampler.
        int bytes = 0, packetCount = 0, idleVisits = 0;
        while (subscribers.Count > 0 && packetCount < 32 && timer.Elapsed.TotalMilliseconds < budget)
        {
            deliveryCursor %= subscribers.Count;
            Subscriber sub = subscribers[deliveryCursor];
            if (sub.NextBlock >= sub.Job.Blocks.Count)
            {
                if (++idleVisits >= subscribers.Count) break;
                deliveryCursor++;
                continue;
            }
            TerrainSamplingBatch block = sub.Job.Blocks[sub.NextBlock];
            if (bytes + block.Data.Length > transferLimit) break;
            idleVisits = 0;
            send(sub.Owner, new TerrainSamplingBatch { Id = sub.Id, Offset = block.Offset, Count = block.Count,
                Compressed = block.Compressed, Data = block.Data });
            sub.NextBlock++;
            bytes += block.Data.Length;
            packetCount++;
            WireBytes += block.Data.Length;
            RawBytes += (long)block.Count * TerrainSamplingCodec.ColumnBytes;
            sub.Bytes += block.Data.Length;
            sub.Job.LastUsed = ++serial;
            if (sub.Job.Complete && sub.NextBlock == sub.Job.Blocks.Count)
            {
                CompletedRequests++;
                log?.Invoke($"grid {sub.Job.Request.Width}x{sub.Job.Request.Height} step={sub.Job.Request.Step} cache={sub.Cached} elapsedMs={sub.Timer.ElapsedMilliseconds} wireBytes={sub.Bytes}");
                subscribers.RemoveAt(deliveryCursor);
            }
            else deliveryCursor++;
        }

        Job[] active = pool.Values.Where(j => !j.Complete).ToArray();
        if (worker != null)
        {
            if (work == null && active.Length > 0)
            {
                producerCursor %= active.Length;
                workingJob = active[producerCursor++];
                work = worker.Schedule(workingJob.Request, workingJob.Offset, workingJob.Cancellation.Token);
            }
            WorkMilliseconds += timer.Elapsed.TotalMilliseconds;
            return;
        }
        while (active.Length > 0 && remaining > 0 && timer.Elapsed.TotalMilliseconds < budget)
        {
            producerCursor %= active.Length;
            Job job = active[producerCursor++];
            if (job.Complete)
            {
                active = active.Where(j => !j.Complete).ToArray();
                continue;
            }
            var request = job.Request;
            int quantum = Math.Min(128, remaining);
            try
            {
                while (quantum-- > 0 && job.Offset < request.Width * request.Height
                    && timer.Elapsed.TotalMilliseconds < budget)
                {
                    int x = Math.Clamp(request.X + (job.Offset % request.Width) * request.Step, 0, sizeX - 1);
                    int z = Math.Clamp(request.Z + (job.Offset / request.Width) * request.Step, 0, sizeZ - 1);
                    TerrainSamplingCodec.Write(job.Writer, sample(x, z));
                    job.Offset++; Samples++; remaining--;
                    if (job.Buffer.Length == TerrainSamplingCodec.MaxBatchColumns * TerrainSamplingCodec.ColumnBytes
                        || job.Offset == request.Width * request.Height)
                    {
                        job.Blocks.Add(TerrainSamplingCodec.Encode(job.Buffer.ToArray(), job.BlockOffset));
                        job.BlockOffset = job.Offset;
                        job.Buffer.SetLength(0);
                    }
                }
                if (job.Complete)
                {
                    job.Writer.Dispose();
                    job.Buffer.Dispose();
                    job.ReservedBytes = job.Blocks.Sum(b => (long)b.Data.Length + 128) + 256;
                }
            }
            catch (Exception)
            {
                foreach (var sub in subscribers.Where(s => s.Job == job).ToArray())
                { Fail(sub.Owner, sub.Id, "Terrain Sampler failed to sample this area."); subscribers.Remove(sub); }
                job.Buffer.Dispose();
                job.Cancellation.Dispose();
                pool.Remove((request.X, request.Z, request.Width, request.Height, request.Step));
                active = active.Where(j => j != job).ToArray();
            }
        }
        WorkMilliseconds += timer.Elapsed.TotalMilliseconds;
    }

    private void Fail(string owner, int id, string reason) => send(owner, new TerrainSamplingBatch { Id = id, Error = reason });

    public void UpdateLoad(double cpuPercent, long intervalMilliseconds) => worker?.UpdateLoad(cpuPercent, intervalMilliseconds);

    private void CollectWork()
    {
        if (work == null || !work.IsCompleted) return;
        Job job = workingJob!;
        try
        {
            var result = work.GetAwaiter().GetResult();
            job.Blocks.AddRange(result.Blocks);
            job.Offset += result.Count;
            Samples += result.Count;
            WorkerMilliseconds += result.Milliseconds;
            if (job.Complete)
            {
                job.Writer.Dispose();
                job.ReservedBytes = job.Blocks.Sum(b => (long)b.Data.Length + 128) + 256;
            }
        }
        catch (OperationCanceledException)
        {
            job.Cancellation.Dispose();
            job.Cancellation = new CancellationTokenSource();
        }
        catch (Exception)
        {
            foreach (var sub in subscribers.Where(s => s.Job == job).ToArray())
            { Fail(sub.Owner, sub.Id, "Terrain Sampler failed to sample this area."); subscribers.Remove(sub); }
        }
        workingJob = null;
        work = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        workingJob?.Cancellation.Cancel();
        worker?.Dispose();
        // Observe a failed result without sending any packets during shutdown.
        if (work?.IsFaulted == true) _ = work.Exception;
        foreach (Job job in pool.Values) { job.Writer.Dispose(); job.Cancellation.Dispose(); }
        subscribers.Clear(); pool.Clear();
        work = null; workingJob = null;
    }
    private sealed class Job
    {
        public readonly TerrainSamplingRequest Request;
        public readonly List<TerrainSamplingBatch> Blocks = new();
        public readonly MemoryStream Buffer = new();
        public readonly BinaryWriter Writer;
        public int Offset, BlockOffset;
        public long ReservedBytes, LastUsed;
        public CancellationTokenSource Cancellation = new();
        public bool Complete => Offset == Request.Width * Request.Height;
        public Job(TerrainSamplingRequest request, long reserved)
        { Request = request; ReservedBytes = reserved; Writer = new BinaryWriter(Buffer); }
    }
    private sealed class Subscriber
    {
        public readonly string Owner;
        public readonly int Id;
        public readonly Job Job;
        public readonly bool Cached;
        public readonly Stopwatch Timer = Stopwatch.StartNew();
        public int NextBlock;
        public long Bytes;
        public Subscriber(string owner, int id, Job job, bool cached)
        { Owner = owner; Id = id; Job = job; Cached = cached; }
    }
}
