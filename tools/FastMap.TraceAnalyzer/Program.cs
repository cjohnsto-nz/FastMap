using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/FastMap.TraceAnalyzer -- <trace.nettrace> [--dump-events]");
    return 1;
}

string tracePath = args[0];
bool dumpEvents = args.Contains("--dump-events", StringComparer.OrdinalIgnoreCase);
bool dumpAllocationMembers = args.Contains("--dump-allocation-members", StringComparer.OrdinalIgnoreCase);
if (!File.Exists(tracePath))
{
    Console.Error.WriteLine("Trace not found: " + tracePath);
    return 1;
}

if (dumpAllocationMembers)
{
    foreach (System.Reflection.MemberInfo member in typeof(GCAllocationTickTraceData).GetMembers().OrderBy(static member => member.Name))
    {
        Console.WriteLine(member.MemberType + " " + member.Name);
    }

    return 0;
}

Dictionary<string, EventStats> events = new(StringComparer.Ordinal);
Dictionary<string, AllocationStats> allocationByType = new(StringComparer.Ordinal);
Dictionary<string, string> samplePayloadByEvent = new(StringComparer.Ordinal);
List<TraceEvent> gcEvents = new();
List<GcPause> gcPauses = new();
List<EePause> eePauses = new();
Dictionary<int, GcPause> activeGcPauses = new();
EePause? activeEePause = null;
double firstTimestamp = double.NaN;
double lastTimestamp = 0;
long allocationSampleBytes = 0;
long allocationSampleCount = 0;

using EventPipeEventSource source = new(tracePath);
source.Clr.GCStart += data =>
{
    activeGcPauses[data.Count] = new GcPause(data.Count, data.TimeStampRelativeMSec)
    {
        Reason = data.Reason.ToString(),
        Type = data.Type.ToString(),
        Depth = data.Depth,
    };
};
source.Clr.GCStop += data =>
{
    if (activeGcPauses.Remove(data.Count, out GcPause? pause))
    {
        pause.StopMs = data.TimeStampRelativeMSec;
        gcPauses.Add(pause);
    }
};
source.Clr.GCHeapStats += data =>
{
    if (gcPauses.Count > 0)
    {
        GcPause pause = gcPauses[^1];
        if (Math.Abs(pause.StopMs - data.TimeStampRelativeMSec) < 250)
        {
            pause.GenerationSize0 = data.GenerationSize0;
            pause.GenerationSize1 = data.GenerationSize1;
            pause.GenerationSize2 = data.GenerationSize2;
            pause.GenerationSize3 = data.GenerationSize3;
            pause.GenerationSize4 = data.GenerationSize4;
        }
    }
};
source.Clr.GCSuspendEEStart += data =>
{
    activeEePause = new EePause(data.TimeStampRelativeMSec)
    {
        Reason = data.Reason.ToString(),
    };
};
source.Clr.GCRestartEEStop += data =>
{
    if (activeEePause != null)
    {
        activeEePause.StopMs = data.TimeStampRelativeMSec;
        eePauses.Add(activeEePause);
        activeEePause = null;
    }
};
source.Clr.GCAllocationTick += data =>
{
    string typeName = data.TypeName ?? "unknown";
    long amount = data.AllocationAmount64;
    allocationSampleBytes += amount;
    allocationSampleCount++;
    if (!allocationByType.TryGetValue(typeName, out AllocationStats? allocationStats))
    {
        allocationStats = new AllocationStats(typeName);
        allocationByType[typeName] = allocationStats;
    }

    allocationStats.Count++;
    allocationStats.Bytes += amount;
    allocationStats.LastMs = data.TimeStampRelativeMSec;
};
source.Dynamic.All += traceEvent =>
{
    firstTimestamp = double.IsNaN(firstTimestamp) ? traceEvent.TimeStampRelativeMSec : firstTimestamp;
    lastTimestamp = traceEvent.TimeStampRelativeMSec;

    string eventName = traceEvent.ProviderName + "/" + traceEvent.EventName;
    if (!events.TryGetValue(eventName, out EventStats? eventStats))
    {
        eventStats = new EventStats(eventName);
        events[eventName] = eventStats;
    }

    eventStats.Count++;
    eventStats.LastMs = traceEvent.TimeStampRelativeMSec;
    samplePayloadByEvent.TryAdd(eventName, PayloadSummary(traceEvent));

    if (false && traceEvent.ProviderName.Contains("DotNETRuntime", StringComparison.OrdinalIgnoreCase)
        && traceEvent.EventName.Contains("GC", StringComparison.OrdinalIgnoreCase))
    {
        gcEvents.Add(traceEvent.Clone());
    }

    if (traceEvent.EventName.Contains("AllocationTick", StringComparison.OrdinalIgnoreCase) && allocationSampleCount == 0)
    {
        string typeName = GetPayloadString(traceEvent, "TypeName")
            ?? GetPayloadString(traceEvent, "TypeID")
            ?? "unknown";
        long amount = GetPayloadLong(traceEvent, "AllocationAmount")
            ?? GetPayloadLong(traceEvent, "AllocationAmount64")
            ?? GetPayloadLong(traceEvent, "AllocAmount")
            ?? 0;

        allocationSampleBytes += amount;
        allocationSampleCount++;
        if (!allocationByType.TryGetValue(typeName, out AllocationStats? allocationStats))
        {
            allocationStats = new AllocationStats(typeName);
            allocationByType[typeName] = allocationStats;
        }

        allocationStats.Count++;
        allocationStats.Bytes += amount;
        allocationStats.LastMs = traceEvent.TimeStampRelativeMSec;
    }
};

source.Process();

double durationSeconds = (lastTimestamp - firstTimestamp) / 1000.0;
Console.WriteLine($"Trace: {tracePath}");
Console.WriteLine($"Duration: {durationSeconds:n1}s");
Console.WriteLine($"Events: {events.Values.Sum(static item => item.Count):n0}");
Console.WriteLine();

Console.WriteLine("Top events:");
foreach (EventStats item in events.Values.OrderByDescending(static item => item.Count).Take(dumpEvents ? 80 : 20))
{
    Console.WriteLine($"{item.Count,10:n0}  last={item.LastMs / 1000.0,8:n1}s  {item.Name}");
    if (dumpEvents && samplePayloadByEvent.TryGetValue(item.Name, out string? sample))
    {
        Console.WriteLine($"              sample: {sample}");
    }
}

Console.WriteLine();
Console.WriteLine("GC pauses:");
foreach (GcPause item in gcPauses.OrderByDescending(static item => item.DurationMs).Take(20).OrderBy(static item => item.StartMs))
{
    Console.WriteLine($"{item.StartMs / 1000.0,8:n3}s  duration={item.DurationMs,8:n3}ms  gen={item.Depth}  reason={item.Reason}  type={item.Type}  heap={FormatBytes(item.TotalHeapBytes)}");
}

Console.WriteLine();
Console.WriteLine("Stop-the-world EE pauses:");
foreach (EePause item in eePauses.OrderByDescending(static item => item.DurationMs).Take(20).OrderBy(static item => item.StartMs))
{
    Console.WriteLine($"{item.StartMs / 1000.0,8:n3}s  duration={item.DurationMs,8:n3}ms  reason={item.Reason}");
}

Console.WriteLine();
Console.WriteLine($"AllocationTick samples: {allocationSampleCount:n0}, sampled bytes={FormatBytes(allocationSampleBytes)}");
foreach (AllocationStats item in allocationByType.Values.OrderByDescending(static item => item.Bytes).Take(30))
{
    Console.WriteLine($"{FormatBytes(item.Bytes),12}  {item.Count,8:n0}  last={item.LastMs / 1000.0,8:n1}s  {item.TypeName}");
}

Console.WriteLine();
Console.WriteLine("GC events near end:");
double nearEndStart = Math.Max(firstTimestamp, lastTimestamp - 15000);
foreach (TraceEvent item in gcEvents.Where(item => item.TimeStampRelativeMSec >= nearEndStart).OrderBy(item => item.TimeStampRelativeMSec))
{
    Console.WriteLine($"{item.TimeStampRelativeMSec / 1000.0,8:n3}s  {item.EventName}  {PayloadSummary(item)}");
}

return 0;

static string? GetPayloadString(TraceEvent traceEvent, string name)
{
    try
    {
        object? value = traceEvent.PayloadByName(name);
        return value?.ToString();
    }
    catch
    {
        return null;
    }
}

static long? GetPayloadLong(TraceEvent traceEvent, string name)
{
    try
    {
        object? value = traceEvent.PayloadByName(name);
        return value switch
        {
            null => null,
            long longValue => longValue,
            int intValue => intValue,
            uint uintValue => uintValue,
            ulong ulongValue when ulongValue <= long.MaxValue => (long)ulongValue,
            _ when long.TryParse(value.ToString(), out long parsed) => parsed,
            _ => null
        };
    }
    catch
    {
        return null;
    }
}

static string PayloadSummary(TraceEvent traceEvent)
{
    List<string> parts = new();
    foreach (string name in traceEvent.PayloadNames)
    {
        object? value = null;
        try
        {
            value = traceEvent.PayloadByName(name);
        }
        catch
        {
        }

        parts.Add(name + "=" + value);
    }

    return string.Join("; ", parts);
}

static string FormatBytes(long bytes)
{
    string[] suffixes = ["B", "KB", "MB", "GB"];
    double value = bytes;
    int suffix = 0;
    while (value >= 1024 && suffix < suffixes.Length - 1)
    {
        value /= 1024;
        suffix++;
    }

    return $"{value:n1} {suffixes[suffix]}";
}

internal sealed class EventStats(string name)
{
    public string Name { get; } = name;

    public long Count { get; set; }

    public double LastMs { get; set; }
}

internal sealed class AllocationStats(string typeName)
{
    public string TypeName { get; } = typeName;

    public long Count { get; set; }

    public long Bytes { get; set; }

    public double LastMs { get; set; }
}

internal sealed class GcPause(int count, double startMs)
{
    public int Count { get; } = count;

    public double StartMs { get; } = startMs;

    public double StopMs { get; set; }

    public double DurationMs => StopMs <= 0 ? 0 : StopMs - StartMs;

    public int Depth { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public long GenerationSize0 { get; set; }

    public long GenerationSize1 { get; set; }

    public long GenerationSize2 { get; set; }

    public long GenerationSize3 { get; set; }

    public long GenerationSize4 { get; set; }

    public long TotalHeapBytes => GenerationSize0 + GenerationSize1 + GenerationSize2 + GenerationSize3 + GenerationSize4;
}

internal sealed class EePause(double startMs)
{
    public double StartMs { get; } = startMs;

    public double StopMs { get; set; }

    public double DurationMs => StopMs <= 0 ? 0 : StopMs - StartMs;

    public string Reason { get; set; } = string.Empty;
}
