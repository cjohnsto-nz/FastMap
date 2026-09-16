namespace FastMap.Network;

internal sealed record TerrainTileCacheIdentity(string WorldId, long Seed, int SizeX, int SizeZ,
    string WorldConfiguration, string ModVersions, string Rendering, int Revision = 0)
{
    public string Fingerprint => TerrainRenderingIdentity.Hash(writer =>
    {
        writer.Write("fastmap-server-tiles-v1");
        writer.Write(WorldId); writer.Write(Seed); writer.Write(SizeX); writer.Write(SizeZ);
        writer.Write(WorldConfiguration); writer.Write(ModVersions); writer.Write(Rendering);
        writer.Write(Revision);
    });
}
