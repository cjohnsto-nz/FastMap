param(
    [int]$DurationSeconds = 180,
    [string]$OutputDirectory = "benchmarks"
)

$ErrorActionPreference = 'Stop'

$tool = Join-Path $PSScriptRoot '..\.tools\dotnet-trace.exe'
if (-not (Test-Path -LiteralPath $tool)) {
    throw "dotnet-trace was not found at '$tool'. Run: dotnet tool install dotnet-trace --tool-path .\.tools"
}

$process = Get-Process -Name Vintagestory -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $process) {
    throw 'Vintage Story is not running.'
}

$root = Split-Path -Parent $PSScriptRoot
$outputPath = Join-Path $root $OutputDirectory
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$tracePath = Join-Path $outputPath "fastmap-gc-$timestamp.nettrace"
$duration = [TimeSpan]::FromSeconds($DurationSeconds).ToString('c')

Write-Host "Capturing GC/allocation trace for Vintagestory.exe pid=$($process.Id) for $DurationSeconds seconds." -ForegroundColor Cyan
Write-Host "Output: $tracePath" -ForegroundColor Cyan

& $tool collect `
    --process-id $process.Id `
    --profile gc-verbose `
    --duration $duration `
    --output $tracePath

Write-Host "Trace saved to $tracePath" -ForegroundColor Green
