using System;
using K4os.Compression.LZ4;
using ProtoBuf;

namespace FastMap.Network;

[ProtoContract]
public sealed class TerrainTileRequest
{
    [ProtoMember(1)] public int Id { get; set; }
    [ProtoMember(2)] public int PageX { get; set; }
    [ProtoMember(3)] public int PageZ { get; set; }
    [ProtoMember(4)] public int Step { get; set; } = 4;
    [ProtoMember(5)] public int Style { get; set; }
    [ProtoMember(6)] public bool Cancel { get; set; }
    [ProtoMember(7)] public bool CachedOnly { get; set; }
    public int Width => (1024 + Step - 1) / Step;
    public bool IsValid(int sizeX, int sizeZ) => Id > 0 && Step >= 1 && Step <= 32 && Style >= 0 && Style <= 2
        && PageX >= 0 && PageZ >= 0 && (long)PageX * 1024 < sizeX && (long)PageZ * 1024 < sizeZ;
}

[ProtoContract]
public sealed class TerrainTileBatch
{
    [ProtoMember(1)] public int Id { get; set; }
    [ProtoMember(2)] public int Offset { get; set; }
    [ProtoMember(3)] public int TotalBytes { get; set; }
    [ProtoMember(4)] public byte[] Data { get; set; } = Array.Empty<byte>();
    [ProtoMember(5)] public bool Compressed { get; set; }
    [ProtoMember(6)] public string Error { get; set; } = "";
    [ProtoMember(7)] public bool CacheMiss { get; set; }
    [ProtoMember(8)] public bool Queued { get; set; }
    [ProtoMember(9)] public int NextUpdateMilliseconds { get; set; }
}

internal sealed record EncodedTerrainTile(byte[] Data, bool Compressed)
{
    internal const int FragmentBytes = 32 * 1024;
    public static EncodedTerrainTile Encode(int[] pixels)
    {
        byte[] raw = new byte[checked(pixels.Length * 4)];
        for (int i=0;i<pixels.Length;i++) System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(i*4),pixels[i]);
        byte[] compressed = new byte[LZ4Codec.MaximumOutputSize(raw.Length)];
        int length = LZ4Codec.Encode(raw,0,raw.Length,compressed,0,compressed.Length,LZ4Level.L00_FAST);
        return length > 0 && length < raw.Length ? new(compressed.AsSpan(0,length).ToArray(),true) : new(raw,false);
    }
    public int[] Decode(int pixelCount)
    {
        int length=checked(pixelCount*4);
        if(pixelCount<1 || pixelCount>1024*1024 || Data.Length<1 || Data.Length>length)
            throw new System.IO.InvalidDataException("Invalid tile size");
        byte[] raw;
        if(Compressed)
        {
            raw=new byte[length];
            if(LZ4Codec.Decode(Data,0,Data.Length,raw,0,length)!=length) throw new System.IO.InvalidDataException("Invalid compressed tile");
        }
        else { if(Data.Length!=length) throw new System.IO.InvalidDataException("Invalid raw tile"); raw=Data; }
        var pixels=new int[pixelCount];
        for(int i=0;i<pixels.Length;i++) pixels[i]=System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(i*4));
        return pixels;
    }
}
