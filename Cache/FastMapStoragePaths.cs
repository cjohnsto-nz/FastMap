using System;
using System.IO;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace FastMap.Cache;

internal static class FastMapStoragePaths
{
    private const string ModDataDirectoryName = "ModData";
    private const string FastMapDirectoryName = "FastMap";

    public static string RootPath => Path.Combine(GamePaths.DataPath, ModDataDirectoryName, FastMapDirectoryName);

    public static string LegacyRootPath => Path.Combine(GamePaths.DataPath, FastMapDirectoryName);

    public static string GetWorldPath(string savegameIdentifier)
    {
        return Path.Combine(RootPath, SanitizePathPart(savegameIdentifier));
    }

    public static string GetProfilesPath(string savegameIdentifier)
    {
        return Path.Combine(RootPath, "profiles", SanitizePathPart(savegameIdentifier));
    }

    public static void MigrateLegacyRootIfNeeded(ICoreAPI api)
    {
        string legacyRoot = LegacyRootPath;
        string root = RootPath;
        if (!Directory.Exists(legacyRoot) || PathsEqual(legacyRoot, root))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(root)!);
            if (!Directory.Exists(root))
            {
                Directory.Move(legacyRoot, root);
                api.Logger.Notification("[FastMap] Migrated cache data from {0} to {1}.", legacyRoot, root);
                return;
            }

            DirectoryMergeStats stats = new();
            MergeDirectoryCopyFirst(legacyRoot, root, stats);
            DeleteLegacyRootBestEffort(legacyRoot, stats);
            api.Logger.Notification(
                "[FastMap] Merged legacy cache data from {0} into {1}: copied={2}, skippedExisting={3}, deleted={4}, deleteFailures={5}.",
                legacyRoot,
                root,
                stats.FilesCopied,
                stats.FilesSkippedExisting,
                stats.FilesDeleted,
                stats.DeleteFailures);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[FastMap] Failed to migrate legacy cache data from {0} to {1}: {2}", legacyRoot, root, ex.Message);
        }
    }

    public static string SanitizePathPart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown-world";
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        char[] chars = value.ToCharArray();

        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }

    private static void MergeDirectoryCopyFirst(string source, string destination, DirectoryMergeStats stats)
    {
        Directory.CreateDirectory(destination);

        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            string targetDirectory = Path.Combine(destination, Path.GetFileName(directory));
            MergeDirectoryCopyFirst(directory, targetDirectory, stats);
        }

        foreach (string file in Directory.EnumerateFiles(source))
        {
            string targetFile = Path.Combine(destination, Path.GetFileName(file));
            if (File.Exists(targetFile))
            {
                stats.FilesSkippedExisting++;
                continue;
            }

            File.Copy(file, targetFile);
            stats.FilesCopied++;
        }
    }

    private static void DeleteLegacyRootBestEffort(string legacyRoot, DirectoryMergeStats stats)
    {
        if (!Directory.Exists(legacyRoot))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(legacyRoot, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(file);
                stats.FilesDeleted++;
            }
            catch
            {
                stats.DeleteFailures++;
            }
        }

        try
        {
            Directory.Delete(legacyRoot, recursive: true);
        }
        catch
        {
            stats.DeleteFailures++;
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        string fullLeft = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullRight = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fullLeft, fullRight, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class DirectoryMergeStats
    {
        public int FilesCopied { get; set; }

        public int FilesSkippedExisting { get; set; }

        public int FilesDeleted { get; set; }

        public int DeleteFailures { get; set; }
    }
}
