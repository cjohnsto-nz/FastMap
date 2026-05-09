using System;
using System.Reflection;
using Vintagestory.API.MathTools;

namespace FastMap.Map;

public readonly struct FastMapTerrainSamplerColumn
{
    public FastMapTerrainSamplerColumn(int height)
    {
        Height = height;
        HasClimate = false;
        Rainfall = 0f;
        Temperature = 0f;
        ClimateColor = 0;
        ForestDensity = 0f;
        ShrubDensity = 0f;
    }

    public FastMapTerrainSamplerColumn(
        int height,
        float rainfall,
        float temperature,
        int climateColor,
        float forestDensity = 0f,
        float shrubDensity = 0f)
    {
        Height = height;
        HasClimate = true;
        Rainfall = NormalizeFraction(rainfall);
        Temperature = NormalizeTemperature(temperature);
        ClimateColor = climateColor;
        ForestDensity = NormalizeFraction(forestDensity);
        ShrubDensity = NormalizeFraction(shrubDensity);
    }

    public int Height { get; }
    public bool HasClimate { get; }
    public float Rainfall { get; }
    public float Temperature { get; }
    public int ClimateColor { get; }
    public float ForestDensity { get; }
    public float ShrubDensity { get; }

    private static float NormalizeFraction(float value)
    {
        return Math.Clamp(value > 1f ? value / 255f : value, 0f, 1f);
    }

    private static float NormalizeTemperature(float value)
    {
        if (value is >= 0f and <= 1f)
        {
            return value;
        }

        return Math.Clamp(value > 40f ? value / 255f : (value + 20f) / 60f, 0f, 1f);
    }
}

internal sealed class FastMapTerrainSamplerAdapter
{
    private readonly Func<int, int, int> sampleHeight;
    private readonly Func<int, int, FastMapTerrainSamplerColumn>? sampleColumn;

    private FastMapTerrainSamplerAdapter(Func<int, int, int> sampleHeight, Func<int, int, FastMapTerrainSamplerColumn>? sampleColumn)
    {
        this.sampleHeight = sampleHeight;
        this.sampleColumn = sampleColumn;
    }

    public bool HasColumnSamples => sampleColumn != null;

    public int GetBlockColumnHeight(int blockX, int blockZ)
    {
        return sampleHeight(blockX, blockZ);
    }

    public FastMapTerrainSamplerColumn SampleColumn(int blockX, int blockZ)
    {
        if (sampleColumn != null)
        {
            return sampleColumn(blockX, blockZ);
        }

        return new FastMapTerrainSamplerColumn(sampleHeight(blockX, blockZ));
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
                Func<int, int, FastMapTerrainSamplerColumn>? sampleColumn =
                    TryCreatePublicColumnSample(instance, modType)
                    ?? TryCreateClimateColumnSample(assembly, instance, modType, sampleHeight);

                return new FastMapTerrainSamplerAdapter(sampleHeight, sampleColumn);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static Func<int, int, FastMapTerrainSamplerColumn>? TryCreatePublicColumnSample(object instance, Type modType)
    {
        string[] methodNames =
        {
            "SampleColumn",
            "GetColumnSample",
            "GetTerrainColumnSample",
            "GetTerrainSample"
        };

        foreach (string methodName in methodNames)
        {
            MethodInfo? method = modType.GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: new[] { typeof(int), typeof(int) },
                modifiers: null);

            if (method == null || method.ReturnType == typeof(void))
            {
                continue;
            }

            return (blockX, blockZ) =>
            {
                object? result = method.Invoke(instance, new object[] { blockX, blockZ });
                return TryReadColumnSample(result, out FastMapTerrainSamplerColumn sample)
                    ? sample
                    : new FastMapTerrainSamplerColumn(ReadInt(result, "Height", "BlockColumnHeight", "Y") ?? 0);
            };
        }

        return null;
    }

    private static Func<int, int, FastMapTerrainSamplerColumn>? TryCreateClimateColumnSample(
        Assembly terrainSamplerAssembly,
        object instance,
        Type modType,
        Func<int, int, int> sampleHeight)
    {
        try
        {
            PropertyInfo? genTerraProperty = modType.GetProperty("GenTerra", BindingFlags.Public | BindingFlags.Instance);
            object? genTerra = genTerraProperty?.GetValue(instance);
            if (genTerra == null)
            {
                return null;
            }

            Type genTerraType = genTerra.GetType();
            Type? worldCoordinateType = terrainSamplerAssembly.GetType("AlgernonsTerrainSampler.Coordinates.WorldMapCoordinate");
            Type? mapCoordinateType = terrainSamplerAssembly.GetType("AlgernonsTerrainSampler.Coordinates.MapCoordinate");
            Type? terrainGenerationLibType = terrainSamplerAssembly.GetType("AlgernonsTerrainSampler.Terrain.TerrainGenerationLib");
            Type? mappingType = terrainSamplerAssembly.GetType("AlgernonsTerrainSampler.Mapping");
            if (worldCoordinateType == null || mapCoordinateType == null || terrainGenerationLibType == null || mappingType == null)
            {
                return null;
            }

            MethodInfo? worldToChunk = mappingType.GetMethod("WorldToMapChunkCoordinate", BindingFlags.Public | BindingFlags.Static);
            MethodInfo? createContext = terrainGenerationLibType.GetMethod("CreateTerrainGenerationContext", BindingFlags.Public | BindingFlags.Static);
            MethodInfo? calculateRainfall = terrainGenerationLibType.GetMethod("CalculateRainfallFromClimate", BindingFlags.Public | BindingFlags.Static);
            if (worldToChunk == null || createContext == null || calculateRainfall == null)
            {
                return null;
            }

            PropertyInfo? regionChunkSizeProperty = genTerraType.GetProperty("RegionChunkSize", BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo? regionSizeProperty = genTerraType.GetProperty("RegionSize", BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo? regionMapSizeProperty = genTerraType.GetProperty("RegionMapSize", BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo? numberOfOctavesProperty = genTerraType.GetProperty("NumberOfOctaves", BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo? distort2dxProperty = genTerraType.GetProperty("Distort2dx", BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo? distort2dzProperty = genTerraType.GetProperty("Distort2dz", BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo? basegameGenMapsProperty = genTerraType.GetProperty("BasegameGenMaps", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo? landformMapByRegionField = genTerraType.GetField("LandformMapByRegion", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo? landformsField = genTerraType.GetField("landforms", BindingFlags.Public | BindingFlags.Instance);

            object? basegameGenMaps = basegameGenMapsProperty?.GetValue(genTerra);
            if (basegameGenMaps == null)
            {
                return null;
            }

            Type genMapsType = basegameGenMaps.GetType();
            FieldInfo? landformsGenField = genMapsType.GetField("landformsGen", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo? upheavelGenField = genMapsType.GetField("upheavelGen", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo? oceanGenField = genMapsType.GetField("oceanGen", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo? climateGenField = genMapsType.GetField("climateGen", BindingFlags.Public | BindingFlags.Instance);
            if (landformsGenField == null || upheavelGenField == null || oceanGenField == null || climateGenField == null)
            {
                return null;
            }

            MethodInfo? noiseMethod = distort2dxProperty?.PropertyType.GetMethod(
                "Noise",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: new[] { typeof(double), typeof(double) },
                modifiers: null);

            if (noiseMethod == null)
            {
                return null;
            }

            bool disabled = false;
            return (blockX, blockZ) =>
            {
                int height = sampleHeight(blockX, blockZ);
                if (disabled)
                {
                    return new FastMapTerrainSamplerColumn(height);
                }

                try
                {
                    object worldCoordinate = Activator.CreateInstance(worldCoordinateType, blockX, blockZ)!;
                    object chunkCoordinate = worldToChunk.Invoke(null, new object[] { blockX, blockZ })!;
                    object blockColumnInChunk = Activator.CreateInstance(mapCoordinateType, blockX % 32, blockZ % 32)!;
                    object context = createContext.Invoke(null, new[]
                    {
                        chunkCoordinate,
                        regionChunkSizeProperty!.GetValue(genTerra)!,
                        regionSizeProperty!.GetValue(genTerra)!,
                        landformMapByRegionField!.GetValue(genTerra)!,
                        regionMapSizeProperty!.GetValue(genTerra)!,
                        landformsField!.GetValue(genTerra)!,
                        numberOfOctavesProperty!.GetValue(genTerra)!,
                        32,
                        landformsGenField.GetValue(basegameGenMaps)!,
                        upheavelGenField.GetValue(basegameGenMaps)!,
                        oceanGenField.GetValue(basegameGenMaps)!,
                        climateGenField.GetValue(basegameGenMaps)!,
                        true
                    })!;

                    object? distort2dx = distort2dxProperty!.GetValue(genTerra);
                    object? distort2dz = distort2dzProperty!.GetValue(genTerra);
                    if (distort2dx == null || distort2dz == null)
                    {
                        return new FastMapTerrainSamplerColumn(height);
                    }

                    float rainfall = (float)calculateRainfall.Invoke(null, new[]
                    {
                        blockColumnInChunk,
                        context,
                        32,
                        height,
                        worldCoordinate,
                        distort2dx,
                        distort2dz
                    })!;

                    if (rainfall < 0f || !TrySampleClimateColor(context, blockColumnInChunk, blockX, blockZ, distort2dx, distort2dz, noiseMethod, out int climateColor))
                    {
                        return new FastMapTerrainSamplerColumn(height);
                    }

                    float temperature = ((climateColor >> 16) & 0xFF) / 255f;
                    return new FastMapTerrainSamplerColumn(height, rainfall, temperature, climateColor);
                }
                catch
                {
                    disabled = true;
                    return new FastMapTerrainSamplerColumn(height);
                }
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool TrySampleClimateColor(
        object context,
        object blockColumnInChunk,
        int blockX,
        int blockZ,
        object distort2dx,
        object distort2dz,
        MethodInfo noiseMethod,
        out int climateColor)
    {
        climateColor = 0;
        object? climateCorners = context.GetType().GetField("ClimateMapCorners")?.GetValue(context);
        if (climateCorners == null)
        {
            return false;
        }

        int upLeft = ReadInt(climateCorners, "UpLeft") ?? 0;
        int upRight = ReadInt(climateCorners, "UpRight") ?? 0;
        int botLeft = ReadInt(climateCorners, "BotLeft") ?? 0;
        int botRight = ReadInt(climateCorners, "BotRight") ?? 0;
        if (upLeft == 0 && upRight == 0 && botLeft == 0 && botRight == 0)
        {
            return false;
        }

        int localX = ReadInt(blockColumnInChunk, "X") ?? blockX % 32;
        int localZ = ReadInt(blockColumnInChunk, "Z") ?? blockZ % 32;
        double dx = (double)noiseMethod.Invoke(distort2dx, new object[] { (double)blockX, (double)blockZ })!;
        double dz = (double)noiseMethod.Invoke(distort2dz, new object[] { (double)blockX, (double)blockZ })!;
        float x = localX / 32f + (float)dx / 32f;
        float z = localZ / 32f + (float)dz / 32f;
        climateColor = GameMath.BiLerpRgbColor(x, z, upLeft, upRight, botLeft, botRight);
        return true;
    }

    private static bool TryReadColumnSample(object? result, out FastMapTerrainSamplerColumn sample)
    {
        int? height = ReadInt(result, "Height", "BlockColumnHeight", "Y");
        if (height == null)
        {
            sample = default;
            return false;
        }

        int? climateColor = ReadInt(result, "ClimateColor", "Climate", "ClimateMapColor");
        float? rainfall = ReadFloat(result, "Rainfall", "WorldgenRainfall", "WorldGenRainfall");
        float? temperature = ReadFloat(result, "Temperature", "WorldgenTemperature", "WorldGenTemperature");
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
            ReadFloat(result, "ForestDensity", "Forest", "ForestRel") ?? 0f,
            ReadFloat(result, "ShrubDensity", "ShrubsDensity", "ShrubDensity", "ShrubRel") ?? 0f);
        return true;
    }

    private static int? ReadInt(object? instance, params string[] names)
    {
        if (instance == null)
        {
            return null;
        }

        Type type = instance.GetType();
        foreach (string name in names)
        {
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
        }

        return null;
    }

    private static float? ReadFloat(object? instance, params string[] names)
    {
        if (instance == null)
        {
            return null;
        }

        Type type = instance.GetType();
        foreach (string name in names)
        {
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
        }

        return null;
    }
}
