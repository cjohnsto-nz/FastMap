using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using K4os.Compression.LZ4;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace FastMap.Map;

internal sealed class FastMapTerrainFallbackDiskCache
{
    private const int Magic = 0x31464d46; // FMF1
    private const int Version = 1;
    private const int ChunksPerPage = FastMapPageComponent.ChunksPerPage;

    private readonly string rootPath;
    private readonly int resolutionScale;
    private readonly bool useHighCompression;
    private readonly object knownPageFilesLock = new();
    private readonly HashSet<FastVec2i> knownPageFiles = new();

    public FastMapTerrainFallbackDiskCache(string savegameIdentifier, int resolutionScale, bool useHighCompression)
    {
        this.resolutionScale = Math.Clamp(resolutionScale, 1, 32);
        this.useHighCompression = useHighCompression;
        string worldPath = Path.Combine(GamePaths.DataPath, "FastMap", SanitizePathPart(savegameIdentifier));
        rootPath = Path.Combine(worldPath, $"terrain-fallback-v1-r{this.resolutionScale}");
        GamePaths.EnsurePathExists(rootPath);
        IndexExistingPages();
    }

    public string RootPath => rootPath;

    public bool MightContain(FastVec2i pageKey)
    {
        lock (knownPageFilesLock)
        {
            return knownPageFiles.Contains(pageKey);
        }
    }

    public bool TryLoad(FastVec2i pageKey, out FastMapPageSnapshot snapshot)
    {
        snapshot = null!;
        string path = GetPath(pageKey);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using BinaryReader reader = new(stream);

            if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version)
            {
                return false;
            }

            int pageX = reader.ReadInt32();
            int pageY = reader.ReadInt32();
            int savedResolutionScale = reader.ReadInt32();
            int lowResolutionSize = reader.ReadInt32();
            int rawByteCount = reader.ReadInt32();
            int compressedByteCount = reader.ReadInt32();

            if (pageX != pageKey.X
                || pageY != pageKey.Y
                || savedResolutionScale != resolutionScale
                || lowResolutionSize != LowResolutionSize(resolutionScale)
                || rawByteCount != lowResolutionSize * lowResolutionSize * sizeof(int)
                || compressedByteCount <= 0)
            {
                return false;
            }

            byte[] compressedBytes = reader.ReadBytes(compressedByteCount);
            if (compressedBytes.Length != compressedByteCount)
            {
                return false;
            }

            byte[] rawBytes = new byte[rawByteCount];
            int decodedLength = LZ4Codec.Decode(compressedBytes, 0, compressedBytes.Length, rawBytes, 0, rawBytes.Length);
            if (decodedLength != rawByteCount)
            {
                return false;
            }

            int[] lowResolutionPixels = new int[lowResolutionSize * lowResolutionSize];
            MemoryMarshal.Cast<byte, int>(rawBytes).CopyTo(lowResolutionPixels);
            snapshot = CreateSnapshot(pageKey, lowResolutionPixels, resolutionScale);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Save(FastVec2i pageKey, int[] lowResolutionPixels)
    {
        int lowResolutionSize = LowResolutionSize(resolutionScale);
        if (lowResolutionPixels.Length != lowResolutionSize * lowResolutionSize)
        {
            return;
        }

        string path = GetPath(pageKey);
        string tmpPath = path + ".tmp";
        byte[] rawBytes = MemoryMarshal.AsBytes(lowResolutionPixels.AsSpan()).ToArray();
        byte[] compressedBytes = new byte[LZ4Codec.MaximumOutputSize(rawBytes.Length)];
        LZ4Level compressionLevel = useHighCompression ? LZ4Level.L09_HC : LZ4Level.L00_FAST;
        int compressedLength = LZ4Codec.Encode(rawBytes, 0, rawBytes.Length, compressedBytes, 0, compressedBytes.Length, compressionLevel);
        if (compressedLength <= 0)
        {
            return;
        }

        using (FileStream stream = File.Open(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (BinaryWriter writer = new(stream))
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(pageKey.X);
            writer.Write(pageKey.Y);
            writer.Write(resolutionScale);
            writer.Write(lowResolutionSize);
            writer.Write(rawBytes.Length);
            writer.Write(compressedLength);
            writer.Write(compressedBytes, 0, compressedLength);
        }

        File.Move(tmpPath, path, overwrite: true);
        MarkPageFileKnown(pageKey);
    }

    public static int LowResolutionSize(int resolutionScale)
    {
        return (FastMapPageComponent.PageSize + resolutionScale - 1) / resolutionScale;
    }

    public static int[] Expand(int[] lowResolutionPixels, int lowResolutionSize, int resolutionScale)
    {
        int[] pixels = new int[FastMapPageComponent.PixelCount];
        for (int cellZ = 0; cellZ < lowResolutionSize; cellZ++)
        {
            int localZ = cellZ * resolutionScale;
            int maxZ = Math.Min(localZ + resolutionScale, FastMapPageComponent.PageSize);
            for (int cellX = 0; cellX < lowResolutionSize; cellX++)
            {
                int color = lowResolutionPixels[cellZ * lowResolutionSize + cellX];
                int localX = cellX * resolutionScale;
                int maxX = Math.Min(localX + resolutionScale, FastMapPageComponent.PageSize);
                for (int z = localZ; z < maxZ; z++)
                {
                    int offset = z * FastMapPageComponent.PageSize;
                    for (int x = localX; x < maxX; x++)
                    {
                        pixels[offset + x] = color;
                    }
                }
            }
        }

        return pixels;
    }

    public static FastMapPageSnapshot CreateSnapshot(FastVec2i pageKey, int[] pixels, int resolutionScale)
    {
        uint[] validRows = new uint[ChunksPerPage];
        for (int i = 0; i < validRows.Length; i++)
        {
            validRows[i] = uint.MaxValue;
        }

        return new FastMapPageSnapshot(pageKey, validRows, pixels, transferPixelsToPage: true, synthetic: true, resolutionScale: resolutionScale);
    }

    private void IndexExistingPages()
    {
        if (!Directory.Exists(rootPath))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(rootPath, "*.fmf"))
        {
            if (TryParsePageKey(Path.GetFileNameWithoutExtension(file), out FastVec2i pageKey))
            {
                MarkPageFileKnown(pageKey);
            }
        }
    }

    private void MarkPageFileKnown(FastVec2i pageKey)
    {
        lock (knownPageFilesLock)
        {
            knownPageFiles.Add(pageKey);
        }
    }

    private string GetPath(FastVec2i pageKey)
    {
        return Path.Combine(rootPath, pageKey.X + "_" + pageKey.Y + ".fmf");
    }

    private static bool TryParsePageKey(string? fileName, out FastVec2i pageKey)
    {
        pageKey = default;
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        int separator = fileName.IndexOf('_');
        if (separator <= 0 || separator >= fileName.Length - 1)
        {
            return false;
        }

        if (!int.TryParse(fileName.AsSpan(0, separator), out int x)
            || !int.TryParse(fileName.AsSpan(separator + 1), out int y))
        {
            return false;
        }

        pageKey = new FastVec2i(x, y);
        return true;
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
