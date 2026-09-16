using System;

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

    public FastMapTerrainSamplerColumn WithHeight(int height)
    {
        return HasClimate
            ? new FastMapTerrainSamplerColumn(height, Rainfall, Temperature, ClimateColor, ForestDensity, ShrubDensity)
            : new FastMapTerrainSamplerColumn(height);
    }

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
