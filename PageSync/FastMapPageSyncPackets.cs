using System;
using ProtoBuf;

namespace FastMap.PageSync;

internal static class FastMapPageSyncNetwork
{
    public const string ChannelName = "fastmappagesync";
}

[ProtoContract]
public sealed class FastMapPageSyncKey
{
    [ProtoMember(1)]
    public int X { get; set; }

    [ProtoMember(2)]
    public int Y { get; set; }
}

[ProtoContract]
public sealed class FastMapPageSyncRequestPacket
{
    [ProtoMember(1)]
    public FastMapPageSyncKey[] Pages { get; set; } = Array.Empty<FastMapPageSyncKey>();
}

[ProtoContract]
public sealed class FastMapPageSyncUploadPacket
{
    [ProtoMember(1)]
    public byte[] Payload { get; set; } = Array.Empty<byte>();
}

[ProtoContract]
public sealed class FastMapPageSyncPagePacket
{
    [ProtoMember(1)]
    public byte[] Payload { get; set; } = Array.Empty<byte>();
}

[ProtoContract]
public sealed class FastMapPageSyncResetPacket
{
}
