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

public static class FastMapWorldgenDelegateSummary
{
    private sealed class Stat
    {
        public string Detail = "";
        public int Count;
        public double TotalMs;
        public List<double> Durations = new List<double>();
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
            int detailIndex = IndexOf(columns, "detail");
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                string[] parts = SplitCsvLine(line);
                if (parts.Length <= Math.Max(stageIndex, Math.Max(durationIndex, detailIndex)))
                {
                    continue;
                }

                if (!string.Equals(parts[stageIndex], "server_worldgen_delegate", StringComparison.Ordinal))
                {
                    continue;
                }

                string detail = parts[detailIndex];
                double duration = 0;
                double.TryParse(parts[durationIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out duration);

                Stat stat;
                if (!stats.TryGetValue(detail, out stat))
                {
                    stat = new Stat { Detail = detail };
                    stats[detail] = stat;
                }

                stat.Count++;
                stat.TotalMs += duration;
                stat.Durations.Add(duration);
            }
        }

        double totalMs = stats.Values.Sum(stat => stat.TotalMs);
        List<Stat> ordered = stats.Values.OrderByDescending(stat => stat.TotalMs).ToList();
        if (ordered.Count == 0)
        {
            return "No server_worldgen_delegate rows found.";
        }

        List<string> lines = new List<string>();
        lines.Add("pass|handler|count|totalMs|sharePct|avgMs|p95Ms|p99Ms");
        foreach (Stat stat in ordered)
        {
            stat.Durations.Sort();
            double average = stat.Count == 0 ? 0 : stat.TotalMs / stat.Count;
            double share = totalMs <= 0 ? 0 : 100.0 * stat.TotalMs / totalMs;
            lines.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2}|{3:F3}|{4:F2}|{5:F4}|{6:F4}|{7:F4}",
                Extract(stat.Detail, "pass="),
                Extract(stat.Detail, "handler="),
                stat.Count,
                stat.TotalMs,
                share,
                average,
                Percentile(stat.Durations, 0.95),
                Percentile(stat.Durations, 0.99)));
        }

        return string.Join(Environment.NewLine, lines.ToArray());
    }

    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        int index = (int)Math.Ceiling(values.Count * percentile) - 1;
        index = Math.Max(0, Math.Min(values.Count - 1, index));
        return values[index];
    }

    private static string Extract(string detail, string key)
    {
        int start = detail.IndexOf(key, StringComparison.Ordinal);
        if (start < 0)
        {
            return "";
        }

        start += key.Length;
        int end = detail.IndexOf(';', start);
        return end < 0 ? detail.Substring(start) : detail.Substring(start, end - start);
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

[FastMapWorldgenDelegateSummary]::Summarize($Path) |
    ConvertFrom-Csv -Delimiter '|' |
    Format-Table -AutoSize
