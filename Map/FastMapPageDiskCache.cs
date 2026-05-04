using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using K4os.Compression.LZ4;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace FastMap.Map;

internal sealed class FastMapPageDiskCache
{
    private const int V1Magic = 0x31504d46; // FMP1
    private const int V1Version = 1;
    private const int V2Magic = 0x32504d46; // FMP2
    private const int V2Version = 2;
    private const int ChunksPerPage = 32;
    private const int ChunkSize = 32;
    private const int ChunkPixelCount = ChunkSize * ChunkSize;
    private const int ChunkByteCount = ChunkPixelCount * sizeof(int);
    private const int PageSize = 1024;
    private const int PixelCount = PageSize * PageSize;

    private readonly string rootPath;
    private readonly string v1RootPath;
    private readonly bool enableCompression;

    public FastMapPageDiskCache(string savegameIdentifier, bool enableCompression)
    {
        this.enableCompression = enableCompression;
        string worldPath = Path.Combine(GamePaths.DataPath, "FastMap", SanitizePathPart(savegameIdentifier));
        v1RootPath = Path.Combine(worldPath, "pages-v1");
        rootPath = enableCompression ? Path.Combine(worldPath, "pages-v2") : v1RootPath;
        GamePaths.EnsurePathExists(rootPath);
    }

    public string RootPath => rootPath;

    public bool TryLoad(FastVec2i pageKey, out FastMapPageSnapshot snapshot)
    {
        return TryLoadV2(pageKey, out snapshot) || TryLoadV1(pageKey, out snapshot);
    }

    public void Save(FastMapPageSnapshot snapshot)
    {
        if (!snapshot.HasAnyValidChunks || snapshot.ValidRows.Length != ChunksPerPage || snapshot.Pixels.Length != PixelCount)
        {
            return;
        }

        if (!enableCompression)
        {
            SaveV1(snapshot);
            return;
        }

        string path = GetPath(rootPath, snapshot.PageKey);
        string tmpPath = path + ".tmp";
        byte[] rawChunkBytes = BuildSparseChunkPayload(snapshot, out int validChunkCount);
        byte[] compressedBytes = new byte[LZ4Codec.MaximumOutputSize(rawChunkBytes.Length)];
        int compressedLength = LZ4Codec.Encode(rawChunkBytes, 0, rawChunkBytes.Length, compressedBytes, 0, compressedBytes.Length);
        if (compressedLength <= 0)
        {
            return;
        }

        using (FileStream stream = File.Open(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (BinaryWriter writer = new(stream))
        {
            writer.Write(V2Magic);
            writer.Write(V2Version);
            writer.Write(snapshot.PageKey.X);
            writer.Write(snapshot.PageKey.Y);
            writer.Write(ChunksPerPage);
            writer.Write(ChunkSize);
            writer.Write(validChunkCount);
            writer.Write(rawChunkBytes.Length);
            writer.Write(compressedLength);

            for (int i = 0; i < snapshot.ValidRows.Length; i++)
            {
                writer.Write(snapshot.ValidRows[i]);
            }

            writer.Write(compressedBytes, 0, compressedLength);
        }

        File.Move(tmpPath, path, overwrite: true);
    }

    private void SaveV1(FastMapPageSnapshot snapshot)
    {
        string path = GetPath(v1RootPath, snapshot.PageKey);
        string tmpPath = path + ".tmp";

        using (FileStream stream = File.Open(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (BinaryWriter writer = new(stream))
        {
            writer.Write(V1Magic);
            writer.Write(V1Version);
            writer.Write(snapshot.PageKey.X);
            writer.Write(snapshot.PageKey.Y);
            writer.Write(ChunksPerPage);
            writer.Write(snapshot.Pixels.Length);

            for (int i = 0; i < snapshot.ValidRows.Length; i++)
            {
                writer.Write(snapshot.ValidRows[i]);
            }

            writer.Write(MemoryMarshal.AsBytes(snapshot.Pixels.AsSpan()));
        }

        File.Move(tmpPath, path, overwrite: true);
    }

    private bool TryLoadV2(FastVec2i pageKey, out FastMapPageSnapshot snapshot)
    {
        snapshot = null!;
        string path = GetPath(rootPath, pageKey);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using BinaryReader reader = new(stream);

            if (reader.ReadInt32() != V2Magic || reader.ReadInt32() != V2Version)
            {
                return false;
            }

            int pageX = reader.ReadInt32();
            int pageY = reader.ReadInt32();
            int chunksPerPage = reader.ReadInt32();
            int chunkSize = reader.ReadInt32();
            int validChunkCount = reader.ReadInt32();
            int rawByteCount = reader.ReadInt32();
            int compressedByteCount = reader.ReadInt32();

            if (pageX != pageKey.X || pageY != pageKey.Y || chunksPerPage != ChunksPerPage || chunkSize != ChunkSize)
            {
                return false;
            }

            if (validChunkCount < 0 || validChunkCount > ChunksPerPage * ChunksPerPage || rawByteCount != validChunkCount * ChunkByteCount || compressedByteCount <= 0)
            {
                return false;
            }

            uint[] validRows = ReadValidRows(reader);
            byte[] compressedBytes = reader.ReadBytes(compressedByteCount);
            if (compressedBytes.Length != compressedByteCount)
            {
                return false;
            }

            byte[] rawChunkBytes = new byte[rawByteCount];
            int decodedLength = LZ4Codec.Decode(compressedBytes, 0, compressedBytes.Length, rawChunkBytes, 0, rawChunkBytes.Length);
            if (decodedLength != rawByteCount)
            {
                return false;
            }

            int[] pixels = new int[PixelCount];
            RestoreSparseChunkPayload(validRows, rawChunkBytes, pixels);
            snapshot = new FastMapPageSnapshot(pageKey, validRows, pixels);
            return snapshot.HasAnyValidChunks;
        }
        catch
        {
            return false;
        }
    }

    private bool TryLoadV1(FastVec2i pageKey, out FastMapPageSnapshot snapshot)
    {
        snapshot = null!;
        string path = GetPath(v1RootPath, pageKey);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using BinaryReader reader = new(stream);

            if (reader.ReadInt32() != V1Magic || reader.ReadInt32() != V1Version)
            {
                return false;
            }

            int pageX = reader.ReadInt32();
            int pageY = reader.ReadInt32();
            int chunksPerPage = reader.ReadInt32();
            int pixelCount = reader.ReadInt32();

            if (pageX != pageKey.X || pageY != pageKey.Y || chunksPerPage != ChunksPerPage || pixelCount != PixelCount)
            {
                return false;
            }

            uint[] validRows = ReadValidRows(reader);

            int byteCount = PixelCount * sizeof(int);
            byte[] pixelBytes = reader.ReadBytes(byteCount);
            if (pixelBytes.Length != byteCount)
            {
                return false;
            }

            int[] pixels = new int[PixelCount];
            MemoryMarshal.Cast<byte, int>(pixelBytes).CopyTo(pixels);
            snapshot = new FastMapPageSnapshot(pageKey, validRows, pixels);
            return snapshot.HasAnyValidChunks;
        }
        catch
        {
            return false;
        }
    }

    private static uint[] ReadValidRows(BinaryReader reader)
    {
        uint[] validRows = new uint[ChunksPerPage];
        for (int i = 0; i < validRows.Length; i++)
        {
            validRows[i] = reader.ReadUInt32();
        }

        return validRows;
    }

    private static byte[] BuildSparseChunkPayload(FastMapPageSnapshot snapshot, out int validChunkCount)
    {
        validChunkCount = CountValidChunks(snapshot.ValidRows);
        byte[] payload = new byte[validChunkCount * ChunkByteCount];
        Span<int> chunkBuffer = stackalloc int[ChunkPixelCount];
        int offset = 0;

        for (int localZ = 0; localZ < ChunksPerPage; localZ++)
        {
            uint row = snapshot.ValidRows[localZ];
            if (row == 0)
            {
                continue;
            }

            for (int localX = 0; localX < ChunksPerPage; localX++)
            {
                if ((row & (1u << localX)) == 0)
                {
                    continue;
                }

                CopyChunkFromPage(snapshot.Pixels, localX, localZ, chunkBuffer);
                MemoryMarshal.AsBytes(chunkBuffer).CopyTo(payload.AsSpan(offset, ChunkByteCount));
                offset += ChunkByteCount;
            }
        }

        return payload;
    }

    private static void RestoreSparseChunkPayload(uint[] validRows, byte[] payload, int[] pixels)
    {
        ReadOnlySpan<int> chunkSource = MemoryMarshal.Cast<byte, int>(payload);
        int chunkIndex = 0;

        for (int localZ = 0; localZ < ChunksPerPage; localZ++)
        {
            uint row = validRows[localZ];
            if (row == 0)
            {
                continue;
            }

            for (int localX = 0; localX < ChunksPerPage; localX++)
            {
                if ((row & (1u << localX)) == 0)
                {
                    continue;
                }

                CopyChunkToPage(chunkSource.Slice(chunkIndex * ChunkPixelCount, ChunkPixelCount), pixels, localX, localZ);
                chunkIndex++;
            }
        }
    }

    private static void CopyChunkFromPage(int[] pixels, int localX, int localZ, Span<int> chunkBuffer)
    {
        int sourceX = localX * ChunkSize;
        int sourceY = localZ * ChunkSize;
        for (int row = 0; row < ChunkSize; row++)
        {
            pixels.AsSpan((sourceY + row) * PageSize + sourceX, ChunkSize).CopyTo(chunkBuffer.Slice(row * ChunkSize, ChunkSize));
        }
    }

    private static void CopyChunkToPage(ReadOnlySpan<int> chunkPixels, int[] pixels, int localX, int localZ)
    {
        int destinationX = localX * ChunkSize;
        int destinationY = localZ * ChunkSize;
        for (int row = 0; row < ChunkSize; row++)
        {
            chunkPixels.Slice(row * ChunkSize, ChunkSize).CopyTo(pixels.AsSpan((destinationY + row) * PageSize + destinationX, ChunkSize));
        }
    }

    private static int CountValidChunks(uint[] validRows)
    {
        int count = 0;
        for (int i = 0; i < validRows.Length; i++)
        {
            count += BitOperations.PopCount(validRows[i]);
        }

        return count;
    }

    private static string GetPath(string root, FastVec2i pageKey)
    {
        return Path.Combine(root, pageKey.X + "_" + pageKey.Y + ".fmp");
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
