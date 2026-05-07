using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using K4os.Compression.LZ4;
using Vintagestory.API.MathTools;

namespace FastMap.Map;

internal static class FastMapPagePayloadCodec
{
    private const int Magic = 0x53504d46; // FMPS
    private const int Version = 1;
    private const int ChunksPerPage = FastMapPageComponent.ChunksPerPage;
    private const int ChunkSize = FastMapPageComponent.ChunkSize;
    private const int ChunkPixelCount = ChunkSize * ChunkSize;
    private const int ChunkByteCount = ChunkPixelCount * sizeof(int);
    private const int PixelCount = FastMapPageComponent.PixelCount;

    public static byte[] Encode(FastMapPageSnapshot snapshot, bool useHighCompression)
    {
        if (!snapshot.HasAnyValidChunks || snapshot.ValidRows.Length != ChunksPerPage || snapshot.Pixels.Length != PixelCount)
        {
            return Array.Empty<byte>();
        }

        byte[] rawChunkBytes = BuildSparseChunkPayload(snapshot, out int validChunkCount);
        byte[] compressedBytes = new byte[LZ4Codec.MaximumOutputSize(rawChunkBytes.Length)];
        LZ4Level compressionLevel = useHighCompression ? LZ4Level.L09_HC : LZ4Level.L00_FAST;
        int compressedLength = LZ4Codec.Encode(rawChunkBytes, 0, rawChunkBytes.Length, compressedBytes, 0, compressedBytes.Length, compressionLevel);
        if (compressedLength <= 0)
        {
            return Array.Empty<byte>();
        }

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write(Magic);
        writer.Write(Version);
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
        writer.Flush();
        return stream.ToArray();
    }

    public static bool TryDecode(byte[] payload, out FastMapPageSnapshot snapshot)
    {
        snapshot = null!;
        if (payload.Length == 0)
        {
            return false;
        }

        try
        {
            using MemoryStream stream = new(payload, writable: false);
            using BinaryReader reader = new(stream);

            if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version)
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

            if (chunksPerPage != ChunksPerPage || chunkSize != ChunkSize)
            {
                return false;
            }

            if (validChunkCount < 0 || validChunkCount > ChunksPerPage * ChunksPerPage || rawByteCount != validChunkCount * ChunkByteCount || compressedByteCount <= 0)
            {
                return false;
            }

            uint[] validRows = new uint[ChunksPerPage];
            for (int i = 0; i < validRows.Length; i++)
            {
                validRows[i] = reader.ReadUInt32();
            }

            if (validChunkCount != CountValidChunks(validRows))
            {
                return false;
            }

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
            snapshot = new FastMapPageSnapshot(new FastVec2i(pageX, pageY), validRows, pixels);
            return snapshot.HasAnyValidChunks;
        }
        catch
        {
            return false;
        }
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
            pixels.AsSpan((sourceY + row) * FastMapPageComponent.PageSize + sourceX, ChunkSize).CopyTo(chunkBuffer.Slice(row * ChunkSize, ChunkSize));
        }
    }

    private static void CopyChunkToPage(ReadOnlySpan<int> chunkPixels, int[] pixels, int localX, int localZ)
    {
        int destinationX = localX * ChunkSize;
        int destinationY = localZ * ChunkSize;
        for (int row = 0; row < ChunkSize; row++)
        {
            chunkPixels.Slice(row * ChunkSize, ChunkSize).CopyTo(pixels.AsSpan((destinationY + row) * FastMapPageComponent.PageSize + destinationX, ChunkSize));
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
}
