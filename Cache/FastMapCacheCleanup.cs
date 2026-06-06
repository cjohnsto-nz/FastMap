using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace FastMap.Cache;

internal static partial class FastMapCacheCleanup
{
    public static FastMapCacheCleanupResult CleanupVersionedPageCaches(bool keepLatestPageVersion)
    {
        string rootPath = FastMapStoragePaths.RootPath;
        FastMapCacheCleanupResult result = new();
        if (!Directory.Exists(rootPath))
        {
            return result;
        }

        foreach (string worldCachePath in Directory.EnumerateDirectories(rootPath))
        {
            result.WorldCachesScanned++;
            List<PageVersionDirectory> pageVersionDirectories = FindPageVersionDirectories(worldCachePath);
            if (pageVersionDirectories.Count == 0)
            {
                continue;
            }

            int latestVersion = -1;
            foreach (PageVersionDirectory directory in pageVersionDirectories)
            {
                latestVersion = Math.Max(latestVersion, directory.Version);
            }

            foreach (PageVersionDirectory directory in pageVersionDirectories)
            {
                if (keepLatestPageVersion && directory.Version == latestVersion)
                {
                    result.PageVersionFoldersKept++;
                    continue;
                }

                DeleteDirectory(directory.Path, result);
            }
        }

        return result;
    }

    private static List<PageVersionDirectory> FindPageVersionDirectories(string worldCachePath)
    {
        List<PageVersionDirectory> directories = new();
        foreach (string directoryPath in Directory.EnumerateDirectories(worldCachePath))
        {
            string name = Path.GetFileName(directoryPath);
            Match match = PageVersionDirectoryRegex().Match(name);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out int version))
            {
                continue;
            }

            directories.Add(new PageVersionDirectory(directoryPath, version));
        }

        return directories;
    }

    private static void DeleteDirectory(string directoryPath, FastMapCacheCleanupResult result)
    {
        DirectoryInfo directory = new(directoryPath);
        if (!directory.Exists)
        {
            return;
        }

        DirectoryStats stats = MeasureDirectory(directory);
        try
        {
            directory.Delete(recursive: true);
            result.PageVersionFoldersDeleted++;
            result.FilesDeleted += stats.Files;
            result.BytesFreed += stats.Bytes;
        }
        catch (Exception ex)
        {
            result.Failures++;
            result.FailureMessages.Add($"{directory.FullName}: {ex.Message}");
        }
    }

    private static DirectoryStats MeasureDirectory(DirectoryInfo directory)
    {
        long files = 0;
        long bytes = 0;
        foreach (FileInfo file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            files++;
            bytes += file.Length;
        }

        return new DirectoryStats(files, bytes);
    }

    [GeneratedRegex(@"^pages-v(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PageVersionDirectoryRegex();

    private readonly record struct PageVersionDirectory(string Path, int Version);

    private readonly record struct DirectoryStats(long Files, long Bytes);
}

internal sealed class FastMapCacheCleanupResult
{
    public int WorldCachesScanned { get; set; }

    public int PageVersionFoldersDeleted { get; set; }

    public int PageVersionFoldersKept { get; set; }

    public long FilesDeleted { get; set; }

    public long BytesFreed { get; set; }

    public int Failures { get; set; }

    public List<string> FailureMessages { get; } = new();

    public string ToSummary(bool keepLatestPageVersion)
    {
        string keptText = keepLatestPageVersion
            ? $"{PageVersionFoldersKept} latest page-version folder(s) kept. "
            : "Latest page-version folders were not excluded. ";

        string summary = $"FastMap cleanup complete. Scanned {WorldCachesScanned} world cache(s). Deleted {PageVersionFoldersDeleted} page-version folder(s), {FilesDeleted} file(s), freed {FormatBytes(BytesFreed)}. {keptText}";
        if (Failures > 0)
        {
            summary += $"{Failures} folder(s) could not be deleted; see client-main.log for details.";
        }

        return summary;
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int suffix = 0;
        while (value >= 1024 && suffix < suffixes.Length - 1)
        {
            value /= 1024;
            suffix++;
        }

        return suffix == 0 ? $"{bytes} {suffixes[suffix]}" : $"{value:0.##} {suffixes[suffix]}";
    }
}
