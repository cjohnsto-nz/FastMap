using System;
using System.Reflection;

namespace FastMap.Map;

internal sealed class FastMapTerrainSamplerAdapter
{
    private readonly Func<int, int, int> sampleHeight;

    private FastMapTerrainSamplerAdapter(Func<int, int, int> sampleHeight)
    {
        this.sampleHeight = sampleHeight;
    }

    public int GetBlockColumnHeight(int blockX, int blockZ)
    {
        return sampleHeight(blockX, blockZ);
    }

    public static FastMapTerrainSamplerAdapter? TryCreate()
    {
        try
        {
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
                return new FastMapTerrainSamplerAdapter(sampleHeight);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }
}
