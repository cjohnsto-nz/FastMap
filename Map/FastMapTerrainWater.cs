namespace FastMap.Map;

internal static class FastMapTerrainWater
{
    public static int WaterHeightThreshold(int seaLevel, int waterLevelOffset)
    {
        return seaLevel + waterLevelOffset;
    }

    public static bool IsWaterHeight(int height, int seaLevel, int waterLevelOffset)
    {
        return height < WaterHeightThreshold(seaLevel, waterLevelOffset);
    }

    public static bool IsLandHeight(int height, int seaLevel, int waterLevelOffset)
    {
        return !IsWaterHeight(height, seaLevel, waterLevelOffset);
    }
}
