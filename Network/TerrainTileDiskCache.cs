using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FastMap.Network;

// Only the index is consulted on the game thread. File reads have their own
// bounded worker, independent of cold generation; writes use the tile worker.
internal sealed class TerrainTileDiskCache : IDisposable
{
    private const int Magic = 0x31544d46; // FMT1
    private const int MaxFiles = 16384;
    private readonly string root, directory;
    private readonly long limit;
    private readonly Action<string>? log;
    private readonly object ioLock = new();
    private readonly ConcurrentDictionary<(int, int, int), long> index = new();
    private readonly Dictionary<string, (long Size, DateTime Used)> files = new();
    private readonly BlockingCollection<Action> reads = new(32);
    private readonly Thread thread;
    private long bytes;
    private volatile bool ready, disabled;
    private bool reportedFailure;
    private int disposed;
    public bool Ready => ready;
    public int CachedPages => index.Count;
    public long Bytes => Interlocked.Read(ref bytes);

    // root is a dedicated per-world cache directory; identity is a SHA-256 hash.
    public TerrainTileDiskCache(string root, string identity, int megabytes = 1024, Action<string>? log = null)
    {
        if (!TerrainRenderingIdentity.IsValid(identity)) throw new ArgumentException("Invalid cache identity", nameof(identity));
        this.root = Path.GetFullPath(root);
        directory = Path.Combine(this.root, identity);
        limit = Math.Clamp(megabytes, 1, 16384) * 1024L * 1024;
        this.log = log;
        thread = new Thread(() =>
        {
            Initialize();
            foreach (var read in reads.GetConsumingEnumerable()) read();
        }) { IsBackground = true, Name = "FastMap tile disk cache" };
        thread.Start();
    }

    private static (int, int, int) Key(TerrainTileRequest r) => (r.PageX, r.PageZ, r.Step);
    private string PathFor(TerrainTileRequest r) => Path.Combine(directory, $"{r.PageX}_{r.PageZ}_{r.Step}.fmt");
    public long Size(TerrainTileRequest r) => index.TryGetValue(Key(r), out long size) ? size : 0;

    private void Initialize()
    {
        try
        {
            Directory.CreateDirectory(directory);
            // Include obsolete identities in the per-world disk budget. Only
            // FastMap's own tile files in hash-named namespaces are managed.
            foreach (string folder in Directory.EnumerateDirectories(root))
            {
                if (!TerrainRenderingIdentity.IsValid(Path.GetFileName(folder))) continue;
                foreach (string path in Directory.EnumerateFiles(folder, "*.fmt"))
                {
                    var file = new FileInfo(path);
                    files[path] = (file.Length, file.LastWriteTimeUtc);
                    bytes += file.Length;
                    if (folder == directory && TryKey(path, out var key)
                        && file.Length >= 130 && file.Length <= 3L * ((1024 + key.Item3 - 1) / key.Item3)
                            * ((1024 + key.Item3 - 1) / key.Item3) * 4 + 256) index[key] = file.Length;
                }
                // Incomplete atomic writes have no usable tile and can be discarded.
                foreach (string path in Directory.EnumerateFiles(folder, "*.fmt.tmp")) File.Delete(path);
            }
            Trim(0, null);
            log?.Invoke($"tile disk cache ready pages={CachedPages} bytes={Bytes} path={directory}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            disabled = true;
            index.Clear();
            ReportFailure(ex);
        }
        finally { ready = true; }
    }

    private static bool TryKey(string path, out (int, int, int) key)
    {
        key = default;
        var parts = Path.GetFileNameWithoutExtension(path).Split('_');
        if (parts.Length != 3 || !int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int z)
            || !int.TryParse(parts[2], out int step) || x < 0 || z < 0 || step < 1 || step > 32) return false;
        key = (x, z, step);
        return true;
    }

    public bool TryRead(TerrainTileRequest request, out Task<EncodedTerrainTile[]?> task)
    {
        var completion = new TaskCompletionSource<EncodedTerrainTile[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        task = completion.Task;
        long reservedSize = Size(request);
        if (!ready || disabled || reservedSize == 0) return false;
        return reads.TryAdd(() => completion.SetResult(Load(request, reservedSize)));
    }

    private EncodedTerrainTile[]? Load(TerrainTileRequest request, long reservedSize)
    {
        lock (ioLock)
        {
            string path = PathFor(request);
            try
            {
                using var stream = File.OpenRead(path);
                int rawLength = checked(request.Width * request.Width * 4);
                if (stream.Length > reservedSize || stream.Length > 3L * rawLength + 256)
                    throw new InvalidDataException("Tile exceeds its memory reservation");
                using var reader = new BinaryReader(stream);
                if (reader.ReadInt32() != Magic || reader.ReadInt32() != request.PageX
                    || reader.ReadInt32() != request.PageZ || reader.ReadInt32() != request.Step)
                    throw new InvalidDataException("Invalid tile header");
                var tiles = new EncodedTerrainTile[3];
                for (int style = 0; style < tiles.Length; style++)
                {
                    byte compressed = reader.ReadByte();
                    int length = reader.ReadInt32();
                    if (compressed > 1 || length < 1 || length > rawLength || (compressed == 0 && length != rawLength)
                        || length + 32L > stream.Length - stream.Position)
                        throw new InvalidDataException("Invalid tile payload length");
                    byte[] checksum = reader.ReadBytes(32);
                    byte[] data = reader.ReadBytes(length);
                    if (!Checksum(compressed, data).AsSpan().SequenceEqual(checksum))
                        throw new InvalidDataException("Tile checksum mismatch");
                    tiles[style] = new EncodedTerrainTile(data, compressed == 1);
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing tile data");
                // Persist access time for eviction order across restarts. Failure
                // to update it must not prevent serving an otherwise valid tile.
                if (files.TryGetValue(path, out var file)) files[path] = (file.Size, DateTime.UtcNow);
                try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ReportFailure(ex); }
                return tiles;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                index.TryRemove(Key(request), out _);
                try { Remove(path); }
                catch (Exception deleteError) when (deleteError is IOException or UnauthorizedAccessException) { }
                ReportFailure(ex);
                return null;
            }
        }
    }

    private static byte[] Checksum(byte compressed, byte[] data)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(new[] { compressed });
        hash.AppendData(data);
        return hash.GetHashAndReset();
    }

    public void Store(TerrainTileRequest request, EncodedTerrainTile[] tiles, bool prewarm = false)
    {
        if (!ready || disabled) return;
        lock (ioLock)
        {
            string path = PathFor(request), temp = path + ".tmp";
            try
            {
                long size = 16 + tiles.Sum(t => 37L + t.Data.Length);
                if (size > limit) return;
                if (!Trim(size, path, preserveCurrent: prewarm)) return;
                using (var stream = File.Create(temp))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(Magic); writer.Write(request.PageX); writer.Write(request.PageZ); writer.Write(request.Step);
                    foreach (var tile in tiles)
                    {
                        writer.Write((byte)(tile.Compressed ? 1 : 0)); writer.Write(tile.Data.Length);
                        writer.Write(Checksum((byte)(tile.Compressed ? 1 : 0), tile.Data)); writer.Write(tile.Data);
                    }
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, path, overwrite: true);
                if (files.TryGetValue(path, out var previous)) bytes -= previous.Size;
                files[path] = (size, DateTime.UtcNow);
                bytes += size;
                index[Key(request)] = size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ReportFailure(ex);
                try { File.Delete(temp); }
                catch (Exception deleteError) when (deleteError is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private bool Trim(long needed, string? replacing, bool preserveCurrent = false)
    {
        long replacedSize = replacing != null && files.TryGetValue(replacing, out var previous) ? previous.Size : 0;
        foreach (var file in files.OrderBy(f => f.Value.Used).ToArray())
        {
            if (bytes - replacedSize + needed <= limit && files.Count < MaxFiles) break;
            if (file.Key != replacing && (!preserveCurrent || Path.GetDirectoryName(file.Key) != directory)) Remove(file.Key);
        }
        return bytes - replacedSize + needed <= limit && files.Count < MaxFiles;
    }

    private void Remove(string path)
    {
        File.Delete(path);
        if (files.Remove(path, out var file)) bytes -= file.Size;
        if (Path.GetDirectoryName(path) == directory && TryKey(path, out var key)) index.TryRemove(key, out _);
    }

    private void ReportFailure(Exception ex)
    {
        if (reportedFailure) return;
        reportedFailure = true;
        log?.Invoke($"tile disk cache could not use a file; affected tiles will be regenerated: {ex.Message}");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        reads.CompleteAdding();
        thread.Join();
        reads.Dispose();
    }
}
