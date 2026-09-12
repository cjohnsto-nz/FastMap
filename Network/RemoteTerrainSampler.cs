using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FastMap.Map;

namespace FastMap.Network;

// Called by map workers and the main-thread network pump. Never waits for the network.
internal sealed class RemoteTerrainSampler : IDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<(int, int, int, int, int), Grid> grids = new();
    private readonly Queue<TerrainSamplingRequest> outgoing = new();
    private readonly Func<long> clock;
    private int nextId;
    private bool available;
    private bool disposed;
    private const int MaxPending = 4;
    private const int MaxCachedSamples = 2 * 1025 * 1025;
    private const int TimeoutMs = 30000;
    public RemoteTerrainSampler(Func<long> clock) => this.clock = clock;
    public bool Available { get { lock (sync) return available && !disposed; } }

    public void SetAvailable(bool value)
    {
        lock (sync)
        {
            available = value;
            if (!value) { grids.Clear(); outgoing.Clear(); }
        }
    }

    public FastMapTerrainSamplerColumn[] SampleGrid(int x, int z, int width, int height, int step)
    {
        lock (sync)
        {
            if (!available || disposed) throw new InvalidOperationException("Server terrain sampling is unavailable.");
            var key = (x, z, width, height, step);
            if (grids.TryGetValue(key, out Grid? existing))
            {
                existing.LastUsed = clock();
                if (existing.Error != null)
                {
                    grids.Remove(key);
                    throw new InvalidOperationException(existing.Error);
                }
                if (existing.Received == existing.Samples.Length) return existing.Samples;
                throw new TerrainSamplesPendingException();
            }

            if (grids.Values.Count(g => !g.Complete) >= MaxPending) throw new TerrainSamplesPendingException();
            int count = checked(width * height);
            if (width < 1 || height < 1 || width > 1025 || height > 1025 || step < 1 || step > 32)
                throw new ArgumentOutOfRangeException(nameof(width));
            // Evict completed, least recently used grids before allocating another page.
            while (grids.Count >= 64 || grids.Values.Sum(g => g.Samples.Length) + count > MaxCachedSamples)
            {
                var oldest = grids.Where(g => g.Value.Complete).OrderBy(g => g.Value.LastUsed).FirstOrDefault();
                if (oldest.Value == null) throw new TerrainSamplesPendingException();
                grids.Remove(oldest.Key);
            }
            var request = new TerrainSamplingRequest { Id = ++nextId, X = x, Z = z, Width = width, Height = height, Step = step };
            grids.Add(key, new Grid(request, count, clock()));
            outgoing.Enqueue(request);
            throw new TerrainSamplesPendingException();
        }
    }

    public void Pump(Action<TerrainSamplingRequest> send)
    {
        lock (sync)
        {
            if (!available || disposed) return;
            foreach (var entry in grids.ToArray())
            {
                Grid grid = entry.Value;
                if (grid.Complete) continue;
                // Abandon work the viewport no longer asks for, or a stalled stream.
                if (clock() - grid.LastUsed > 15000 || clock() - grid.LastProgress > TimeoutMs)
                {
                    outgoing.Enqueue(new TerrainSamplingRequest { Id = grid.Request.Id, Cancel = true });
                    grid.Error = "Server terrain sampling timed out or was cancelled.";
                }
            }
            while (outgoing.TryDequeue(out TerrainSamplingRequest? request)) send(request);
        }
    }

    public void Receive(TerrainSamplingBatch batch)
    {
        lock (sync)
        {
            Grid? grid = grids.Values.FirstOrDefault(g => g.Request.Id == batch.Id);
            if (grid == null || grid.Complete) return;
            if (!string.IsNullOrEmpty(batch.Error)) { grid.Error = batch.Error; return; }
            int count = batch.Count;
            if (batch.Offset != grid.Received || count < 1 || count > TerrainSamplingCodec.MaxBatchColumns
                || count > grid.Samples.Length - grid.Received)
            {
                grid.Error = "Invalid terrain sampling response.";
                outgoing.Enqueue(new TerrainSamplingRequest { Id = batch.Id, Cancel = true });
                return;
            }
            try
            {
                byte[] data = TerrainSamplingCodec.Decode(batch);
                using var reader = new BinaryReader(new MemoryStream(data, false));
                for (int i = 0; i < count; i++) grid.Samples[grid.Received + i] = TerrainSamplingCodec.Read(reader);
                grid.Received += count;
                grid.LastProgress = clock();
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is EndOfStreamException || ex is ArgumentException)
            {
                grid.Error = "Invalid terrain sampling response.";
                outgoing.Enqueue(new TerrainSamplingRequest { Id = batch.Id, Cancel = true });
            }
        }
    }

    public void Dispose()
    {
        lock (sync) { disposed = true; available = false; grids.Clear(); outgoing.Clear(); }
    }

    private sealed class Grid
    {
        public readonly TerrainSamplingRequest Request;
        public readonly FastMapTerrainSamplerColumn[] Samples;
        public int Received;
        public long LastUsed;
        public long LastProgress;
        public string? Error;
        public bool Complete => Error != null || Received == Samples.Length;
        public Grid(TerrainSamplingRequest request, int count, long now)
        { Request = request; Samples = new FastMapTerrainSamplerColumn[count]; LastUsed = LastProgress = now; }
    }
}
