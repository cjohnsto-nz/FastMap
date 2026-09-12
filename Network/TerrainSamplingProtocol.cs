using System;
using System.IO;
using FastMap.Map;
using ProtoBuf;
using K4os.Compression.LZ4;

namespace FastMap.Network;

[ProtoContract]
public sealed class TerrainSamplingHello
{
    [ProtoMember(1)] public int Version { get; set; } = 6;
}

[ProtoContract]
public sealed class TerrainSamplingStatus
{
    [ProtoMember(1)] public int Version { get; set; } = 6;
    [ProtoMember(2)] public bool Available { get; set; }
    [ProtoMember(3)] public string Reason { get; set; } = "";
}

[ProtoContract]
public sealed class TerrainSamplingRequest
{
    [ProtoMember(1)] public int Id { get; set; }
    [ProtoMember(2)] public int X { get; set; }
    [ProtoMember(3)] public int Z { get; set; }
    [ProtoMember(4)] public int Width { get; set; }
    [ProtoMember(5)] public int Height { get; set; }
    [ProtoMember(6)] public int Step { get; set; }
    [ProtoMember(7)] public bool Cancel { get; set; }

    public bool IsValid(int sizeX, int sizeZ)
    {
        if (Id <= 0 || Step < 1 || Step > 32 || Width < 1 || Height < 1 || Width > 1025 || Height > 1025)
            return false;
        long spanX = (long)(Width - 1) * Step;
        long spanZ = (long)(Height - 1) * Step;
        // One map page plus its shading border. Edge samples are clamped to the world.
        return spanX <= 1056 && spanZ <= 1056 && X >= -32 && Z >= -32
            && X < sizeX && Z < sizeZ && (long)X + spanX < (long)sizeX + 32
            && (long)Z + spanZ < (long)sizeZ + 32;
    }
}

[ProtoContract]
public sealed class TerrainSamplingBatch
{
    [ProtoMember(1)] public int Id { get; set; }
    [ProtoMember(2)] public int Offset { get; set; }
    [ProtoMember(3)] public byte[] Data { get; set; } = Array.Empty<byte>();
    [ProtoMember(4)] public string Error { get; set; } = "";
    [ProtoMember(5)] public int Count { get; set; }
    [ProtoMember(6)] public bool Compressed { get; set; }
}

internal static class TerrainSamplingCodec
{
    public const int ColumnBytes = 25;
    public const int MaxBatchColumns = 1024;

    public static TerrainSamplingBatch Encode(byte[] raw, int offset)
    {
        if (raw.Length == 0 || raw.Length % ColumnBytes != 0 || raw.Length > MaxBatchColumns * ColumnBytes)
            throw new InvalidDataException("Invalid sample block size.");
        byte[] packed = new byte[LZ4Codec.MaximumOutputSize(raw.Length)];
        // Adjacent columns have similar fields. Byte planes expose that redundancy to LZ4.
        byte[] shuffled = new byte[raw.Length];
        int count = raw.Length / ColumnBytes;
        for (int fieldByte = 0; fieldByte < ColumnBytes; fieldByte++)
            for (int i = 0; i < count; i++) shuffled[fieldByte * count + i] = raw[i * ColumnBytes + fieldByte];
        int length = LZ4Codec.Encode(shuffled, 0, shuffled.Length, packed, 0, packed.Length);
        bool compressed = length > 0 && length < raw.Length;
        return new TerrainSamplingBatch { Offset = offset, Count = raw.Length / ColumnBytes,
            Compressed = compressed, Data = compressed ? packed.AsSpan(0, length).ToArray() : raw };
    }

    public static byte[] Decode(TerrainSamplingBatch batch)
    {
        if (batch.Count < 1 || batch.Count > MaxBatchColumns || batch.Data == null
            || batch.Data.Length < 1 || batch.Data.Length > MaxBatchColumns * ColumnBytes)
            throw new InvalidDataException("Invalid terrain sample block.");
        int size = batch.Count * ColumnBytes;
        if (!batch.Compressed)
        {
            if (batch.Data.Length != size) throw new InvalidDataException("Invalid raw sample length.");
            return batch.Data;
        }
        byte[] shuffled = new byte[size];
        if (LZ4Codec.Decode(batch.Data, 0, batch.Data.Length, shuffled, 0, size) != size)
            throw new InvalidDataException("Invalid compressed sample length.");
        byte[] raw = new byte[size];
        for (int fieldByte = 0; fieldByte < ColumnBytes; fieldByte++)
            for (int i = 0; i < batch.Count; i++) raw[i * ColumnBytes + fieldByte] = shuffled[fieldByte * batch.Count + i];
        return raw;
    }

    public static void Write(BinaryWriter writer, FastMapTerrainSamplerColumn column)
    {
        writer.Write(column.Height);
        writer.Write(column.HasClimate);
        writer.Write(column.Rainfall);
        writer.Write(column.Temperature);
        writer.Write(column.ClimateColor);
        writer.Write(column.ForestDensity);
        writer.Write(column.ShrubDensity);
    }

    public static FastMapTerrainSamplerColumn Read(BinaryReader reader)
    {
        int height = reader.ReadInt32();
        bool climate = reader.ReadBoolean();
        float rain = reader.ReadSingle();
        float temperature = reader.ReadSingle();
        int color = reader.ReadInt32();
        float forest = reader.ReadSingle();
        float shrubs = reader.ReadSingle();
        if (!float.IsFinite(rain) || !float.IsFinite(temperature) || !float.IsFinite(forest) || !float.IsFinite(shrubs))
            throw new InvalidDataException("Invalid terrain sample.");
        return climate ? new FastMapTerrainSamplerColumn(height, rain, temperature, color, forest, shrubs)
            : new FastMapTerrainSamplerColumn(height);
    }
}

internal sealed class TerrainSamplesPendingException : Exception { }
