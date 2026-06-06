using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace FastMap;

internal readonly record struct FastMapModDetectionResult(bool Present, string Source, string Details);

internal static class FastMapModDetection
{
    private const BindingFlags InstanceMemberFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private const BindingFlags StaticMemberFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    public static bool IsModPresent(ICoreAPI api, string modId)
    {
        return DetectMod(api, modId).Present;
    }

    public static FastMapModDetectionResult DetectMod(ICoreAPI api, string modId)
    {
        List<string> clientMods = ReadClientModIds(api);
        if (ContainsModId(clientMods, modId))
        {
            return new FastMapModDetectionResult(true, "api.ModLoader.Mods", Summarize("client", clientMods));
        }

        if (InvokeBooleanModLoaderMethod(api.ModLoader, "IsModEnabled", modId))
        {
            return new FastMapModDetectionResult(true, "api.ModLoader.IsModEnabled", Summarize("client", clientMods));
        }

        if (InvokeObjectModLoaderMethod(api.ModLoader, "GetMod", modId) != null)
        {
            return new FastMapModDetectionResult(true, "api.ModLoader.GetMod", Summarize("client", clientMods));
        }

        List<string> loadedModIds = ReadLoadedModDictionaryIds(api.ModLoader);
        if (ContainsModId(loadedModIds, modId))
        {
            return new FastMapModDetectionResult(true, "api.ModLoader.loadedMods", Summarize("loaded", loadedModIds));
        }

        object? apiGame = ReadMember(api, "game");
        List<string> serverModsFromApiWorld = ReadServerModIds(api.World);
        if (ContainsModId(serverModsFromApiWorld, modId))
        {
            return new FastMapModDetectionResult(true, "api.World.ServerMods", Summarize("server", serverModsFromApiWorld));
        }

        List<string> serverModsFromClientWorld = ReadServerModIds(ReadMember(api, "World"));
        if (ContainsModId(serverModsFromClientWorld, modId))
        {
            return new FastMapModDetectionResult(true, "api.World property ServerMods", Summarize("server", serverModsFromClientWorld));
        }

        List<string> serverModsFromGame = ReadServerModIds(apiGame);
        if (ContainsModId(serverModsFromGame, modId))
        {
            return new FastMapModDetectionResult(true, "ClientCoreAPI.game.ServerMods", Summarize("server", serverModsFromGame));
        }

        List<string> crashReporterMods = ReadCrashReporterModIds();
        if (ContainsModId(crashReporterMods, modId))
        {
            return new FastMapModDetectionResult(true, "CrashReporter.LoadedMods", Summarize("crashReporter", crashReporterMods));
        }

        List<string> localPackageMods = IsSinglePlayer(api) ? ReadLocalPackageModIds(api.ModLoader) : new List<string>();
        if (ContainsModId(localPackageMods, modId))
        {
            return new FastMapModDetectionResult(true, "singleplayer local mod package", Summarize("localPackages", localPackageMods));
        }

        string details = string.Join(
            "; ",
            Summarize("client", clientMods),
            Summarize("loaded", loadedModIds),
            Summarize("server(api.World)", serverModsFromApiWorld),
            Summarize("server(api.World property)", serverModsFromClientWorld),
            Summarize("server(game)", serverModsFromGame),
            Summarize("crashReporter", crashReporterMods),
            Summarize("localPackages", localPackageMods));

        return new FastMapModDetectionResult(false, "not found", details);
    }

    private static List<string> ReadClientModIds(ICoreAPI api)
    {
        List<string> ids = new();
        foreach (Mod mod in api.ModLoader.Mods)
        {
            AddId(ids, mod.Info?.ModID);
        }

        return ids;
    }

    private static List<string> ReadLoadedModDictionaryIds(object? modLoader)
    {
        object? loadedMods = ReadMember(modLoader, "loadedMods");
        return ReadModIds(loadedMods);
    }

    private static List<string> ReadServerModIds(object? source)
    {
        object? serverMods = ReadMember(source, "ServerMods");
        return ReadModIds(serverMods);
    }

    private static List<string> ReadCrashReporterModIds()
    {
        Type? crashReporterType = FindType("Vintagestory.ClientNative.CrashReporter");
        object? loadedMods = ReadStaticMember(crashReporterType, "LoadedMods");
        return ReadModIds(loadedMods);
    }

    private static List<string> ReadLocalPackageModIds(object? modLoader)
    {
        List<string> ids = new();
        foreach (string path in ReadLocalModSearchPaths(modLoader))
        {
            if (!Directory.Exists(path))
            {
                continue;
            }

            foreach (string file in SafeEnumerateFiles(path, "*.zip", SearchOption.TopDirectoryOnly))
            {
                AddId(ids, TryReadZipModId(file));
            }

            foreach (string file in SafeEnumerateFiles(path, "modinfo.json", SearchOption.AllDirectories))
            {
                AddId(ids, TryReadModInfoFileModId(file));
            }
        }

        return ids;
    }

    private static List<string> ReadLocalModSearchPaths(object? modLoader)
    {
        List<string> paths = new();
        AddPath(paths, GamePaths.DataPathMods);
        AddPath(paths, Path.Combine(GamePaths.DataPath, "Mods"));
        AddPaths(paths, ReadMember(modLoader, "ModSearchPaths") as IEnumerable);
        return paths;
    }

    private static void AddPaths(List<string> paths, IEnumerable? source)
    {
        if (source == null)
        {
            return;
        }

        foreach (object? value in source)
        {
            AddPath(paths, value as string);
        }
    }

    private static void AddPath(List<string> paths, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string fullPath = Path.GetFullPath(path);
        foreach (string existing in paths)
        {
            if (string.Equals(existing, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        paths.Add(fullPath);
    }

    private static IEnumerable<string> SafeEnumerateFiles(string path, string searchPattern, SearchOption searchOption)
    {
        try
        {
            return Directory.EnumerateFiles(path, searchPattern, searchOption);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string? TryReadZipModId(string zipPath)
    {
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (!entry.FullName.EndsWith("modinfo.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using Stream stream = entry.Open();
                using StreamReader reader = new(stream);
                string? modId = TryReadModInfoJsonModId(reader.ReadToEnd());
                if (!string.IsNullOrWhiteSpace(modId))
                {
                    return modId;
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string? TryReadModInfoFileModId(string filePath)
    {
        try
        {
            return TryReadModInfoJsonModId(File.ReadAllText(filePath));
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadModInfoJsonModId(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            return TryGetStringProperty(root, "modid")
                ?? TryGetStringProperty(root, "modId")
                ?? TryGetStringProperty(root, "ModID");
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetStringProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static List<string> ReadModIds(object? source)
    {
        List<string> ids = new();
        if (source is string id)
        {
            AddId(ids, id);
            return ids;
        }

        if (source is IDictionary dictionary)
        {
            foreach (object? key in dictionary.Keys)
            {
                AddId(ids, key as string);
            }

            foreach (object? value in dictionary.Values)
            {
                AddId(ids, ReadModId(value));
            }

            return ids;
        }

        if (source is not IEnumerable enumerable)
        {
            AddId(ids, ReadModId(source));
            return ids;
        }

        foreach (object? item in enumerable)
        {
            AddId(ids, ReadModId(item));
        }

        return ids;
    }

    private static string? ReadModId(object? source)
    {
        if (source == null)
        {
            return null;
        }

        if (source is string id)
        {
            return id;
        }

        object? key = ReadMember(source, "Key");
        if (key is string keyString)
        {
            return keyString;
        }

        object? info = ReadMember(source, "Info");
        return ReadStringMember(info, "ModID")
            ?? ReadStringMember(info, "ModId")
            ?? ReadStringMember(info, "modid")
            ?? ReadStringMember(source, "ModID")
            ?? ReadStringMember(source, "ModId")
            ?? ReadStringMember(source, "modid")
            ?? ReadStringMember(source, "Id");
    }

    private static bool ContainsModId(List<string> ids, string modId)
    {
        foreach (string id in ids)
        {
            if (string.Equals(id, modId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddId(List<string> ids, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        if (!ContainsModId(ids, id))
        {
            ids.Add(id);
        }
    }

    private static bool InvokeBooleanModLoaderMethod(object? modLoader, string methodName, string modId)
    {
        return InvokeObjectModLoaderMethod(modLoader, methodName, modId) is true;
    }

    private static object? InvokeObjectModLoaderMethod(object? modLoader, string methodName, string modId)
    {
        if (modLoader == null)
        {
            return null;
        }

        try
        {
            MethodInfo? method = FindMethod(modLoader.GetType(), methodName, typeof(string));
            return method?.Invoke(modLoader, new object[] { modId });
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadStringMember(object? source, string name)
    {
        return ReadMember(source, name) as string;
    }

    private static object? ReadMember(object? source, string name)
    {
        if (source == null)
        {
            return null;
        }

        try
        {
            MemberInfo? member = FindMember(source.GetType(), name, InstanceMemberFlags);
            return ReadMemberValue(source, member);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSinglePlayer(ICoreAPI api)
    {
        return ReadMember(api, "IsSinglePlayer") is true;
    }

    private static object? ReadStaticMember(Type? type, string name)
    {
        if (type == null)
        {
            return null;
        }

        try
        {
            MemberInfo? member = FindMember(type, name, StaticMemberFlags);
            return ReadMemberValue(null, member);
        }
        catch
        {
            return null;
        }
    }

    private static object? ReadMemberValue(object? source, MemberInfo? member)
    {
        return member switch
        {
            FieldInfo field => field.GetValue(source),
            PropertyInfo property when property.GetIndexParameters().Length == 0 => property.GetValue(source),
            _ => null
        };
    }

    private static MemberInfo? FindMember(Type? type, string name, BindingFlags flags)
    {
        while (type != null)
        {
            FieldInfo? field = type.GetField(name, flags);
            if (field != null)
            {
                return field;
            }

            PropertyInfo? property = type.GetProperty(name, flags);
            if (property != null)
            {
                return property;
            }

            type = type.BaseType;
        }

        return null;
    }

    private static MethodInfo? FindMethod(Type? type, string name, params Type[] parameterTypes)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        while (type != null)
        {
            MethodInfo? method = type.GetMethod(name, flags, null, parameterTypes, null);
            if (method != null)
            {
                return method;
            }

            type = type.BaseType;
        }

        return null;
    }

    private static Type? FindType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType(fullName, false);
            if (type != null)
            {
                return type;
            }
        }

        return null;
    }

    private static string Summarize(string label, List<string> ids)
    {
        const int maxIds = 16;
        if (ids.Count == 0)
        {
            return label + "=0";
        }

        int count = Math.Min(ids.Count, maxIds);
        string summary = string.Join(",", ids.GetRange(0, count));
        if (ids.Count > maxIds)
        {
            summary += ",...";
        }

        return $"{label}={ids.Count}[{summary}]";
    }
}
