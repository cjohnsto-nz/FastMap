using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using Vintagestory.API.Common;

namespace FastMap.Map;

internal sealed class FastMapFallbackPalette
{
    private readonly int[] grass;
    private readonly int[] dryGrass;
    private readonly int[] lushGrass;
    private readonly int[] trees;
    private readonly int[] water;
    private readonly int[] snow;
    private readonly int[] brown;

    private FastMapFallbackPalette(int[] grass, int[] dryGrass, int[] lushGrass, int[] trees, int[] water, int[] snow, int[] brown)
    {
        this.grass = grass;
        this.dryGrass = dryGrass;
        this.lushGrass = lushGrass;
        this.trees = trees;
        this.water = water;
        this.snow = snow;
        this.brown = brown;
    }

    public bool HasGrass => grass.Length > 0;
    public bool HasDryGrass => dryGrass.Length > 0;
    public bool HasLushGrass => lushGrass.Length > 0;
    public bool HasTrees => trees.Length > 0;
    public bool HasWater => water.Length > 0;
    public bool HasSnow => snow.Length > 0;
    public bool HasBrown => brown.Length > 0;
    public bool HasAny => HasGrass || HasWater || HasSnow;

    public static FastMapFallbackPalette Load(ICoreAPI api)
    {
        string assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;
        int[] grass = LoadPalette(api, assemblyDirectory, "grass.png");
        int[] dryGrass = LoadPalette(api, assemblyDirectory, "drygrass.png", warnIfMissing: false);
        int[] lushGrass = LoadPalette(api, assemblyDirectory, "lushgrass.png", warnIfMissing: false);
        int[] trees = LoadPalette(api, assemblyDirectory, "trees.png", warnIfMissing: false);
        int[] water = LoadPalette(api, assemblyDirectory, "water.png");
        int[] snow = LoadPalette(api, assemblyDirectory, "snow.png");
        int[] brown = LoadPalette(api, assemblyDirectory, "map-bkg.png", warnIfMissing: false);

        api.Logger.Notification(
            "[FastMap] Terrain fallback palettes loaded: grass={0}, dryGrass={1}, lushGrass={2}, trees={3}, water={4}, snow={5}, brown={6}, path={7}",
            grass.Length,
            dryGrass.Length,
            lushGrass.Length,
            trees.Length,
            water.Length,
            snow.Length,
            brown.Length,
            assemblyDirectory);

        return new FastMapFallbackPalette(grass, dryGrass, lushGrass, trees, water, snow, brown);
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
