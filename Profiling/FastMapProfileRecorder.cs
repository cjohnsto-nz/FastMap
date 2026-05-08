#if FASTMAPPROFILING
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using FastMap.Config;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace FastMap.Profiling;

public static class FastMapProfileRecorder
{
    private static readonly object Sync = new();
    private static FastMapProfileSink? clientSink;
    private static FastMapProfileSink? serverSink;

    public static string Start(ICoreAPI api, FastMapConfig config)
    {
        string side = api.Side == EnumAppSide.Server ? "server" : "client";
        FastMapProfileSink sink = new(api, side, config.ProfileAutoFlushIntervalSeconds);

        lock (Sync)
        {
            if (api.Side == EnumAppSide.Server)
            {
                serverSink?.Dispose();
                serverSink = sink;
            }
            else
            {
                clientSink?.Dispose();
                clientSink = sink;
            }
        }

        api.Logger.Notification("[FastMap] Profiling started for {0}. Output: {1}", side, sink.FilePath);
        return sink.FilePath;
    }

    public static string Stop(EnumAppSide side)
    {
        FastMapProfileSink? sink;
        lock (Sync)
        {
            if (side == EnumAppSide.Server)
            {
                sink = serverSink;
                serverSink = null;
            }
            else
            {
                sink = clientSink;
                clientSink = null;
            }
        }

        if (sink == null)
        {
            return "FastMap profiling was not running.";
        }

        string path = sink.FilePath;
        sink.Dispose();
        return "FastMap profiling stopped. Output: " + path;
    }

    public static string Status(EnumAppSide side)
    {
        FastMapProfileSink? sink = side == EnumAppSide.Server
            ? Volatile.Read(ref serverSink)
            : Volatile.Read(ref clientSink);

        return sink == null
            ? "FastMap profiling is not running."
            : "FastMap profiling is running. Output: " + sink.FilePath;
    }

    public static string Flush(EnumAppSide side)
    {
        FastMapProfileSink? sink = side == EnumAppSide.Server
            ? Volatile.Read(ref serverSink)
            : Volatile.Read(ref clientSink);

        if (sink == null)
        {
            return "FastMap profiling is not running.";
        }

        sink.Flush();
        return "FastMap profiling flushed. Output: " + sink.FilePath;
    }

    public static void DisposeAll()
    {
        FastMapProfileSink? client;
        FastMapProfileSink? server;
        lock (Sync)
        {
            client = clientSink;
            server = serverSink;
            clientSink = null;
            serverSink = null;
        }

        client?.Dispose();
        server?.Dispose();
    }

    public static bool ClientEnabled => Volatile.Read(ref clientSink) != null;

    public static bool ServerEnabled => Volatile.Read(ref serverSink) != null;

    public static void RecordClient(string stage, int chunkX = 0, int chunkY = 0, int chunkZ = 0, double durationMs = 0, long bytes = 0, string? detail = null, string kind = "exclusive", string category = "general")
    {
        Volatile.Read(ref clientSink)?.Record(stage, chunkX, chunkY, chunkZ, durationMs, bytes, detail, kind, category);
    }

    public static void RecordServer(string stage, int chunkX = 0, int chunkY = 0, int chunkZ = 0, double durationMs = 0, long bytes = 0, string? detail = null, string kind = "exclusive", string category = "general")
    {
        Volatile.Read(ref serverSink)?.Record(stage, chunkX, chunkY, chunkZ, durationMs, bytes, detail, kind, category);
    }

    public static long Timestamp() => Stopwatch.GetTimestamp();

    public static double ElapsedMilliseconds(long startTimestamp)
    {
        return (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
    }

    private sealed class FastMapProfileSink : IDisposable
    {
        private readonly ConcurrentQueue<string> pendingLines = new();
        private readonly StreamWriter writer;
        private readonly long startedTimestamp = Stopwatch.GetTimestamp();
        private readonly object writerLock = new();
        private readonly ICoreAPI api;
        private readonly long listenerId;
        private volatile bool disposed;

        public FastMapProfileSink(ICoreAPI api, string side, int autoFlushIntervalSeconds)
        {
            this.api = api;
            Side = side;
            string saveId = SanitizePathPart(api.World?.SavegameIdentifier ?? "unknown-world");
            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string directory = Path.Combine(GamePaths.DataPath, "FastMap", "profiles", saveId);
            Directory.CreateDirectory(directory);
            FilePath = Path.Combine(directory, "fastmap-profile-" + side + "-" + timestamp + ".csv");
            writer = new StreamWriter(new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.Read), Encoding.UTF8);
            writer.WriteLine("utcTimestamp,elapsedMs,side,stage,chunkX,chunkY,chunkZ,durationMs,bytes,detail,kind,category,threadId");
            writer.Flush();
            listenerId = api.Event.RegisterGameTickListener(_ => Flush(), Math.Max(1, autoFlushIntervalSeconds) * 1000);
        }

        public string FilePath { get; }
        private string Side { get; }

        public void Record(string stage, int chunkX, int chunkY, int chunkZ, double durationMs, long bytes, string? detail, string kind, string category)
        {
            if (disposed)
            {
                return;
            }

            double elapsedMs = (Stopwatch.GetTimestamp() - startedTimestamp) * 1000.0 / Stopwatch.Frequency;
            string line = string.Join(
                ",",
                Csv(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
                elapsedMs.ToString("F3", CultureInfo.InvariantCulture),
                Csv(Side),
                Csv(stage),
                chunkX.ToString(CultureInfo.InvariantCulture),
                chunkY.ToString(CultureInfo.InvariantCulture),
                chunkZ.ToString(CultureInfo.InvariantCulture),
                durationMs.ToString("F3", CultureInfo.InvariantCulture),
                bytes.ToString(CultureInfo.InvariantCulture),
                Csv(detail ?? string.Empty),
                Csv(kind),
                Csv(category),
                Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture));

            pendingLines.Enqueue(line);
        }

        public void Flush()
        {
            if (disposed)
            {
                return;
            }

            lock (writerLock)
            {
                while (pendingLines.TryDequeue(out string? line))
                {
                    writer.WriteLine(line);
                }

                writer.Flush();
            }
        }

        public void Dispose()
        {
            disposed = true;
            api.Event.UnregisterGameTickListener(listenerId);
            lock (writerLock)
            {
                while (pendingLines.TryDequeue(out string? line))
                {
                    writer.WriteLine(line);
                }

                writer.Dispose();
            }
        }

        private static string Csv(string value)
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static string SanitizePathPart(string value)
        {
            foreach (char invalidChar in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalidChar, '_');
            }

            return string.IsNullOrWhiteSpace(value) ? "unknown-world" : value;
        }
    }
}
#else
using System.Diagnostics;

namespace FastMap.Profiling;

public static class FastMapProfileRecorder
{
    public const bool ClientEnabled = false;

    public const bool ServerEnabled = false;

    [Conditional("FASTMAPPROFILING")]
    public static void RecordClient(string stage, int chunkX = 0, int chunkY = 0, int chunkZ = 0, double durationMs = 0, long bytes = 0, string? detail = null, string kind = "exclusive", string category = "general")
    {
    }

    [Conditional("FASTMAPPROFILING")]
    public static void RecordServer(string stage, int chunkX = 0, int chunkY = 0, int chunkZ = 0, double durationMs = 0, long bytes = 0, string? detail = null, string kind = "exclusive", string category = "general")
    {
    }

    public static long Timestamp() => 0;

    public static double ElapsedMilliseconds(long startTimestamp) => 0;
}
#endif
