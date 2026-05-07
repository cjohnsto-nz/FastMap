param(
    [Parameter(Mandatory = $true)]
    [string]$Path
)

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

public static class FastMapRepairReasonSummaryTool
{
    private sealed class Stat
    {
        public string Stage = "";
        public string Detail = "";
        public int Count;
        public double TotalMs;
        public long Bytes;
    }

    public static string Summarize(string path)
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
            int detailIndex = IndexOf(columns, "detail");
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                string[] parts = SplitCsvLine(line);
                if (parts.Length <= Math.Max(Math.Max(stageIndex, durationIndex), Math.Max(bytesIndex, detailIndex)))
                {
                    continue;
                }

                string stage = parts[stageIndex];
                if (!stage.StartsWith("fastmap_chunk_repair_", StringComparison.Ordinal))
                {
                    continue;
                }

                string detail = parts[detailIndex];
                string key = stage + "|" + detail;
                Stat stat;
                if (!stats.TryGetValue(key, out stat))
                {
                    stat = new Stat { Stage = stage, Detail = detail };
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

        List<string> lines = new List<string>();
        lines.Add("stage|detail|count|totalMs|avgMs|bytes");
        foreach (Stat stat in stats.Values.OrderByDescending(stat => stat.Count).ThenByDescending(stat => stat.TotalMs))
        {
            double average = stat.Count == 0 ? 0 : stat.TotalMs / stat.Count;
            lines.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2}|{3:F3}|{4:F4}|{5}",
                stat.Stage,
                stat.Detail,
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

[FastMapRepairReasonSummaryTool]::Summarize($Path) |
    ConvertFrom-Csv -Delimiter '|' |
    Format-Table -AutoSize
