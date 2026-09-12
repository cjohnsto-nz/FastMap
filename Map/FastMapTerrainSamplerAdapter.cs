using System;
using System.Reflection;
using FastMap.Network;
using Vintagestory.API.Client;
using ICoreAPI = Vintagestory.API.Common.ICoreAPI;
using Mod = Vintagestory.API.Common.Mod;

namespace FastMap.Map;

internal sealed class FastMapTerrainSamplerAdapter
{
    public const string RequiredModId = "algernonsterrainsampler";
    public const string MinimumSupportedVersion = "1.3.0";

    private readonly Func<int, int, int> sampleHeight;
    private readonly Func<int, int, FastMapTerrainSamplerColumn>? sampleColumn;
    private RemoteTerrainSampler? remote;
    private RemoteTerrainTiles? tiles;

    private FastMapTerrainSamplerAdapter(Func<int, int, int> sampleHeight, Func<int, int, FastMapTerrainSamplerColumn>? sampleColumn)
    {
        this.sampleHeight = sampleHeight;
        this.sampleColumn = sampleColumn;
    }

    public bool HasColumnSamples => sampleColumn != null;
    public bool IsAvailable => remote?.Available ?? true;
    public bool IsRemote => remote != null;
    public bool SupportsBackgroundSampling { get; private set; }

    internal static FastMapTerrainSamplerAdapter CreateRemote(RemoteTerrainSampler remote, RemoteTerrainTiles tiles)
    {
        FastMapTerrainSamplerColumn Sample(int x, int z) => remote.SampleGrid(x, z, 1, 1, 1)[0];
        return new FastMapTerrainSamplerAdapter((x, z) => Sample(x, z).Height, Sample) { remote = remote, tiles = tiles };
    }

    public int[] SampleTile(int x,int z,int step,int style) => tiles?.Get(x,z,step,style)
        ?? throw new InvalidOperationException("Server terrain tiles are unavailable");

    public FastMapTerrainSamplerColumn[] SampleGrid(int x, int z, int width, int height, int step)
    {
        if (remote != null) return remote.SampleGrid(x, z, width, height, step);
        var samples = new FastMapTerrainSamplerColumn[checked(width * height)];
        for (int row = 0; row < height; row++)
            for (int col = 0; col < width; col++)
                samples[row * width + col] = SampleColumn(x + col * step, z + row * step);
        return samples;
    }

    public int GetBlockColumnHeight(int blockX, int blockZ)
    {
        return sampleHeight(blockX, blockZ);
    }

    public FastMapTerrainSamplerColumn SampleColumn(int blockX, int blockZ)
    {
        return sampleColumn != null
            ? sampleColumn(blockX, blockZ)
            : new FastMapTerrainSamplerColumn(sampleHeight(blockX, blockZ));
    }

    public static FastMapTerrainSamplerAdapter? TryCreate(ICoreAPI? api = null)
    {
        // An assembly on a multiplayer client cannot expose the remote server's sampler.
        if (api is ICoreClientAPI client && !client.IsSinglePlayer)
        {
            var adapter = client.ModLoader.GetModSystem<FastMapTerrainSamplingSystem>()?.ClientAdapter;
            return adapter?.IsAvailable == true ? adapter : null;
        }
        try
        {
            if (!IsInstalledVersionSupported(api))
            {
                return null;
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? modType = assembly.GetType("AlgernonsTerrainSampler.TerrainSamplerMod");
                if (modType == null)
                {
                    continue;
                }

                PropertyInfo? instanceProperty = modType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                MethodInfo? getHeightMethod = modType.GetMethod(
                    "GetBlockColumnHeight",
                    BindingFlags.Public | BindingFlags.Instance,
                    binder: null,
                    types: new[] { typeof(int), typeof(int) },
                    modifiers: null);

                object? instance = instanceProperty?.GetValue(null);
                if (instance == null || getHeightMethod == null)
                {
                    return null;
                }

                Func<int, int, int> sampleHeight = getHeightMethod.CreateDelegate<Func<int, int, int>>(instance);
                return new FastMapTerrainSamplerAdapter(sampleHeight, TryCreateColumnSample(instance, modType, sampleHeight))
                {
                    // Audited 1.3.0 uses thread-local contexts and locked map generators.
                    // Watersheds delegates to another mod whose concurrency contract is unknown.
                    SupportsBackgroundSampling = api != null && InstalledTerrainSamplerVersion(api) == "1.3.0"
                        && modType.GetProperty("WatershedsLoaded")?.GetValue(instance) is false
                };
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    public static bool IsInstalledVersionSupported(ICoreAPI? api)
    {
        if (api == null)
        {
            return true;
        }

        string? installedVersion = InstalledTerrainSamplerVersion(api);
        return installedVersion != null && IsVersionAtLeast(installedVersion, MinimumSupportedVersion);
    }

    public static string? InstalledTerrainSamplerVersion(ICoreAPI api)
    {
        foreach (Mod mod in api.ModLoader.Mods)
        {
            if (string.Equals(mod.Info?.ModID, RequiredModId, StringComparison.OrdinalIgnoreCase))
            {
                return mod.Info?.Version;
            }
        }

        return null;
    }

    private static bool IsVersionAtLeast(string installedVersion, string minimumVersion)
    {
        Version installed = ParseVersionPrefix(installedVersion);
        Version minimum = ParseVersionPrefix(minimumVersion);
        return installed.CompareTo(minimum) >= 0;
    }

    private static Version ParseVersionPrefix(string version)
    {
        ReadOnlySpan<char> span = version.AsSpan().Trim();
        int length = 0;
        bool previousWasDot = false;

        while (length < span.Length)
        {
            char c = span[length];
            if (char.IsDigit(c))
            {
                previousWasDot = false;
                length++;
                continue;
            }

            if (c == '.' && !previousWasDot)
            {
                previousWasDot = true;
                length++;
                continue;
            }

            break;
        }

        string numeric = span[..length].TrimEnd('.').ToString();
        return Version.TryParse(numeric, out Version? parsed) ? parsed : new Version(0, 0, 0);
    }

    private static Func<int, int, FastMapTerrainSamplerColumn>? TryCreateColumnSample(
        object instance,
        Type modType,
        Func<int, int, int> sampleHeight)
    {
        MethodInfo? method = modType.GetMethod(
            "SampleColumn",
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: new[] { typeof(int), typeof(int) },
            modifiers: null);

        if (method == null || method.ReturnType == typeof(void))
        {
            return null;
        }

        return (blockX, blockZ) =>
        {
            object? result = method.Invoke(instance, new object[] { blockX, blockZ });
            return TryReadColumnSample(result, out FastMapTerrainSamplerColumn sample)
                ? sample
                : new FastMapTerrainSamplerColumn(sampleHeight(blockX, blockZ));
        };
    }

    private static bool TryReadColumnSample(object? result, out FastMapTerrainSamplerColumn sample)
    {
        int? height = ReadInt(result, "Height");
        if (height == null)
        {
            sample = default;
            return false;
        }

        int? climateColor = ReadInt(result, "ClimateColor");
        float? rainfall = ReadFloat(result, "Rainfall");
        float? temperature = ReadFloat(result, "Temperature");
        if (climateColor != null)
        {
            rainfall ??= ((climateColor.Value >> 8) & 0xFF) / 255f;
            temperature ??= ((climateColor.Value >> 16) & 0xFF) / 255f;
        }

        if (rainfall == null || temperature == null)
        {
            sample = new FastMapTerrainSamplerColumn(height.Value);
            return true;
        }

        sample = new FastMapTerrainSamplerColumn(
            height.Value,
            rainfall.Value,
            temperature.Value,
            climateColor ?? 0,
            ReadFloat(result, "ForestDensity") ?? 0f,
            ReadFloat(result, "ShrubDensity") ?? 0f);
        return true;
    }

    private static int? ReadInt(object? instance, string name)
    {
        if (instance == null)
        {
            return null;
        }

        Type type = instance.GetType();
        PropertyInfo? property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property != null)
        {
            object? value = property.GetValue(instance);
            if (value != null)
            {
                return Convert.ToInt32(value);
            }
        }

        FieldInfo? field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
        if (field != null)
        {
            object? value = field.GetValue(instance);
            if (value != null)
            {
                return Convert.ToInt32(value);
            }
        }

        return null;
    }

    private static float? ReadFloat(object? instance, string name)
    {
        if (instance == null)
        {
            return null;
        }

        Type type = instance.GetType();
        PropertyInfo? property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property != null)
        {
            object? value = property.GetValue(instance);
            if (value != null)
            {
                return Convert.ToSingle(value);
            }
        }

        FieldInfo? field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
        if (field != null)
        {
            object? value = field.GetValue(instance);
            if (value != null)
            {
                return Convert.ToSingle(value);
            }
        }

        return null;
    }
}
