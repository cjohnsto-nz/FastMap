using System;
using System.IO;
using System.Runtime.InteropServices;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace FastMap.Map;

internal sealed class FastMapPageDiskCache
{
    private const int Magic = 0x31504d46; // FMP1
    private const int Version = 1;
    private const int ChunksPerPage = 32;
    private const int PageSize = 1024;
    private const int PixelCount = PageSize * PageSize;

    private readonly string rootPath;

    public FastMapPageDiskCache(string savegameIdentifier)
    {
        rootPath = Path.Combine(GamePaths.DataPath, "FastMap", SanitizePathPart(savegameIdentifier), "pages-v1");
        GamePaths.EnsurePathExists(rootPath);
    }

    public string RootPath => rootPath;

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
            int chunksPerPage = reader.ReadInt32();
            int pixelCount = reader.ReadInt32();

            if (pageX != pageKey.X || pageY != pageKey.Y || chunksPerPage != ChunksPerPage || pixelCount != PixelCount)
            {
                return false;
            }

            uint[] validRows = new uint[ChunksPerPage];
            for (int i = 0; i < validRows.Length; i++)
            {
                validRows[i] = reader.ReadUInt32();
            }

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

    public void Save(FastMapPageSnapshot snapshot)
    {
        if (!snapshot.HasAnyValidChunks || snapshot.ValidRows.Length != ChunksPerPage || snapshot.Pixels.Length != PixelCount)
        {
            return;
        }

        string path = GetPath(snapshot.PageKey);
        string tmpPath = path + ".tmp";

        using (FileStream stream = File.Open(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (BinaryWriter writer = new(stream))
        {
            writer.Write(Magic);
            writer.Write(Version);
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

    private string GetPath(FastVec2i pageKey)
    {
        return Path.Combine(rootPath, pageKey.X + "_" + pageKey.Y + ".fmp");
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
