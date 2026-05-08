using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using ProtoBuf;

const int ChunksPerPage = 32;
const int ChunkSize = 32;
const int NativeDbQueryBatchSize = 512;
const int PagePixels = ChunksPerPage * ChunkSize * ChunksPerPage * ChunkSize;
const ulong CoordMask = (1UL << 27) - 1UL;
const ulong SignBit = 1UL << 26;

Dictionary<string, string> options = ParseArgs(args);
if (!options.TryGetValue("db", out string? dbPath) || string.IsNullOrWhiteSpace(dbPath))
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/FastMap.DbBench -- --db <mapdb.db> [--name label] [--output benchmarks] [--limit-pages n] [--parallel]");
    return 2;
}

dbPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dbPath));
if (!File.Exists(dbPath))
{
    Console.Error.WriteLine($"Map DB not found: {dbPath}");
    return 2;
}

string name = options.TryGetValue("name", out string? configuredName) && !string.IsNullOrWhiteSpace(configuredName)
    ? SanitizeFileName(configuredName)
    : Path.GetFileNameWithoutExtension(dbPath);
string outputDir = Path.GetFullPath(options.TryGetValue("output", out string? configuredOutput) && !string.IsNullOrWhiteSpace(configuredOutput)
    ? configuredOutput
    : "benchmarks");
int limitPages = options.TryGetValue("limit-pages", out string? configuredLimit) && int.TryParse(configuredLimit, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLimit)
    ? parsedLimit
    : 0;
bool parallel = options.ContainsKey("parallel");
int[] parallelDegrees = ParseParallelDegrees(options);

Directory.CreateDirectory(outputDir);

string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
string csvPath = Path.Combine(outputDir, $"mapdb-bench-{timestamp}-{name}.csv");
string mdPath = Path.Combine(outputDir, $"mapdb-bench-{timestamp}-{name}.md");

Console.WriteLine($"Benchmarking {dbPath}");
Console.WriteLine($"Output: {csvPath}");

List<ResultRow> results = new();

using SqliteConnection connection = OpenReadOnly(dbPath);
long dbBytes = new FileInfo(dbPath).Length;
long rowCount = ExecuteScalarLong(connection, "SELECT COUNT(*) FROM mappiece");
results.Add(new ResultRow(name, "metadata", "db_bytes", 1, dbBytes, 0, 0, 0, string.Empty));
results.Add(new ResultRow(name, "metadata", "mappiece_rows", 1, rowCount, 0, 0, 0, string.Empty));

List<MapRow> rows = Bench("scan_positions", () => ScanPositions(connection), out double scanPositionsMs);
results.Add(new ResultRow(name, "scan_positions", "all_rows", 1, rows.Count, scanPositionsMs, scanPositionsMs, 0, string.Empty));

Dictionary<PageKey, List<MapRow>> pages = BuildPageIndex(rows);
List<PageKey> pageKeys = pages.Keys.OrderBy(static key => key.Z).ThenBy(static key => key.X).ToList();
if (limitPages > 0 && pageKeys.Count > limitPages)
{
    pageKeys = pageKeys.Take(limitPages).ToList();
}

long selectedRows = pageKeys.Sum(key => pages[key].Count);
results.Add(new ResultRow(name, "metadata", limitPages > 0 ? "selected_pages_limited" : "selected_pages", pageKeys.Count, selectedRows, 0, 0, 0, $"totalPages={pages.Count}"));

FullScanDecodeResult fullScan = Bench("full_scan_decode_group", () => FullScanDecodeGroup(connection), out double fullScanMs);
results.Add(new ResultRow(name, "full_scan_decode_group", "all_rows", 1, fullScan.Rows, fullScanMs, fullScanMs, fullScan.Pages, $"pixels={fullScan.Pixels};deserialized={fullScan.Deserialized};blobMs={fullScan.BlobMs:F2};deserializeMs={fullScan.DeserializeMs:F2}"));

PageAssembleResult pointProbe = Bench("point_probe_known_pages", () => PointProbeKnownPages(connection, pageKeys, rows.Select(static row => row.Position).ToHashSet()), out double pointProbeMs);
results.Add(new ResultRow(name, "point_probe_known_pages", "selected_pages", pageKeys.Count, pointProbe.Rows, pointProbeMs, pointProbeMs / Math.Max(1, pageKeys.Count), pointProbe.Pages, $"pixels={pointProbe.Pixels};deserialized={pointProbe.Deserialized};sqliteQueries={pointProbe.SqliteQueries};blobMs={pointProbe.BlobMs:F2};deserializeMs={pointProbe.DeserializeMs:F2};copyMs={pointProbe.CopyMs:F2}"));

PageAssembleResult pageInQuery = Bench("page_in_query", () => PageInQuery(connection, pageKeys), out double pageInQueryMs);
results.Add(new ResultRow(name, "page_in_query", "selected_pages", pageKeys.Count, pageInQuery.Rows, pageInQueryMs, pageInQueryMs / Math.Max(1, pageKeys.Count), pageInQuery.Pages, $"pixels={pageInQuery.Pixels};deserialized={pageInQuery.Deserialized};sqliteQueries={pageInQuery.SqliteQueries};blobMs={pageInQuery.BlobMs:F2};deserializeMs={pageInQuery.DeserializeMs:F2};copyMs={pageInQuery.CopyMs:F2}"));

if (parallel)
{
    foreach (int degree in parallelDegrees)
    {
        PageAssembleResult parallelPageInQuery = Bench($"page_in_query_parallel_{degree}", () => PageInQueryParallel(dbPath, pageKeys, degree), out double parallelPageInQueryMs);
        results.Add(new ResultRow(name, "page_in_query_parallel", $"degree={degree}", pageKeys.Count, parallelPageInQuery.Rows, parallelPageInQueryMs, parallelPageInQueryMs / Math.Max(1, pageKeys.Count), parallelPageInQuery.Pages, $"pixels={parallelPageInQuery.Pixels};deserialized={parallelPageInQuery.Deserialized};sqliteQueries={parallelPageInQuery.SqliteQueries};blobMs={parallelPageInQuery.BlobMs:F2};deserializeMs={parallelPageInQuery.DeserializeMs:F2};copyMs={parallelPageInQuery.CopyMs:F2}"));
    }
}

WriteCsv(csvPath, results);
WriteMarkdown(mdPath, dbPath, results);
Console.WriteLine(File.ReadAllText(mdPath));
return 0;

static T Bench<T>(string name, Func<T> action, out double elapsedMs)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    Stopwatch stopwatch = Stopwatch.StartNew();
    T result = action();
    stopwatch.Stop();
    elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
    Console.WriteLine($"{name}: {elapsedMs:N2} ms");
    return result;
}

static SqliteConnection OpenReadOnly(string dbPath)
{
    SqliteConnectionStringBuilder builder = new()
    {
        DataSource = dbPath,
        Mode = SqliteOpenMode.ReadOnly,
        Cache = SqliteCacheMode.Shared
    };
    SqliteConnection connection = new(builder.ConnectionString);
    connection.Open();
    using SqliteCommand pragma = connection.CreateCommand();
    pragma.CommandText = "PRAGMA query_only = ON; PRAGMA temp_store = MEMORY; PRAGMA mmap_size = 268435456;";
    pragma.ExecuteNonQuery();
    return connection;
}

static long ExecuteScalarLong(SqliteConnection connection, string sql)
{
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = sql;
    object? value = command.ExecuteScalar();
    return Convert.ToInt64(value, CultureInfo.InvariantCulture);
}

static List<MapRow> ScanPositions(SqliteConnection connection)
{
    List<MapRow> rows = new();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT position, length(data) FROM mappiece ORDER BY position";
    using SqliteDataReader reader = command.ExecuteReader();
    while (reader.Read())
    {
        ulong position = unchecked((ulong)reader.GetInt64(0));
        int dataBytes = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
        FastCoord coord = DecodeChunkIndex(position);
        rows.Add(new MapRow(position, coord.X, coord.Z, dataBytes));
    }

    return rows;
}

static Dictionary<PageKey, List<MapRow>> BuildPageIndex(IEnumerable<MapRow> rows)
{
    Dictionary<PageKey, List<MapRow>> pages = new();
    foreach (MapRow row in rows)
    {
        PageKey pageKey = new(FloorDiv(row.X, ChunksPerPage), FloorDiv(row.Z, ChunksPerPage));
        if (!pages.TryGetValue(pageKey, out List<MapRow>? pageRows))
        {
            pageRows = new List<MapRow>();
            pages.Add(pageKey, pageRows);
        }

        pageRows.Add(row);
    }

    return pages;
}

static FullScanDecodeResult FullScanDecodeGroup(SqliteConnection connection)
{
    int rows = 0;
    int deserialized = 0;
    long pixels = 0;
    double blobMs = 0;
    double deserializeMs = 0;
    HashSet<PageKey> pages = new();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT position, data FROM mappiece ORDER BY position";
    using SqliteDataReader reader = command.ExecuteReader();
    while (reader.Read())
    {
        rows++;
        ulong position = unchecked((ulong)reader.GetInt64(0));
        long blobStart = Stopwatch.GetTimestamp();
        byte[] data = (byte[])reader["data"];
        blobMs += Stopwatch.GetElapsedTime(blobStart).TotalMilliseconds;
        long deserializeStart = Stopwatch.GetTimestamp();
        MapPieceDb? piece = Deserialize(data);
        deserializeMs += Stopwatch.GetElapsedTime(deserializeStart).TotalMilliseconds;
        if (piece?.Pixels == null)
        {
            continue;
        }

        FastCoord coord = DecodeChunkIndex(position);
        pages.Add(new PageKey(FloorDiv(coord.X, ChunksPerPage), FloorDiv(coord.Z, ChunksPerPage)));
        deserialized++;
        pixels += piece.Pixels.Length;
    }

    return new FullScanDecodeResult(rows, deserialized, pages.Count, pixels, blobMs, deserializeMs);
}

static PageAssembleResult PointProbeKnownPages(SqliteConnection connection, List<PageKey> pageKeys, HashSet<ulong> knownPositions)
{
    int rows = 0;
    int deserialized = 0;
    long pixels = 0;
    int sqliteQueries = 0;
    double blobMs = 0;
    double deserializeMs = 0;
    double copyMs = 0;
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT data FROM mappiece WHERE position = $position";
    SqliteParameter positionParameter = command.Parameters.Add("$position", SqliteType.Integer);
    command.Prepare();

    foreach (PageKey pageKey in pageKeys)
    {
        int[] pagePixels = new int[PagePixels];
        for (int dz = 0; dz < ChunksPerPage; dz++)
        {
            for (int dx = 0; dx < ChunksPerPage; dx++)
            {
                int chunkX = pageKey.X * ChunksPerPage + dx;
                int chunkZ = pageKey.Z * ChunksPerPage + dz;
                ulong position = EncodeChunkIndex(chunkX, chunkZ);
                if (!knownPositions.Contains(position))
                {
                    continue;
                }

                sqliteQueries++;
                positionParameter.Value = unchecked((long)position);
                using SqliteDataReader reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    continue;
                }

                rows++;
                long blobStart = Stopwatch.GetTimestamp();
                byte[] data = (byte[])reader["data"];
                blobMs += Stopwatch.GetElapsedTime(blobStart).TotalMilliseconds;
                long deserializeStart = Stopwatch.GetTimestamp();
                MapPieceDb? piece = Deserialize(data);
                deserializeMs += Stopwatch.GetElapsedTime(deserializeStart).TotalMilliseconds;
                if (piece?.Pixels == null)
                {
                    continue;
                }

                deserialized++;
                pixels += piece.Pixels.Length;
                long copyStart = Stopwatch.GetTimestamp();
                CopyTileIntoPage(piece.Pixels, pagePixels, dx, dz);
                copyMs += Stopwatch.GetElapsedTime(copyStart).TotalMilliseconds;
            }
        }
    }

    return new PageAssembleResult(pageKeys.Count, rows, deserialized, pixels, sqliteQueries, blobMs, deserializeMs, copyMs);
}

static PageAssembleResult PageInQuery(SqliteConnection connection, List<PageKey> pageKeys)
{
    using SqliteCommand pageCommand = CreatePageInQueryCommand(connection);
    int rows = 0;
    int deserialized = 0;
    long pixels = 0;
    int sqliteQueries = 0;
    double blobMs = 0;
    double deserializeMs = 0;
    double copyMs = 0;

    foreach (PageKey pageKey in pageKeys)
    {
        int[] pagePixels = new int[PagePixels];
        PageAssembleResult result = LoadPageInQuery(pageCommand, pageKey, pagePixels);
        rows += result.Rows;
        deserialized += result.Deserialized;
        pixels += result.Pixels;
        sqliteQueries += result.SqliteQueries;
        blobMs += result.BlobMs;
        deserializeMs += result.DeserializeMs;
        copyMs += result.CopyMs;
    }

    return new PageAssembleResult(pageKeys.Count, rows, deserialized, pixels, sqliteQueries, blobMs, deserializeMs, copyMs);
}

static PageAssembleResult PageInQueryParallel(string dbPath, List<PageKey> pageKeys, int degree)
{
    ConcurrentBag<PageAssembleResult> results = new();
    Parallel.ForEach(
        Partitioner.Create(pageKeys, true),
        new ParallelOptions { MaxDegreeOfParallelism = degree },
        () =>
        {
            SqliteConnection connection = OpenReadOnly(dbPath);
            return (Connection: connection, Command: CreatePageInQueryCommand(connection));
        },
        (pageKey, _, state) =>
        {
            int[] pagePixels = new int[PagePixels];
            results.Add(LoadPageInQuery(state.Command, pageKey, pagePixels));
            return state;
        },
        state =>
        {
            state.Command.Dispose();
            state.Connection.Dispose();
        });

    return new PageAssembleResult(
        pageKeys.Count,
        results.Sum(static result => result.Rows),
        results.Sum(static result => result.Deserialized),
        results.Sum(static result => result.Pixels),
        results.Sum(static result => result.SqliteQueries),
        results.Sum(static result => result.BlobMs),
        results.Sum(static result => result.DeserializeMs),
        results.Sum(static result => result.CopyMs));
}

static void CopyTileIntoPage(int[] tilePixels, int[] pagePixels, int chunkX, int chunkZ)
{
    int dstX = chunkX * ChunkSize;
    int dstY = chunkZ * ChunkSize;
    int pageSize = ChunksPerPage * ChunkSize;
    for (int row = 0; row < ChunkSize; row++)
    {
        Array.Copy(tilePixels, row * ChunkSize, pagePixels, (dstY + row) * pageSize + dstX, ChunkSize);
    }
}

static MapPieceDb? Deserialize(byte[] data)
{
    using MemoryStream stream = new(data);
    return Serializer.Deserialize<MapPieceDb>(stream);
}

static FastCoord DecodeChunkIndex(ulong position)
{
    return new FastCoord(DecodeSigned27(position & CoordMask), DecodeSigned27((position >> 27) & CoordMask));
}

static ulong EncodeChunkIndex(int x, int z)
{
    return ((ulong)x & CoordMask) | (((ulong)z & CoordMask) << 27);
}

static int DecodeSigned27(ulong value)
{
    long signed = (long)(value & CoordMask);
    if ((value & SignBit) != 0)
    {
        signed -= 1L << 27;
    }

    return checked((int)signed);
}

static int FloorDiv(int value, int divisor)
{
    int quotient = value / divisor;
    int remainder = value % divisor;
    return remainder != 0 && ((remainder < 0) != (divisor < 0)) ? quotient - 1 : quotient;
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    Dictionary<string, string> options = new(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < args.Length; i++)
    {
        string arg = args[i];
        if (!arg.StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        string key = arg[2..];
        if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            options[key] = args[++i];
        }
        else
        {
            options[key] = "true";
        }
    }

    return options;
}

static int[] ParseParallelDegrees(Dictionary<string, string> options)
{
    if (!options.TryGetValue("parallel-degrees", out string? configuredDegrees) || string.IsNullOrWhiteSpace(configuredDegrees))
    {
        return new[] { Math.Max(1, Environment.ProcessorCount - 1) };
    }

    List<int> degrees = new();
    foreach (string part in configuredDegrees.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int degree))
        {
            degrees.Add(Math.Clamp(degree, 1, 128));
        }
    }

    return degrees.Count == 0 ? new[] { Math.Max(1, Environment.ProcessorCount - 1) } : degrees.Distinct().Order().ToArray();
}

static string SanitizeFileName(string value)
{
    StringBuilder builder = new(value.Length);
    foreach (char ch in value)
    {
        builder.Append(Path.GetInvalidFileNameChars().Contains(ch) ? '-' : ch);
    }

    return builder.ToString().Replace(' ', '-');
}

static void WriteCsv(string path, List<ResultRow> results)
{
    using StreamWriter writer = new(path, false, Encoding.UTF8);
    writer.WriteLine("world,stage,scenario,operations,rowsOrBytes,totalMs,msPerOperation,pages,detail");
    foreach (ResultRow result in results)
    {
        writer.WriteLine(string.Join(
            ',',
            Csv(result.World),
            Csv(result.Stage),
            Csv(result.Scenario),
            result.Operations.ToString(CultureInfo.InvariantCulture),
            result.RowsOrBytes.ToString(CultureInfo.InvariantCulture),
            result.TotalMs.ToString("F4", CultureInfo.InvariantCulture),
            result.MsPerOperation.ToString("F6", CultureInfo.InvariantCulture),
            result.Pages.ToString(CultureInfo.InvariantCulture),
            Csv(result.Detail)));
    }
}

static void WriteMarkdown(string path, string dbPath, List<ResultRow> results)
{
    using StreamWriter writer = new(path, false, Encoding.UTF8);
    writer.WriteLine($"# FastMap MapDB Benchmark - {results[0].World}");
    writer.WriteLine();
    writer.WriteLine($"- DB: `{dbPath}`");
    writer.WriteLine($"- Generated: `{DateTime.Now:O}`");
    writer.WriteLine();
    writer.WriteLine("| Stage | Scenario | Operations | Rows/Bytes | Total ms | ms/op | Pages | Detail |");
    writer.WriteLine("|---|---:|---:|---:|---:|---:|---:|---|");
    foreach (ResultRow result in results)
    {
        writer.WriteLine($"| {EscapeMd(result.Stage)} | {EscapeMd(result.Scenario)} | {result.Operations} | {result.RowsOrBytes} | {result.TotalMs:F2} | {result.MsPerOperation:F4} | {result.Pages} | {EscapeMd(result.Detail)} |");
    }
}

static string Csv(string value)
{
    return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

static string EscapeMd(string value)
{
    return value.Replace("|", "\\|", StringComparison.Ordinal);
}

static SqliteCommand CreatePageInQueryCommand(SqliteConnection connection)
{
    SqliteCommand command = connection.CreateCommand();
    StringBuilder sql = new("SELECT position, data FROM mappiece WHERE position IN (");
    for (int i = 0; i < NativeDbQueryBatchSize; i++)
    {
        if (i > 0)
        {
            sql.Append(',');
        }

        sql.Append("$p");
        sql.Append(i.ToString(CultureInfo.InvariantCulture));
        command.Parameters.Add($"$p{i}", SqliteType.Integer);
    }

    sql.Append(')');
    command.CommandText = sql.ToString();
    command.Prepare();
    return command;
}

static PageAssembleResult LoadPageInQuery(SqliteCommand command, PageKey pageKey, int[] pagePixels)
{
    int rows = 0;
    int deserialized = 0;
    long pixels = 0;
    double blobMs = 0;
    double deserializeMs = 0;
    double copyMs = 0;
    int sqliteQueries = 0;

    for (int start = 0; start < ChunksPerPage * ChunksPerPage; start += NativeDbQueryBatchSize)
    {
        for (int i = 0; i < NativeDbQueryBatchSize; i++)
        {
            int localIndex = start + i;
            if (localIndex < ChunksPerPage * ChunksPerPage)
            {
                int dx = localIndex % ChunksPerPage;
                int dz = localIndex / ChunksPerPage;
                int chunkX = pageKey.X * ChunksPerPage + dx;
                int chunkZ = pageKey.Z * ChunksPerPage + dz;
                command.Parameters[i].Value = unchecked((long)EncodeChunkIndex(chunkX, chunkZ));
            }
            else
            {
                command.Parameters[i].Value = DBNull.Value;
            }
        }

        sqliteQueries++;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows++;
            ulong position = unchecked((ulong)reader.GetInt64(0));
            FastCoord coord = DecodeChunkIndex(position);
            int dx = coord.X - pageKey.X * ChunksPerPage;
            int dz = coord.Z - pageKey.Z * ChunksPerPage;
            if ((uint)dx >= ChunksPerPage || (uint)dz >= ChunksPerPage)
            {
                continue;
            }

            long blobStart = Stopwatch.GetTimestamp();
            byte[] data = (byte[])reader["data"];
            blobMs += Stopwatch.GetElapsedTime(blobStart).TotalMilliseconds;
            long deserializeStart = Stopwatch.GetTimestamp();
            MapPieceDb? piece = Deserialize(data);
            deserializeMs += Stopwatch.GetElapsedTime(deserializeStart).TotalMilliseconds;
            if (piece?.Pixels == null)
            {
                continue;
            }

            deserialized++;
            pixels += piece.Pixels.Length;
            long copyStart = Stopwatch.GetTimestamp();
            CopyTileIntoPage(piece.Pixels, pagePixels, dx, dz);
            copyMs += Stopwatch.GetElapsedTime(copyStart).TotalMilliseconds;
        }
    }

    return new PageAssembleResult(1, rows, deserialized, pixels, sqliteQueries, blobMs, deserializeMs, copyMs);
}

[ProtoContract]
public sealed class MapPieceDb
{
    [ProtoMember(1)]
    public int[]? Pixels { get; set; }
}

readonly record struct FastCoord(int X, int Z);
readonly record struct PageKey(int X, int Z);
readonly record struct MapRow(ulong Position, int X, int Z, int DataBytes);
readonly record struct FullScanDecodeResult(int Rows, int Deserialized, int Pages, long Pixels, double BlobMs, double DeserializeMs);
readonly record struct PageAssembleResult(int Pages, int Rows, int Deserialized, long Pixels, int SqliteQueries, double BlobMs, double DeserializeMs, double CopyMs);
readonly record struct ResultRow(string World, string Stage, string Scenario, long Operations, long RowsOrBytes, double TotalMs, double MsPerOperation, int Pages, string Detail);
