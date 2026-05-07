param(
    [Parameter(Mandatory = $true)]
    [string]$Path,

    [switch]$IncludeInclusive
)

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

public static class FastMapProfileSummary
{
    private sealed class Stat
    {
        public string Stage = "";
        public string Kind = "";
        public string Category = "";
        public int Count;
        public double TotalMs;
        public long Bytes;
    }

    public static string Summarize(string path, bool includeInclusive)
    {
        Dictionary<string, Stat> stats = new Dictionary<string, Stat>(StringComparer.Ordinal);
        using (StreamReader reader = new StreamReader(path))
        {
            string header = reader.ReadLine();
            if (header == null)
            {
                return "No profiling rows found.";
            }

            string[] columns = SplitCsvLine(header);
            int stageIndex = IndexOf(columns, "stage");
            int durationIndex = IndexOf(columns, "durationMs");
            int bytesIndex = IndexOf(columns, "bytes");
            int kindIndex = IndexOf(columns, "kind");
            int categoryIndex = IndexOf(columns, "category");
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                string[] parts = SplitCsvLine(line);
                if (stageIndex < 0 || durationIndex < 0 || bytesIndex < 0 || parts.Length <= Math.Max(stageIndex, Math.Max(durationIndex, bytesIndex)))
                {
                    continue;
                }

                string kind = kindIndex >= 0 && kindIndex < parts.Length ? parts[kindIndex] : "exclusive";
                if (!includeInclusive && string.Equals(kind, "inclusive", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string stage = parts[stageIndex];
                string category = categoryIndex >= 0 && categoryIndex < parts.Length ? parts[categoryIndex] : "";
                string key = stage + "|" + kind + "|" + category;
                Stat stat;
                if (!stats.TryGetValue(key, out stat))
                {
                    stat = new Stat { Stage = stage, Kind = kind, Category = category };
                    stats[key] = stat;
                }

                double duration = 0;
                double.TryParse(parts[durationIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
                long bytes = 0;
                long.TryParse(parts[bytesIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out bytes);

                stat.Count++;
                stat.TotalMs += duration;
                stat.Bytes += bytes;
            }
        }

        List<Stat> ordered = stats.Values.OrderByDescending(stat => stat.TotalMs).Take(40).ToList();
        if (ordered.Count == 0)
        {
            return "No matching profiling rows found.";
        }

        List<string> lines = new List<string>();
        lines.Add("stage|kind|category|count|totalMs|avgMs|bytes");
        foreach (Stat stat in ordered)
        {
            double average = stat.Count == 0 ? 0 : stat.TotalMs / stat.Count;
            lines.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2}|{3}|{4:F3}|{5:F4}|{6}",
                stat.Stage,
                stat.Kind,
                stat.Category,
                stat.Count,
                stat.TotalMs,
                average,
                stat.Bytes));
        }

        return string.Join(Environment.NewLine, lines.ToArray());
    }

    private static int IndexOf(string[] columns, string name)
    {
        for (int i = 0; i < columns.Length; i++)
        {
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static string[] SplitCsvLine(string line)
    {
        List<string> values = new List<string>();
        bool inQuotes = false;
        System.Text.StringBuilder current = new System.Text.StringBuilder();
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                current.Length = 0;
            }
            else
            {
                current.Append(c);
            }
        }

        values.Add(current.ToString());
        return values.ToArray();
    }
}
'@

[FastMapProfileSummary]::Summarize($Path, $IncludeInclusive.IsPresent) |
    ConvertFrom-Csv -Delimiter '|' |
    Format-Table -AutoSize
