using System;
using FastMap.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using Vintagestory.API.Common;

namespace FastMap.Map;

// Shared by local sampling and the server tile worker. No game API or GPU calls while rendering.
internal sealed class FastMapTerrainRenderer
{
    private const int PackedFallbackGrassMarker = 0x42;
    private readonly FastMapConfig config;
    private readonly FastMapFallbackPalette fallbackPalette;
    private readonly int mapHeight, heightOffset, terrainSamplerFallbackWaterLevelOffset;
    public FastMapTerrainRenderer(FastMapConfig config, FastMapFallbackPalette palette, int mapHeight, int heightOffset)
    {
        this.config=config; fallbackPalette=palette; this.mapHeight=mapHeight; this.heightOffset=heightOffset;
        terrainSamplerFallbackWaterLevelOffset=config.TerrainSamplerFallbackWaterLevelOffset;
    }

    public int[] Render(FastMapTerrainSamplerColumn[] grid, int baseBlockX, int baseBlockZ, int step,
        int seaLevel, int landColor, int waterColor, int waterEdgeColor, int style)
    {
        int cellsPerAxis=(1024+step-1)/step, gridWidth=cellsPerAxis+1;
        if(grid.Length!=gridWidth*gridWidth) throw new ArgumentException("Invalid terrain tile grid");
        int[] pixels=new int[cellsPerAxis*cellsPerAxis];
        bool useBrownFallback=style==1, usePaletteFallback=style==2;
        FastMapTerrainSamplerColumn Adjust(FastMapTerrainSamplerColumn sample) => heightOffset==0 ? sample
            : sample.WithHeight(Math.Clamp(sample.Height+heightOffset,0,mapHeight-1));
        for(int cellZ=0;cellZ<cellsPerAxis;cellZ++)
            for(int cellX=0;cellX<cellsPerAxis;cellX++)
            {
                int worldX=baseBlockX+cellX*step, worldZ=baseBlockZ+cellZ*step;
                int i=(cellZ+1)*gridWidth+cellX+1;
                var terrainSample=Adjust(grid[i]);
                int height=terrainSample.Height, westHeight=Adjust(grid[i-1]).Height,
                    northHeight=Adjust(grid[i-gridWidth]).Height, diagonalHeight=Adjust(grid[i-gridWidth-1]).Height;
                int color;
                if (useBrownFallback && fallbackPalette.TryGetBrownColor(height, worldX, worldZ, out int brownColor))
                {
                    bool flattenBrownColor = FastMapTerrainWater.IsWaterHeight(height, seaLevel, terrainSamplerFallbackWaterLevelOffset);
                    int brownFallbackColor = TerrainSamplerFallbackBrownColor(brownColor, height, seaLevel, worldX, worldZ, flattenBrownColor);
                    brownFallbackColor = TerrainSamplerBrownClimateTintColor(brownFallbackColor, terrainSample, flattenBrownColor, seaLevel);
                    color = flattenBrownColor
                        ? brownFallbackColor
                        : TerrainSamplerShadeColor(brownFallbackColor, height, westHeight, northHeight, diagonalHeight, seaLevel);
                }
                else if (usePaletteFallback)
                {
                    bool paletteSnow = IsTerrainSamplerSnow(terrainSample, height, seaLevel);
                    if (!fallbackPalette.TryGetColor(
                        height,
                        seaLevel,
                        terrainSamplerFallbackWaterLevelOffset,
                        paletteSnow,
                        dryGrassWeight: 0f,
                        lushGrassWeight: 0f,
                        worldX,
                        worldZ,
                        out int paletteColor,
                        out bool flattenPaletteColor))
                    {
                        throw new InvalidOperationException("Terrain palette is unavailable");
                    }

                    if (!flattenPaletteColor && !paletteSnow)
                    {
                        int shadedAlbedo = TerrainSamplerShadeColor(paletteColor, height, westHeight, northHeight, diagonalHeight, seaLevel);
                        color = PackFallbackGrassPixel(shadedAlbedo, terrainSample, height, seaLevel);
                    }
                    else
                    {
                        if (flattenPaletteColor)
                        {
                            int adjustedTemperature = terrainSample.HasClimate
                                ? Climate.GetAdjustedTemperature(TerrainSamplerUnscaledTemperature(terrainSample), height - seaLevel)
                                : 0;
                            int rainfall = terrainSample.HasClimate
                                ? Climate.GetRainFall(TerrainSamplerUnscaledRainfall(terrainSample), height)
                                : 0;
                            paletteColor = fallbackPalette.TerrainSamplerWaterColor(
                                rainfall,
                                adjustedTemperature,
                                terrainSample.HasClimate,
                                worldX,
                                worldZ,
                                height);
                        }

                        color = flattenPaletteColor
                            ? paletteColor
                            : paletteSnow
                                ? TerrainSamplerShadeSnowColor(
                                    PushSnowColorTowardVanillaTarget(paletteColor),
                                    height,
                                    westHeight,
                                    northHeight,
                                    diagonalHeight,
                                    seaLevel)
                                : TerrainSamplerShadeColor(paletteColor, height, westHeight, northHeight, diagonalHeight, seaLevel);
                    }
                }
                else
                {
                    int climateLandColor = TerrainSamplerClimateTintColor(landColor, terrainSample, water: false, worldX, worldZ, seaLevel);
                    color = TerrainSamplerColor(height, westHeight, northHeight, diagonalHeight, seaLevel, terrainSamplerFallbackWaterLevelOffset, climateLandColor, waterColor, waterEdgeColor);
                    color = TerrainSamplerColdSnowTintColor(color, terrainSample, height, westHeight, northHeight, diagonalHeight, seaLevel, worldX, worldZ);
                }

                pixels[cellZ*cellsPerAxis+cellX]=color;
            }
        return pixels;
    }
    private static int TerrainSamplerColor(
        int height,
        int westHeight,
        int northHeight,
        int diagonalHeight,
        int seaLevel,
        int waterLevelOffset,
        int landColor,
        int waterColor,
        int waterEdgeColor)
    {
        if (FastMapTerrainWater.IsWaterHeight(height, seaLevel, waterLevelOffset))
        {
            // Vanilla keeps ocean pixels flat in the non-true-colour map path and only colours shorelines as wateredge.
            bool nearLand = FastMapTerrainWater.IsLandHeight(westHeight, seaLevel, waterLevelOffset)
                || FastMapTerrainWater.IsLandHeight(northHeight, seaLevel, waterLevelOffset)
                || FastMapTerrainWater.IsLandHeight(diagonalHeight, seaLevel, waterLevelOffset);
            return (nearLand ? waterEdgeColor : waterColor) | unchecked((int)0xFF000000);
        }

        return TerrainSamplerShadeColor(landColor, height, westHeight, northHeight, diagonalHeight, seaLevel);
    }

    private static int TerrainSamplerShadeColor(int color, int height, int westHeight, int northHeight, int diagonalHeight, int seaLevel)
    {
        int diagonalDelta = height - diagonalHeight;
        int westDelta = height - westHeight;
        int northDelta = height - northHeight;
        float signSum = Math.Sign(diagonalDelta) + Math.Sign(westDelta) + Math.Sign(northDelta);
        float maxDelta = Math.Max(Math.Max(Math.Abs(diagonalDelta), Math.Abs(westDelta)), Math.Abs(northDelta));
        float relief = Math.Min(0.5f, maxDelta / 12f);
        float altitude = Math.Clamp((height - seaLevel) / 180f, 0f, 0.35f);
        float shade = signSum > 0f
            ? 1.02f + relief * 0.55f + altitude
            : signSum < 0f
                ? 0.96f - relief * 0.35f + altitude * 0.5f
                : 1f + altitude * 0.75f;

        return ColorUtil.ColorMultiply3Clamped(color, shade) | unchecked((int)0xFF000000);
    }

    private static int TerrainSamplerFallbackBrownColor(int color, int height, int seaLevel, int worldX, int worldZ, bool water)
    {
        uint hash = MixFallbackNoise((uint)worldX, (uint)worldZ, (uint)height);
        float contrast = water ? 1.0f : 1.04f;
        int noise = water ? 0 : (int)(hash % 7) - 3;
        int altitude = Math.Clamp(height - seaLevel, -32, 220);
        int altitudeLift = water ? -14 : (int)MathF.Round(altitude * 0.01f);
        int adjusted = AdjustFallbackBrownColor(color, contrast, noise + altitudeLift);
        return water ? adjusted : BlendFallbackBrownColor(adjusted, color, 0.55f);
    }

    private int TerrainSamplerBrownClimateTintColor(int color, FastMapTerrainSamplerColumn sample, bool water, int seaLevel)
    {
        if (water || !sample.HasClimate)
        {
            return color;
        }

        float rainfall = Math.Clamp(sample.Rainfall, 0f, 1f);
        float temperature = Math.Clamp(sample.Temperature, 0f, 1f);
        GetTerrainSamplerVegetationWeights(sample, seaLevel, out float forest, out float shrub);
        float vegetation = Math.Max(forest, shrub * 0.65f);
        float aridity = Math.Clamp(temperature * (1f - rainfall), 0f, 1f);

        // Brown fallback should stay close to map-bkg.png; avoid vegetation hue shifts.
        int valueShift = (int)MathF.Round(aridity * 3f - Math.Max(0f, rainfall - 0.75f) * 2f - vegetation * 2f);
        return AdjustFallbackBrownColor(color, 1.0f, valueShift);
    }

    private int TerrainSamplerClimateTintColor(int color, FastMapTerrainSamplerColumn sample, bool water, int worldX, int worldZ, int seaLevel)
    {
        if (water || !sample.HasClimate)
        {
            return color;
        }

        float rainfall = Math.Clamp(sample.Rainfall, 0f, 1f);
        float temperature = Math.Clamp(sample.Temperature, 0f, 1f);
        float dryness = 1f - rainfall;
        float cold = 0.5f - temperature;
        GetTerrainSamplerVegetationWeights(sample, seaLevel, out float forest, out float shrub);
        float vegetation = Math.Max(forest, shrub * 0.45f);
        float aridity = Math.Clamp(temperature * dryness, 0f, 1f);
        float aridWeight = Math.Clamp((aridity - 0.28f) / 0.55f, 0f, 0.85f) * (1f - vegetation * 0.85f);

        int tinted = BlendFallbackBrownColor(color, unchecked((int)0xFF68A4C4), aridWeight);
        if (shrub > 0f)
        {
            int shrubColor = ApplyVegetationValueNoise(unchecked((int)0xFF61A39C), worldX, worldZ, sample.Height, 0x9E3779B9u);
            tinted = BlendFallbackBrownColor(tinted, shrubColor, Math.Clamp(shrub * 0.38f, 0f, 0.45f));
        }

        if (forest > 0f)
        {
            int forestColor = ApplyVegetationValueNoise(unchecked((int)0xFF4C8498), worldX, worldZ, sample.Height, 0x85EBCA6Bu);
            tinted = BlendFallbackBrownColor(tinted, forestColor, Math.Clamp(forest * 0.72f, 0f, 0.78f));
        }

        int greenShift = (int)MathF.Round((rainfall - 0.5f) * 2f);
        int blueShift = (int)MathF.Round(cold * 3f);
        int valueShift = (int)MathF.Round(-Math.Max(0f, rainfall - 0.8f) * 2f);

        int r = Math.Clamp((tinted & 0xFF) + valueShift, 0, 255);
        int g = Math.Clamp(((tinted >> 8) & 0xFF) + greenShift + valueShift, 0, 255);
        int b = Math.Clamp(((tinted >> 16) & 0xFF) + blueShift + valueShift, 0, 255);
        return unchecked((int)0xFF000000) | (b << 16) | (g << 8) | r;
    }

    private int TerrainSamplerPaletteClimateTintColor(int color, FastMapTerrainSamplerColumn sample, int worldX, int worldZ, int seaLevel)
    {
        if (!sample.HasClimate)
        {
            return color;
        }

        int tinted = color;
        GetTerrainSamplerVegetationWeights(sample, seaLevel, out float forest, out float shrub);
        if (shrub > 0f)
        {
            int shrubColor = fallbackPalette.TryGetTreeColor(sample.Height, worldX, worldZ, 0x9E3779B9u, out int paletteShrubColor)
                ? paletteShrubColor
                : unchecked((int)0xFF4F6B78);
            shrubColor = ApplyVegetationValueNoise(shrubColor, worldX, worldZ, sample.Height, 0x9E3779B9u);
            shrubColor = BlendFallbackBrownColor(shrubColor, unchecked((int)0xFF3A596B), 0.42f);
            tinted = BlendFallbackBrownColor(tinted, shrubColor, Math.Clamp(shrub * 0.26f, 0f, 0.38f));
        }

        if (forest > 0f)
        {
            int forestColor = fallbackPalette.TryGetTreeColor(sample.Height, worldX, worldZ, 0x85EBCA6Bu, out int paletteForestColor)
                ? paletteForestColor
                : unchecked((int)0xFF3E5F73);
            forestColor = ApplyVegetationValueNoise(forestColor, worldX, worldZ, sample.Height, 0x85EBCA6Bu);
            forestColor = BlendFallbackBrownColor(forestColor, unchecked((int)0xFF2F4A5D), 0.48f);
            tinted = BlendFallbackBrownColor(tinted, forestColor, Math.Clamp(forest * 0.5f, 0f, 0.62f));
        }

        return tinted;
    }

    private void GetTerrainSamplerGrassSpectrumWeights(
        FastMapTerrainSamplerColumn sample,
        int height,
        int seaLevel,
        out float dryWeight,
        out float lushWeight)
    {
        dryWeight = 0f;
        lushWeight = 0f;
        if (!sample.HasClimate)
        {
            return;
        }

        float fertility = TerrainSamplerFertility(sample, height, seaLevel);
        dryWeight = SmoothStep(0.42f, 0.12f, fertility);
        lushWeight = SmoothStep(0.48f, 0.82f, fertility);
    }

    private void GetTerrainSamplerVegetationWeights(FastMapTerrainSamplerColumn sample, int seaLevel, out float forest, out float shrub)
    {
        float rawForest = Math.Clamp(sample.ForestDensity, 0f, 1f);
        float rawShrub = Math.Clamp(sample.ShrubDensity, 0f, 1f);
        if (!sample.HasClimate || rawForest <= 0f && rawShrub <= 0f)
        {
            forest = rawForest;
            shrub = rawShrub;
            return;
        }

        float adjustedRain = TerrainSamplerAdjustedRainfall(sample, sample.Height);
        float adjustedTemp = TerrainSamplerTemperatureCelsius(sample, sample.Height, seaLevel);
        float relativeHeight = TerrainSamplerRelativeHeight(sample.Height, seaLevel);
        float fertilityRel = TerrainSamplerFertility(sample, sample.Height, seaLevel);

        // Vanilla still uses forest/shrub maps as density gates, but tree choice is climate-scored.
        // This broad suitability curve keeps the tint closer to where actual vegetation can appear.
        float forestClimate = SmoothStep(0.22f, 0.48f, adjustedRain)
            * SmoothStep(-8f, 4f, adjustedTemp)
            * (1f - SmoothStep(34f, 42f, adjustedTemp))
            * SmoothStep(0.12f, 0.36f, fertilityRel)
            * (1f - SmoothStep(0.72f, 0.96f, relativeHeight));
        float shrubClimate = SmoothStep(0.10f, 0.32f, adjustedRain)
            * SmoothStep(-14f, -2f, adjustedTemp)
            * (1f - SmoothStep(38f, 46f, adjustedTemp))
            * SmoothStep(0.08f, 0.26f, fertilityRel)
            * (1f - SmoothStep(0.78f, 1.0f, relativeHeight));

        forest = rawForest * Math.Clamp(0.08f + forestClimate * 0.92f, 0f, 1f);
        shrub = rawShrub * Math.Clamp(0.12f + shrubClimate * 0.88f, 0f, 1f);
    }

    private float TerrainSamplerRelativeHeight(int height, int seaLevel)
    {
        int mapHeight = Math.Max(seaLevel + 1, this.mapHeight);
        return Math.Clamp((height - seaLevel) / (float)(mapHeight - seaLevel), 0f, 1f);
    }

    private float TerrainSamplerFertility(FastMapTerrainSamplerColumn sample, int height, int seaLevel)
    {
        float adjustedTemp = TerrainSamplerTemperatureCelsius(sample, height, seaLevel);
        float relativeHeight = TerrainSamplerRelativeHeight(height, seaLevel);
        int rain = Math.Clamp((int)MathF.Round(TerrainSamplerAdjustedRainfall(sample, height) * 255f), 0, 255);
        return Climate.GetFertility(rain, adjustedTemp, relativeHeight) / 255f;
    }

    private static float TerrainSamplerAdjustedRainfall(FastMapTerrainSamplerColumn sample, int height)
    {
        int unscaledRain = (sample.ClimateColor >> 8) & 0xFF;
        if (unscaledRain == 0)
        {
            unscaledRain = Math.Clamp((int)MathF.Round(sample.Rainfall * 255f), 0, 255);
        }

        return Climate.GetRainFall(unscaledRain, height) / 255f;
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        if (Math.Abs(edge1 - edge0) < 0.0001f)
        {
            return value >= edge1 ? 1f : 0f;
        }

        float t = Math.Clamp((value - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private int TerrainSamplerColdSnowTintColor(
        int color,
        FastMapTerrainSamplerColumn sample,
        int height,
        int westHeight,
        int northHeight,
        int diagonalHeight,
        int seaLevel,
        int worldX,
        int worldZ)
    {
        if (!IsTerrainSamplerSnow(sample, height, seaLevel))
        {
            return color;
        }

        int snowColor = fallbackPalette.TryGetSnowColor(height, worldX, worldZ, out int paletteSnowColor)
            ? paletteSnowColor
            : unchecked((int)0xFFC0E0E0);
        snowColor = PushSnowColorTowardVanillaTarget(snowColor);
        return TerrainSamplerShadeSnowColor(snowColor, height, westHeight, northHeight, diagonalHeight, seaLevel);
    }

    private bool IsTerrainSamplerSnow(FastMapTerrainSamplerColumn sample, int height, int seaLevel)
    {
        if (height < TerrainSamplerFallbackSnowStartHeight(seaLevel)
            || FastMapTerrainWater.IsWaterHeight(height, seaLevel, terrainSamplerFallbackWaterLevelOffset)
            || !sample.HasClimate)
        {
            return false;
        }

        return TerrainSamplerTemperatureCelsius(sample, height, seaLevel) < -10f;
    }

    internal int TerrainSamplerFallbackSnowStartHeight(int seaLevel)
    {
        int worldHeight = Math.Max(seaLevel + 1, mapHeight);
        const int referenceWorldHeight = 384;
        int referenceSeaLevel = TerrainSamplerReferenceSeaLevel(referenceWorldHeight);
        int referenceSkyHeight = Math.Max(1, referenceWorldHeight - referenceSeaLevel);
        int worldSkyHeight = Math.Max(1, worldHeight - seaLevel);
        float relativeAboveSea = Math.Clamp(
            (config.TerrainSamplerFallbackSnowStartHeight - referenceSeaLevel) / (float)referenceSkyHeight,
            0f,
            1f);

        int scaledHeight = seaLevel + (int)MathF.Round(relativeAboveSea * worldSkyHeight);
        return Math.Clamp(scaledHeight, seaLevel + 1, worldHeight - 1);
    }

    private static int TerrainSamplerReferenceSeaLevel(int worldHeight)
    {
        return (int)(worldHeight * 0.43137254901960786);
    }

    private static float TerrainSamplerTemperatureCelsius(FastMapTerrainSamplerColumn sample, int height, int seaLevel)
    {
        int unscaledTemp = (sample.ClimateColor >> 16) & 0xFF;
        if (unscaledTemp == 0)
        {
            unscaledTemp = Math.Clamp((int)MathF.Round(sample.Temperature * 255f), 0, 255);
        }

        return Climate.GetScaledAdjustedTemperatureFloat(unscaledTemp, height - seaLevel);
    }

    private static int PushSnowColorTowardVanillaTarget(int color)
    {
        const int targetSnowColor = unchecked((int)0xFFC0E0E0); // #e0e0c0 in Vintage Story's 0xAABBGGRR map color order.
        int targetHueColor = BlendFallbackBrownColor(color, targetSnowColor, 0.75f);
        return ColorUtil.ColorMultiply3Clamped(targetHueColor, 0.86f) | unchecked((int)0xFF000000);
    }

    private static int TerrainSamplerShadeSnowColor(int color, int height, int westHeight, int northHeight, int diagonalHeight, int seaLevel)
    {
        int diagonalDelta = height - diagonalHeight;
        int westDelta = height - westHeight;
        int northDelta = height - northHeight;
        float signSum = Math.Sign(diagonalDelta) + Math.Sign(westDelta) + Math.Sign(northDelta);
        float maxDelta = Math.Max(Math.Max(Math.Abs(diagonalDelta), Math.Abs(westDelta)), Math.Abs(northDelta));
        float relief = Math.Min(0.65f, maxDelta / 10f);
        float altitude = Math.Clamp((height - seaLevel) / 220f, 0f, 0.22f);
        float shade = signSum > 0f
            ? 0.98f + relief * 0.45f + altitude
            : signSum < 0f
                ? 0.78f - relief * 0.12f + altitude * 0.25f
                : 0.9f + altitude * 0.45f;

        return ColorUtil.ColorMultiply3Clamped(color, shade) | unchecked((int)0xFF000000);
    }

    private static int ApplyVegetationValueNoise(int color, int worldX, int worldZ, int height, uint salt)
    {
        uint hash = MixFallbackNoise((uint)worldX ^ salt, (uint)worldZ, (uint)height);
        float multiplier = 0.7f + (hash & 0xFFFF) / 65535f * 0.6f;
        int r = Math.Clamp((int)MathF.Round((color & 0xFF) * multiplier), 0, 255);
        int g = Math.Clamp((int)MathF.Round(((color >> 8) & 0xFF) * multiplier), 0, 255);
        int b = Math.Clamp((int)MathF.Round(((color >> 16) & 0xFF) * multiplier), 0, 255);
        return unchecked((int)0xFF000000) | (b << 16) | (g << 8) | r;
    }

    private static int AdjustFallbackBrownColor(int color, float contrast, int brightnessOffset)
    {
        int r = AdjustFallbackChannel(color & 0xFF, contrast, brightnessOffset);
        int g = AdjustFallbackChannel((color >> 8) & 0xFF, contrast, brightnessOffset);
        int b = AdjustFallbackChannel((color >> 16) & 0xFF, contrast, brightnessOffset);
        return unchecked((int)0xFF000000) | (b << 16) | (g << 8) | r;
    }

    private static int AdjustFallbackChannel(int value, float contrast, int brightnessOffset)
    {
        return Math.Clamp((int)MathF.Round((value - 128) * contrast + 128 + brightnessOffset), 24, 235);
    }

    private static int BlendFallbackBrownColor(int color, int sourceColor, float sourceWeight)
    {
        int r = BlendFallbackChannel(color & 0xFF, sourceColor & 0xFF, sourceWeight);
        int g = BlendFallbackChannel((color >> 8) & 0xFF, (sourceColor >> 8) & 0xFF, sourceWeight);
        int b = BlendFallbackChannel((color >> 16) & 0xFF, (sourceColor >> 16) & 0xFF, sourceWeight);
        return unchecked((int)0xFF000000) | (b << 16) | (g << 8) | r;
    }

    private static int BlendFallbackChannel(int value, int sourceValue, float sourceWeight)
    {
        return Math.Clamp((int)MathF.Round(value * (1f - sourceWeight) + sourceValue * sourceWeight), 0, 255);
    }

    private static uint MixFallbackNoise(uint x, uint z, uint height)
    {
        uint hash = 2166136261u;
        hash = (hash ^ x) * 16777619u;
        hash = (hash ^ z) * 16777619u;
        hash = (hash ^ height) * 16777619u;
        hash ^= hash >> 15;
        hash *= 2246822519u;
        hash ^= hash >> 13;
        return hash;
    }


    private static int PackFallbackGrassPixel(int shadedAlbedo, FastMapTerrainSamplerColumn sample, int height, int seaLevel)
    {
        int adjustedTemperature = sample.HasClimate
            ? Climate.GetAdjustedTemperature(TerrainSamplerUnscaledTemperature(sample), height - seaLevel)
            : 128;
        int rain = sample.HasClimate
            ? TerrainSamplerUnscaledRainfall(sample)
            : 128;
        int albedo = MapColorLuma(shadedAlbedo);
        return (PackedFallbackGrassMarker << 24)
            | ((Math.Clamp(adjustedTemperature, 0, 255) & 0xFF) << 16)
            | ((Math.Clamp(rain, 0, 255) & 0xFF) << 8)
            | (albedo & 0xFF);
    }
    private static int MapColorLuma(int color)
    {
        int r = color & 0xFF;
        int g = (color >> 8) & 0xFF;
        int b = (color >> 16) & 0xFF;
        return Math.Clamp((int)MathF.Round(r * 0.299f + g * 0.587f + b * 0.114f), 0, 255);
    }
    private static int TerrainSamplerUnscaledTemperature(FastMapTerrainSamplerColumn sample)
    {
        int unscaledTemp = (sample.ClimateColor >> 16) & 0xFF;
        if (unscaledTemp == 0)
        {
            unscaledTemp = Math.Clamp((int)MathF.Round(sample.Temperature * 255f), 0, 255);
        }

        return unscaledTemp;
    }
    private static int TerrainSamplerUnscaledRainfall(FastMapTerrainSamplerColumn sample)
    {
        int unscaledRain = (sample.ClimateColor >> 8) & 0xFF;
        if (unscaledRain == 0)
        {
            unscaledRain = Math.Clamp((int)MathF.Round(sample.Rainfall * 255f), 0, 255);
        }

        return unscaledRain;
    }
}
