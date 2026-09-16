using System;
using System.IO;
using System.Security.Cryptography;

namespace FastMap.Network;

// Include every server-owned input that changes the cached pixels. Bump the
// renderer revision when its algorithm changes without a settings change.
internal sealed record TerrainRenderingIdentity(int HeightOffset, int WaterLevelOffset,
    int SnowStartHeight, int MapHeight, int SeaLevel, int LandColor, int WaterColor,
    int WaterEdgeColor, string PaletteFingerprint, string SamplerVersion)
{
    public string Fingerprint => Hash(writer =>
    {
        writer.Write("fastmap-renderer-v1");
        writer.Write(HeightOffset); writer.Write(WaterLevelOffset);
        writer.Write(SnowStartHeight); writer.Write(MapHeight); writer.Write(SeaLevel);
        writer.Write(LandColor); writer.Write(WaterColor); writer.Write(WaterEdgeColor);
        writer.Write(PaletteFingerprint); writer.Write(SamplerVersion);
    });

    internal static string Hash(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) write(writer);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    internal static bool IsValid(string? fingerprint) => fingerprint?.Length == 64
        && System.Linq.Enumerable.All(fingerprint, c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static string CacheSuffix(string? fingerprint) => IsValid(fingerprint)
        ? "-server-" + fingerprint : "-server-pending";
}
