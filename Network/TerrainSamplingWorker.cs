using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FastMap.Map;

namespace FastMap.Network;

// A persistent thread preserves Terrain Sampler's thread-local context cache. Only immutable
// work descriptions and completed blocks cross threads; players, the pool and network stay on main.
internal sealed class TerrainSamplingWorker : IDisposable
{
    internal sealed record Result(List<TerrainSamplingBatch> Blocks, int Count, double Milliseconds);
    private sealed record Work(TerrainSamplingRequest Request, int Offset, int Count,
        CancellationToken Cancellation, TaskCompletionSource<Result> Completion);
    private readonly BlockingCollection<Work> queue = new(1);
    private readonly Thread thread;
    private readonly Func<int, int, FastMapTerrainSamplerColumn> sample;
    private readonly int sizeX, sizeZ;
    private volatile int delayMilliseconds;

    public TerrainSamplingWorker(int sizeX, int sizeZ, Func<int, int, FastMapTerrainSamplerColumn> sample)
    {
        this.sizeX = sizeX; this.sizeZ = sizeZ; this.sample = sample;
        thread = new Thread(Run) { IsBackground = true, Name = "FastMap terrain samples" };
        thread.Start();
    }

    public void UpdateLoad(double processCpuPercent, long intervalMilliseconds) =>
        delayMilliseconds = processCpuPercent >= 75 ? 20 : intervalMilliseconds > 100 ? 10 : 0;

    public Task<Result> Schedule(TerrainSamplingRequest request, int offset, CancellationToken cancellation)
    {
        var completion = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Add(new Work(request, offset, Math.Min(16384, request.Width * request.Height - offset), cancellation, completion));
        return completion.Task;
    }

    private void Run()
    {
        foreach (Work work in queue.GetConsumingEnumerable())
        {
            try { work.Completion.SetResult(Generate(work)); }
            catch (OperationCanceledException) { work.Completion.SetCanceled(work.Cancellation); }
            catch (Exception ex) { work.Completion.SetException(ex); }
        }
    }

    private Result Generate(Work work)
    {
        var timer = Stopwatch.StartNew();
        var blocks = new List<TerrainSamplingBatch>();
        using var buffer = new MemoryStream(TerrainSamplingCodec.MaxBatchColumns * TerrainSamplingCodec.ColumnBytes);
        using var writer = new BinaryWriter(buffer);
        int end = work.Offset + work.Count;
        int blockOffset = work.Offset;
        for (int offset = work.Offset; offset < end; offset++)
        {
            if ((offset & 127) == 0) work.Cancellation.ThrowIfCancellationRequested();
            var request = work.Request;
            int x = Math.Clamp(request.X + offset % request.Width * request.Step, 0, sizeX - 1);
            int z = Math.Clamp(request.Z + offset / request.Width * request.Step, 0, sizeZ - 1);
            TerrainSamplingCodec.Write(writer, sample(x, z));
            if (buffer.Length == TerrainSamplingCodec.MaxBatchColumns * TerrainSamplingCodec.ColumnBytes || offset + 1 == end)
            {
                blocks.Add(TerrainSamplingCodec.Encode(buffer.ToArray(), blockOffset));
                buffer.SetLength(0);
                blockOffset = offset + 1;
                int delay = delayMilliseconds;
                if (delay > 0 && work.Cancellation.WaitHandle.WaitOne(delay)) work.Cancellation.ThrowIfCancellationRequested();
            }
        }
        work.Cancellation.ThrowIfCancellationRequested();
        return new Result(blocks, work.Count, timer.Elapsed.TotalMilliseconds);
    }

    // Caller cancels the current job first. Join before Terrain Sampler disposes its caches.
    public void Dispose()
    {
        queue.CompleteAdding();
        thread.Join();
        queue.Dispose();
    }
}
