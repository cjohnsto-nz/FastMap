using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using FastMap.Config;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace FastMap.Map;

internal sealed class FastMapFallbackPalette
{
    private readonly int[] grass;
    private readonly int[] dryGrass;
    private readonly int[] lushGrass;
    private readonly int[] trees;
    private readonly int[] water;
    private readonly int waterBaseColor;
    private readonly int waterRainfallColor;
    private readonly float waterRainfallStrength;
    private readonly int waterTemperatureColor;
    private readonly float waterTemperatureStrength;
    private readonly float waterNoiseStrength;
    private readonly int[] snow;
    private readonly int[] brown;
    private readonly ClimateTintMap? climatePlantTint;
    private readonly SeasonalTintMap? seasonalGrassTint;

    private FastMapFallbackPalette(
        int[] grass,
        int[] dryGrass,
        int[] lushGrass,
        int[] trees,
        int[] water,
        int waterBaseColor,
        int waterRainfallColor,
        float waterRainfallStrength,
        int waterTemperatureColor,
        float waterTemperatureStrength,
        float waterNoiseStrength,
        int[] snow,
        int[] brown,
        ClimateTintMap? climatePlantTint,
        SeasonalTintMap? seasonalGrassTint)
    {
        this.grass = grass;
        this.dryGrass = dryGrass;
        this.lushGrass = lushGrass;
        this.trees = trees;
        this.water = water;
        this.waterBaseColor = waterBaseColor;
        this.waterRainfallColor = waterRainfallColor;
        this.waterRainfallStrength = Math.Clamp(waterRainfallStrength, 0f, 2f);
        this.waterTemperatureColor = waterTemperatureColor;
        this.waterTemperatureStrength = Math.Clamp(waterTemperatureStrength, 0f, 2f);
        this.waterNoiseStrength = Math.Clamp(waterNoiseStrength, 0f, 1f);
        this.snow = snow;
        this.brown = brown;
        this.climatePlantTint = climatePlantTint;
        this.seasonalGrassTint = seasonalGrassTint;
    }

    public bool HasGrass => grass.Length > 0;
    public bool HasDryGrass => dryGrass.Length > 0;
    public bool HasLushGrass => lushGrass.Length > 0;
    public bool HasTrees => trees.Length > 0;
    public bool HasWater => water.Length > 0;
    public bool HasSnow => snow.Length > 0;
    public bool HasBrown => brown.Length > 0;
    public bool HasAny => HasGrass || HasWater || HasSnow;
    public bool HasClimatePlantTint => climatePlantTint != null;
    public bool HasSeasonalGrassTint => seasonalGrassTint != null;

    public static FastMapFallbackPalette Load(ICoreAPI api, FastMapConfig config)
    {
        config.Normalize();
        string assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;
        int[] grass = LoadTexturePalette(api, "block/plant/grasscoverage/normal-top", out string grassSource);
        if (grass.Length == 0)
        {
            grass = LoadPalette(api, assemblyDirectory, "grass.png");
            grassSource = "grass.png";
        }

        int[] dryGrass = LoadPalette(api, assemblyDirectory, "drygrass.png", warnIfMissing: false);
        int[] lushGrass = LoadPalette(api, assemblyDirectory, "lushgrass.png", warnIfMissing: false);
        int[] trees = LoadPalette(api, assemblyDirectory, "trees.png", warnIfMissing: false);
        int[] water = LoadTexturePalette(api, "block/liquid/water", out string waterSource);
        if (water.Length == 0)
        {
            water = LoadPalette(api, assemblyDirectory, "water.png");
            waterSource = "water.png";
        }

        int waterBaseColor = ParseMapColor(config.TerrainSamplerFallbackWaterBaseColor, unchecked((int)0xFF482D01)); // #012d48 in Vintage Story's 0xAABBGGRR map color order.
        int waterRainfallColor = ParseMapColor(config.TerrainSamplerFallbackWaterRainfallColor, unchecked((int)0xFFDFC782)); // #82c7df in Vintage Story's 0xAABBGGRR map color order.
        int waterTemperatureColor = ParseMapColor(config.TerrainSamplerFallbackWaterTemperatureColor, unchecked((int)0xFF4F89B8)); // #b8894f in Vintage Story's 0xAABBGGRR map color order.
        int[] snow = LoadPalette(api, assemblyDirectory, "snow.png");
        int[] brown = LoadPalette(api, assemblyDirectory, "map-bkg.png", warnIfMissing: false);
        ClimateTintMap? climatePlantTint = LoadClimateTintMap(api, "environment/planttint", padding: 4);
        SeasonalTintMap? seasonalGrassTint = LoadSeasonalTintMap(api, "environment/seasons/grasstint");

        api.Logger.Notification(
            "[FastMap] Terrain fallback palettes loaded: grass={0} ({1}), dryGrass={2}, lushGrass={3}, trees={4}, water={5} ({6}), waterBase={7}, waterRainfall={8}x{9:0.##}, waterTemperature={10}x{11:0.##}, waterNoise={12:0.##}, snow={13}, brown={14}, climatePlant={15}, seasonalGrass={16}, path={17}",
            grass.Length,
            grassSource,
            dryGrass.Length,
            lushGrass.Length,
            trees.Length,
            water.Length,
            waterSource,
            config.TerrainSamplerFallbackWaterBaseColor,
            config.TerrainSamplerFallbackWaterRainfallColor,
            config.TerrainSamplerFallbackWaterRainfallStrength,
            config.TerrainSamplerFallbackWaterTemperatureColor,
            config.TerrainSamplerFallbackWaterTemperatureStrength,
            config.TerrainSamplerFallbackWaterNoiseStrength,
            snow.Length,
            brown.Length,
            climatePlantTint != null ? climatePlantTint.Width + "x" + climatePlantTint.Height : "missing",
            seasonalGrassTint != null ? seasonalGrassTint.Width + "x" + seasonalGrassTint.Height : "missing",
            assemblyDirectory);

        return new FastMapFallbackPalette(
            grass,
            dryGrass,
            lushGrass,
            trees,
            water,
            waterBaseColor,
            waterRainfallColor,
            config.TerrainSamplerFallbackWaterRainfallStrength,
            waterTemperatureColor,
            config.TerrainSamplerFallbackWaterTemperatureStrength,
            config.TerrainSamplerFallbackWaterNoiseStrength,
            snow,
            brown,
            climatePlantTint,
            seasonalGrassTint);
    }

    public bool TryGetColor(
        int height,
        int seaLevel,
        bool useSnow,
        float dryGrassWeight,
        float lushGrassWeight,
        int worldX,
        int worldZ,
        out int color,
        out bool flatten)
    {
        int waterHeight = seaLevel - 2;
        flatten = height <= waterHeight;
        int[] palette = flatten
            ? water
            : useSnow
                ? snow
                : grass;

        if (palette.Length == 0)
        {
            color = 0;
            flatten = false;
            return false;
        }

        uint hash = Mix((uint)worldX, (uint)worldZ, (uint)height);
        color = palette[hash % (uint)palette.Length];
        if (!flatten && !useSnow)
        {
            color = BlendGrassSpectrumColor(color, hash, dryGrassWeight, lushGrassWeight);
        }

        return true;
    }

    public int TerrainSamplerWaterColor(int rain, int adjustedTemperature, bool hasClimate, int worldX, int worldZ, int height)
    {
        if (!hasClimate)
        {
            return ApplyWaterValueNoise(waterBaseColor, worldX, worldZ, height);
        }

        float rainfallWeight = (1f - Math.Clamp(rain, 0, 255) / 255f) * waterRainfallStrength;
        float temperatureWeight = (Math.Clamp(adjustedTemperature, 0, 255) / 255f) * waterTemperatureStrength;
        int color = BlendColor(waterBaseColor, waterRainfallColor, Math.Clamp(rainfallWeight, 0f, 1f));
        color = BlendColor(color, waterTemperatureColor, Math.Clamp(temperatureWeight, 0f, 1f));
        return ApplyWaterValueNoise(color, worldX, worldZ, height);
    }

    private int ApplyWaterValueNoise(int color, int worldX, int worldZ, int height)
    {
        if (waterNoiseStrength <= 0f)
        {
            return color;
        }

        uint hash = Mix((uint)worldX, (uint)worldZ, (uint)height);
        float valueNoise = ((hash >> 16) & 0xFF) / 255f - 0.5f;
        float multiplier = Math.Clamp(1f + valueNoise * 2f * waterNoiseStrength, 0f, 2f);
        int r = Math.Clamp((int)MathF.Round((color & 0xFF) * multiplier), 0, 255);
        int g = Math.Clamp((int)MathF.Round(((color >> 8) & 0xFF) * multiplier), 0, 255);
        int b = Math.Clamp((int)MathF.Round(((color >> 16) & 0xFF) * multiplier), 0, 255);
        return unchecked((int)0xFF000000) | (b << 16) | (g << 8) | r;
    }

    private static int ParseMapColor(string? value, int fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        string hex = value.Trim();
        if (hex.StartsWith("#", StringComparison.Ordinal))
        {
            hex = hex[1..];
        }

        if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
        {
            int r = (rgb >> 16) & 0xFF;
            int g = (rgb >> 8) & 0xFF;
            int b = rgb & 0xFF;
            return unchecked((int)0xFF000000) | (b << 16) | (g << 8) | r;
        }

        return fallback;
    }

    private int BlendGrassSpectrumColor(int color, uint hash, float dryWeight, float lushWeight)
    {
        dryWeight = Math.Clamp(dryWeight, 0f, 1f);
        lushWeight = Math.Clamp(lushWeight, 0f, 1f);
        float total = dryWeight + lushWeight;
        if (total <= 0.001f)
        {
            return color;
        }

        if (total > 1f)
        {
            dryWeight /= total;
            lushWeight /= total;
        }

        if (dryGrass.Length > 0 && dryWeight > 0f)
        {
            int dryColor = dryGrass[(int)((hash >> 8) % (uint)dryGrass.Length)];
            color = BlendColor(color, dryColor, dryWeight);
        }

        if (lushGrass.Length > 0 && lushWeight > 0f)
        {
            int lushColor = lushGrass[(int)((hash >> 16) % (uint)lushGrass.Length)];
            color = BlendColor(color, lushColor, lushWeight);
        }

        return color;
    }

    private static int BlendColor(int from, int to, float weight)
    {
        weight = Math.Clamp(weight, 0f, 1f);
        int r = (int)MathF.Round((from & 0xFF) + ((to & 0xFF) - (from & 0xFF)) * weight);
        int g = (int)MathF.Round(((from >> 8) & 0xFF) + (((to >> 8) & 0xFF) - ((from >> 8) & 0xFF)) * weight);
        int b = (int)MathF.Round(((from >> 16) & 0xFF) + (((to >> 16) & 0xFF) - ((from >> 16) & 0xFF)) * weight);
        return unchecked((int)0xFF000000) | (b << 16) | (g << 8) | r;
    }

    public bool TryGetSnowColor(int height, int worldX, int worldZ, out int color)
    {
        if (snow.Length == 0)
        {
            color = 0;
            return false;
        }

        uint hash = Mix((uint)worldX, (uint)worldZ, (uint)height);
        color = snow[hash % (uint)snow.Length];
        return true;
    }

    public bool TryGetBrownColor(int height, int worldX, int worldZ, out int color)
    {
        if (brown.Length == 0)
        {
            color = 0;
            return false;
        }

        uint hash = Mix((uint)worldX, (uint)worldZ, (uint)height);
        color = brown[hash % (uint)brown.Length];
        return true;
    }

    public bool TryGetTreeColor(int height, int worldX, int worldZ, uint salt, out int color)
    {
        if (trees.Length == 0)
        {
            color = 0;
            return false;
        }

        uint hash = Mix((uint)worldX ^ salt, (uint)worldZ, (uint)height);
        color = trees[hash % (uint)trees.Length];
        return true;
    }

    public bool TryGetSeasonalGrassTint(float yearRel, float yRel, float hemisphereOffset, out int color)
    {
        if (seasonalGrassTint == null)
        {
            color = 0;
            return false;
        }

        color = seasonalGrassTint.Sample(yearRel, yRel, hemisphereOffset);
        return true;
    }

    public bool TryGetClimatePlantTint(int rain, int unscaledTemperature, int heightAboveSeaLevel, out int color)
    {
        if (climatePlantTint == null)
        {
            color = 0;
            return false;
        }

        color = climatePlantTint.Sample(rain, unscaledTemperature, heightAboveSeaLevel);
        return true;
    }

    public bool TryGetClimatePlantTintAdjusted(int rain, int adjustedTemperature, out int color)
    {
        if (climatePlantTint == null)
        {
            color = 0;
            return false;
        }

        color = climatePlantTint.SampleAdjusted(rain, adjustedTemperature);
        return true;
    }

    private static int[] LoadPalette(ICoreAPI api, string directory, string filename, bool warnIfMissing = true)
    {
        string path = Path.Combine(directory, filename);
        if (!File.Exists(path))
        {
            if (warnIfMissing)
            {
                api.Logger.Warning("[FastMap] Terrain fallback palette file not found: {0}", path);
            }

            return Array.Empty<int>();
        }

        try
        {
            return PngPaletteReader.ReadPalette(path);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[FastMap] Failed to read terrain fallback palette {0}: {1}", path, ex.Message);
            return Array.Empty<int>();
        }
    }

    private static int[] LoadTexturePalette(ICoreAPI api, string path, out string source)
    {
        source = "textures/" + path.Trim('/').Replace('\\', '/') + ".png";
        if (api is not ICoreClientAPI capi)
        {
            return Array.Empty<int>();
        }

        try
        {
            IAsset? asset = FindTextureAsset(api, path);
            if (asset == null)
            {
                return Array.Empty<int>();
            }

            source = asset.Location.ToShortString();
            using BitmapRef bitmap = asset.ToBitmap(capi);
            return BitmapToUniqueOpaquePalette(bitmap);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[FastMap] Failed to read terrain fallback texture palette textures/{0}.png: {1}", path, ex.Message);
            return Array.Empty<int>();
        }
    }

    private static int[] BitmapToUniqueOpaquePalette(BitmapRef bitmap)
    {
        int[] sourcePixels = bitmap.Pixels;
        List<int> colors = new();
        HashSet<int> seen = new();
        for (int i = 0; i < sourcePixels.Length; i++)
        {
            int sourceColor = sourcePixels[i];
            if (((sourceColor >> 24) & 0xFF) <= 8)
            {
                continue;
            }

            int color = SkColorToMapColor(sourceColor) | unchecked((int)0xFF000000);
            if (seen.Add(color))
            {
                colors.Add(color);
            }
        }

        return colors.ToArray();
    }

    private static ClimateTintMap? LoadClimateTintMap(ICoreAPI api, string path, int padding)
    {
        if (api is not ICoreClientAPI capi)
        {
            return null;
        }

        try
        {
            IAsset? asset = FindTextureAsset(api, path);
            if (asset == null)
            {
                api.Logger.Warning("[FastMap] Climate plant tint asset not found: textures/{0}.png", path);
                return null;
            }

            using BitmapRef bitmap = asset.ToBitmap(capi);
            int[] sourcePixels = bitmap.Pixels;
            int[] pixels = new int[sourcePixels.Length];
            for (int i = 0; i < sourcePixels.Length; i++)
            {
                pixels[i] = SkColorToMapColor(sourcePixels[i]);
            }

            return new ClimateTintMap(bitmap.Width, bitmap.Height, Math.Max(0, padding), pixels);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[FastMap] Failed to load climate plant tint asset textures/{0}.png: {1}", path, ex.Message);
            return null;
        }
    }

    private static SeasonalTintMap? LoadSeasonalTintMap(ICoreAPI api, string path)
    {
        if (api is not ICoreClientAPI capi)
        {
            return null;
        }

        try
        {
            IAsset? asset = FindTextureAsset(api, path);
            if (asset == null)
            {
                api.Logger.Warning("[FastMap] Seasonal grass tint asset not found: textures/{0}.png", path);
                return null;
            }

            using BitmapRef bitmap = asset.ToBitmap(capi);
            int[] sourcePixels = bitmap.Pixels;
            int[] pixels = new int[sourcePixels.Length];
            for (int i = 0; i < sourcePixels.Length; i++)
            {
                pixels[i] = SkColorToMapColor(sourcePixels[i]);
            }

            return new SeasonalTintMap(bitmap.Width, bitmap.Height, pixels);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[FastMap] Failed to load seasonal grass tint asset textures/{0}.png: {1}", path, ex.Message);
            return null;
        }
    }

    private static IAsset? FindTextureAsset(ICoreAPI api, string path)
    {
        string normalized = path.Replace('\\', '/').Trim('/');
        string withoutExtension = normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^4]
            : normalized;
        string withExtension = withoutExtension + ".png";
        string fullPath = "textures/" + withExtension;

        string?[] preferredDomains = { "survival", "game", null };
        for (int i = 0; i < preferredDomains.Length; i++)
        {
            string? domain = preferredDomains[i];
            IAsset? asset = domain != null
                ? api.Assets.TryGet(new AssetLocation(domain, fullPath), loadAsset: true)
                : api.Assets.TryGet(fullPath, loadAsset: true);
            if (asset != null)
            {
                return asset;
            }
        }

        for (int i = 0; i < preferredDomains.Length; i++)
        {
            string? domain = preferredDomains[i];
            List<IAsset> assets = api.Assets.GetManyInCategory("textures", withoutExtension, domain, loadAsset: true);
            for (int j = 0; j < assets.Count; j++)
            {
                IAsset asset = assets[j];
                if (IsTexturePathMatch(asset.Location.Path, fullPath, withExtension))
                {
                    return asset;
                }
            }
        }

        return null;
    }

    private static bool IsTexturePathMatch(string assetPath, string fullPath, string withExtension)
    {
        return assetPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase)
            || assetPath.Equals(withExtension, StringComparison.OrdinalIgnoreCase)
            || assetPath.EndsWith('/' + withExtension, StringComparison.OrdinalIgnoreCase);
    }

    private static int SkColorToMapColor(int color)
    {
        int a = (color >> 24) & 0xFF;
        int r = (color >> 16) & 0xFF;
        int g = (color >> 8) & 0xFF;
        int b = color & 0xFF;
        return (a << 24) | (b << 16) | (g << 8) | r;
    }

    private static uint Mix(uint x, uint z, uint height)
    {
        uint hash = 2166136261u;
        hash = (hash ^ x) * 16777619u;
        hash = (hash ^ z) * 16777619u;
        hash = (hash ^ height) * 16777619u;
        hash ^= hash >> 16;
        hash *= 2246822519u;
        hash ^= hash >> 13;
        hash *= 3266489917u;
        hash ^= hash >> 16;
        return hash;
    }

    private sealed class SeasonalTintMap
    {
        private readonly int[] pixels;

        public SeasonalTintMap(int width, int height, int[] pixels)
        {
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
            this.pixels = pixels;
        }

        public int Width { get; }

        public int Height { get; }

        public int Sample(float yearRel, float yRel, float hemisphereOffset)
        {
            float xRel = yearRel + hemisphereOffset;
            xRel -= MathF.Floor(xRel);
            return SampleBilinear(xRel * (Width - 1), Math.Clamp(yRel, 0f, 1f) * (Height - 1));
        }

        private int SampleBilinear(float x, float y)
        {
            int x0 = Math.Clamp((int)MathF.Floor(x), 0, Width - 1);
            int y0 = Math.Clamp((int)MathF.Floor(y), 0, Height - 1);
            int x1 = Math.Min(Width - 1, x0 + 1);
            int y1 = Math.Min(Height - 1, y0 + 1);
            float tx = x - x0;
            float ty = y - y0;
            return BlendBilinear(
                pixels[y0 * Width + x0],
                pixels[y0 * Width + x1],
                pixels[y1 * Width + x0],
                pixels[y1 * Width + x1],
                tx,
                ty);
        }
    }

    private sealed class ClimateTintMap
    {
        private readonly int padding;
        private readonly int[] pixels;

        public ClimateTintMap(int width, int height, int padding, int[] pixels)
        {
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
            this.padding = Math.Min(Math.Min(padding, (Width - 1) / 2), (Height - 1) / 2);
            this.pixels = pixels;
        }

        public int Width { get; }

        public int Height { get; }

        public int Sample(int rain, int unscaledTemperature, int heightAboveSeaLevel)
        {
            int adjustedTemperature = Math.Clamp(Climate.GetAdjustedTemperature(unscaledTemperature, heightAboveSeaLevel), 0, 255);
            return SampleAdjusted(rain, adjustedTemperature);
        }

        public int SampleAdjusted(int rain, int adjustedTemperature)
        {
            int innerWidth = Math.Max(1, Width - padding * 2);
            int innerHeight = Math.Max(1, Height - padding * 2);
            float x = Math.Clamp(adjustedTemperature, 0, 255) / 255f * (innerWidth - 1) + padding;
            float y = Math.Clamp(rain, 0, 255) / 255f * (innerHeight - 1) + padding;
            int x0 = Math.Clamp((int)MathF.Floor(x), 0, Width - 1);
            int y0 = Math.Clamp((int)MathF.Floor(y), 0, Height - 1);
            int x1 = Math.Min(Width - 1, x0 + 1);
            int y1 = Math.Min(Height - 1, y0 + 1);
            return BlendBilinear(
                pixels[y0 * Width + x0],
                pixels[y0 * Width + x1],
                pixels[y1 * Width + x0],
                pixels[y1 * Width + x1],
                x - x0,
                y - y0);
        }

        public int SampleAdjustedNearest(int rain, int adjustedTemperature)
        {
            float innerWidth = Math.Max(1, Width - padding * 2);
            float innerHeight = Math.Max(1, Height - padding * 2);
            int x = (int)Math.Clamp(Math.Clamp(adjustedTemperature, 0, 255) / 255f * innerWidth, -padding, Width - 1);
            int y = (int)Math.Clamp(Math.Clamp(rain, 0, 255) / 255f * innerHeight, -padding, Height - 1);
            return pixels[(y + padding) * Width + x + padding];
        }
    }

    private static int BlendBilinear(int c00, int c10, int c01, int c11, float tx, float ty)
    {
        int r = BlendBilinearChannel(c00 & 0xFF, c10 & 0xFF, c01 & 0xFF, c11 & 0xFF, tx, ty);
        int g = BlendBilinearChannel((c00 >> 8) & 0xFF, (c10 >> 8) & 0xFF, (c01 >> 8) & 0xFF, (c11 >> 8) & 0xFF, tx, ty);
        int b = BlendBilinearChannel((c00 >> 16) & 0xFF, (c10 >> 16) & 0xFF, (c01 >> 16) & 0xFF, (c11 >> 16) & 0xFF, tx, ty);
        int a = BlendBilinearChannel((c00 >> 24) & 0xFF, (c10 >> 24) & 0xFF, (c01 >> 24) & 0xFF, (c11 >> 24) & 0xFF, tx, ty);
        return (a << 24) | (b << 16) | (g << 8) | r;
    }

    private static int BlendBilinearChannel(int c00, int c10, int c01, int c11, float tx, float ty)
    {
        float top = c00 + (c10 - c00) * tx;
        float bottom = c01 + (c11 - c01) * tx;
        return Math.Clamp((int)MathF.Round(top + (bottom - top) * ty), 0, 255);
    }

    private static class PngPaletteReader
    {
        private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        public static int[] ReadPalette(string path)
        {
            using FileStream stream = File.OpenRead(path);
            Span<byte> signature = stackalloc byte[8];
            ReadExactly(stream, signature);
            if (!signature.SequenceEqual(Signature))
            {
                throw new InvalidDataException("Invalid PNG signature.");
            }

            int width = 0;
            int height = 0;
            byte bitDepth = 0;
            byte colorType = 0;
            byte[]? palette = null;
            byte[]? transparency = null;
            using MemoryStream compressed = new();

            while (true)
            {
                int length = ReadInt32BigEndian(stream);
                byte[] typeBytes = new byte[4];
                ReadExactly(stream, typeBytes);
                string type = System.Text.Encoding.ASCII.GetString(typeBytes);
                byte[] data = new byte[length];
                ReadExactly(stream, data);
                stream.Position += 4; // CRC

                if (type == "IHDR")
                {
                    width = ReadInt32BigEndian(data, 0);
                    height = ReadInt32BigEndian(data, 4);
                    bitDepth = data[8];
                    colorType = data[9];
                    if (data[10] != 0 || data[11] != 0 || data[12] != 0)
                    {
                        throw new NotSupportedException("Unsupported PNG compression, filter, or interlace method.");
                    }
                }
                else if (type == "PLTE")
                {
                    palette = data;
                }
                else if (type == "tRNS")
                {
                    transparency = data;
                }
                else if (type == "IDAT")
                {
                    compressed.Write(data, 0, data.Length);
                }
                else if (type == "IEND")
                {
                    break;
                }
            }

            if (width <= 0 || height <= 0)
            {
                throw new InvalidDataException("PNG is missing a valid IHDR chunk.");
            }

            int bytesPerPixel = BytesPerPixel(colorType, bitDepth);
            int stride = width * bytesPerPixel;
            byte[] raw = Inflate(compressed.ToArray(), (stride + 1) * height);
            byte[] pixels = Unfilter(raw, width, height, bytesPerPixel, stride);

            HashSet<int> colors = new();
            for (int y = 0; y < height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < width; x++)
                {
                    int offset = row + x * bytesPerPixel;
                    if (TryReadColor(pixels, offset, colorType, palette, transparency, out int color))
                    {
                        colors.Add(color);
                    }
                }
            }

            int[] result = new int[colors.Count];
            colors.CopyTo(result);
            return result;
        }

        private static byte[] Inflate(byte[] compressed, int capacity)
        {
            using MemoryStream input = new(compressed);
            using ZLibStream zlib = new(input, CompressionMode.Decompress);
            using MemoryStream output = new(capacity);
            zlib.CopyTo(output);
            return output.ToArray();
        }

        private static byte[] Unfilter(byte[] raw, int width, int height, int bytesPerPixel, int stride)
        {
            byte[] pixels = new byte[stride * height];
            int rawOffset = 0;
            for (int y = 0; y < height; y++)
            {
                int filter = raw[rawOffset++];
                int row = y * stride;
                int previousRow = row - stride;

                for (int x = 0; x < stride; x++)
                {
                    int value = raw[rawOffset++];
                    int left = x >= bytesPerPixel ? pixels[row + x - bytesPerPixel] : 0;
                    int up = y > 0 ? pixels[previousRow + x] : 0;
                    int upLeft = y > 0 && x >= bytesPerPixel ? pixels[previousRow + x - bytesPerPixel] : 0;

                    pixels[row + x] = filter switch
                    {
                        0 => (byte)value,
                        1 => unchecked((byte)(value + left)),
                        2 => unchecked((byte)(value + up)),
                        3 => unchecked((byte)(value + ((left + up) >> 1))),
                        4 => unchecked((byte)(value + Paeth(left, up, upLeft))),
                        _ => throw new InvalidDataException("Unsupported PNG filter.")
                    };
                }
            }

            return pixels;
        }

        private static bool TryReadColor(
            byte[] pixels,
            int offset,
            byte colorType,
            byte[]? palette,
            byte[]? transparency,
            out int color)
        {
            int r;
            int g;
            int b;
            int a = 255;

            switch (colorType)
            {
                case 2:
                    r = pixels[offset];
                    g = pixels[offset + 1];
                    b = pixels[offset + 2];
                    break;
                case 3:
                    if (palette == null)
                    {
                        throw new InvalidDataException("Indexed PNG is missing PLTE.");
                    }

                    int index = pixels[offset];
                    int paletteOffset = index * 3;
                    if (paletteOffset + 2 >= palette.Length)
                    {
                        color = 0;
                        return false;
                    }

                    r = palette[paletteOffset];
                    g = palette[paletteOffset + 1];
                    b = palette[paletteOffset + 2];
                    a = transparency != null && index < transparency.Length ? transparency[index] : 255;
                    break;
                case 6:
                    r = pixels[offset];
                    g = pixels[offset + 1];
                    b = pixels[offset + 2];
                    a = pixels[offset + 3];
                    break;
                default:
                    throw new NotSupportedException("Unsupported PNG colour type.");
            }

            if (a < 128)
            {
                color = 0;
                return false;
            }

            // Map pixels use Vintage Story's reversed byte order, matching ChunkMapLayer.hexColorsByCode.
            color = unchecked((int)0xFF000000) | (b << 16) | (g << 8) | r;
            return true;
        }

        private static int BytesPerPixel(byte colorType, byte bitDepth)
        {
            if (bitDepth != 8)
            {
                throw new NotSupportedException("Only 8-bit PNG palettes are supported.");
            }

            return colorType switch
            {
                2 => 3,
                3 => 1,
                6 => 4,
                _ => throw new NotSupportedException("Unsupported PNG colour type.")
            };
        }

        private static int Paeth(int left, int up, int upLeft)
        {
            int p = left + up - upLeft;
            int pa = Math.Abs(p - left);
            int pb = Math.Abs(p - up);
            int pc = Math.Abs(p - upLeft);
            return pa <= pb && pa <= pc ? left : pb <= pc ? up : upLeft;
        }

        private static int ReadInt32BigEndian(Stream stream)
        {
            Span<byte> bytes = stackalloc byte[4];
            ReadExactly(stream, bytes);
            return ReadInt32BigEndian(bytes, 0);
        }

        private static int ReadInt32BigEndian(ReadOnlySpan<byte> bytes, int offset)
        {
            return (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
        }

        private static void ReadExactly(Stream stream, Span<byte> buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer[total..]);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                total += read;
            }
        }

        private static void ReadExactly(Stream stream, byte[] buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer, total, buffer.Length - total);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                total += read;
            }
        }
    }
}
