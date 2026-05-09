param(
    [string]$OutputDirectory = "benchmarks"
)

$ErrorActionPreference = 'Stop'

$tool = Join-Path $PSScriptRoot '..\.tools\dotnet-gcdump.exe'
if (-not (Test-Path -LiteralPath $tool)) {
    throw "dotnet-gcdump was not found at '$tool'. Run: dotnet tool install dotnet-gcdump --tool-path .\.tools"
}

$process = Get-Process -Name Vintagestory -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $process) {
    throw 'Vintage Story is not running.'
}

$root = Split-Path -Parent $PSScriptRoot
$outputPath = Join-Path $root $OutputDirectory
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$dumpPath = Join-Path $outputPath "fastmap-gc-$timestamp.gcdump"

Write-Host "Capturing GC heap snapshot for Vintagestory.exe pid=$($process.Id)." -ForegroundColor Cyan
Write-Host "Output: $dumpPath" -ForegroundColor Cyan

& $tool collect `
    --process-id $process.Id `
    --output $dumpPath

Write-Host "GC dump saved to $dumpPath" -ForegroundColor Green
