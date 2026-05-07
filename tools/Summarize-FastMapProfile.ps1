param(
    [Parameter(Mandatory = $true)]
    [string]$Path
)

$rows = Import-Csv -LiteralPath $Path

if (-not $rows -or $rows.Count -eq 0) {
    Write-Host "No profiling rows found."
    exit 0
}

$rows |
    Group-Object stage |
    ForEach-Object {
        $durations = $_.Group |
            ForEach-Object { [double]$_.durationMs } |
            Where-Object { $_ -gt 0 } |
            Sort-Object

        $bytes = ($_.Group | Measure-Object -Property bytes -Sum).Sum
        $avg = if ($durations.Count -gt 0) { ($durations | Measure-Object -Average).Average } else { 0 }
        $sum = if ($durations.Count -gt 0) { ($durations | Measure-Object -Sum).Sum } else { 0 }
        $p95Index = if ($durations.Count -gt 0) { [Math]::Min($durations.Count - 1, [Math]::Floor($durations.Count * 0.95)) } else { 0 }
        $p95 = if ($durations.Count -gt 0) { $durations[$p95Index] } else { 0 }

        [PSCustomObject]@{
            stage = $_.Name
            count = $_.Count
            totalMs = [Math]::Round($sum, 3)
            avgMs = [Math]::Round($avg, 3)
            p95Ms = [Math]::Round($p95, 3)
            bytes = [long]$bytes
        }
    } |
    Sort-Object totalMs -Descending |
    Format-Table -AutoSize
