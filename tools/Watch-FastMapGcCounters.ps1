param(
    [int]$RefreshSeconds = 1
)

$ErrorActionPreference = 'Stop'

$tool = Join-Path $PSScriptRoot '..\.tools\dotnet-counters.exe'
if (-not (Test-Path -LiteralPath $tool)) {
    throw "dotnet-counters was not found at '$tool'. Run: dotnet tool install dotnet-counters --tool-path .\.tools"
}

$process = Get-Process -Name Vintagestory -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $process) {
    throw 'Vintage Story is not running.'
}

Write-Host "Monitoring GC counters for Vintagestory.exe pid=$($process.Id). Press Ctrl+C to stop." -ForegroundColor Cyan
& $tool monitor `
    --process-id $process.Id `
    --refresh-interval $RefreshSeconds `
    System.Runtime
