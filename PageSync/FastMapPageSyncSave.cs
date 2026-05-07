using System.Collections.Generic;
using ProtoBuf;

namespace FastMap.PageSync;

[ProtoContract]
public sealed class FastMapPageSyncSave
{
    [ProtoMember(1)]
    public Dictionary<string, bool> ShareEnabledByPlayerUid { get; set; } = [];

    [ProtoMember(2)]
    public Dictionary<string, string[]> SelectedSourcesByPlayerUid { get; set; } = [];
}
