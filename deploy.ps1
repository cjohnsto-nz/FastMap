param(
    [ValidateSet("1.21", "1.22")]
    [string]$GameVersion = "1.22",

    [string]$Configuration = "Release",

    [switch]$NoLaunch
)

# FastMap deployment script
# Stops the game, builds the mod, packages it into the VS Mods folder, and optionally relaunches the selected client.

$ErrorActionPreference = 'Stop'

$ProjectName = 'FastMap'
$ProjectRoot = $PSScriptRoot
$ProjectFile = Join-Path $ProjectRoot 'FastMap.csproj'
$ModsDir = 'C:\Users\chris\AppData\Roaming\VintagestoryData\Mods'
$VSProcessName = 'Vintagestory'
$VersionKey = $GameVersion -replace "\.", ""
$VersionEnvName = "VINTAGE_STORY_$VersionKey"
$ConfiguredGamePath = [Environment]::GetEnvironmentVariable($VersionEnvName, "User")

if ([string]::IsNullOrWhiteSpace($ConfiguredGamePath)) {
    throw "Environment variable '$VersionEnvName' is not set. Run tools/Set-VintageStoryEnv.ps1 for version $GameVersion first."
}

if (-not (Test-Path -LiteralPath $ConfiguredGamePath)) {
    throw "Configured game path '$ConfiguredGamePath' does not exist."
}

$VSExePath = Join-Path $ConfiguredGamePath 'Vintagestory.exe'
$SourceDir = Join-Path $ProjectRoot "bin\$Configuration\ModPackage\$ProjectName"
$TempDir = Join-Path $env:TEMP 'FastMapTempDeploy'
$ModConfigPath = 'C:\Users\chris\AppData\Roaming\VintagestoryData\ModConfig\fastmap.json'

Write-Host 'Checking for running Vintage Story process...' -ForegroundColor Cyan
$vsProcess = Get-Process -Name $VSProcessName -ErrorAction SilentlyContinue
if ($vsProcess) {
    Write-Host 'Vintage Story is running. Stopping process...'
    Stop-Process -Name $VSProcessName -Force
    Start-Sleep -Seconds 2
}

Write-Host 'Removing old build artifacts...' -ForegroundColor Cyan
if (Test-Path (Join-Path $ProjectRoot 'bin')) { Remove-Item -LiteralPath (Join-Path $ProjectRoot 'bin') -Recurse -Force }
if (Test-Path (Join-Path $ProjectRoot 'obj')) { Remove-Item -LiteralPath (Join-Path $ProjectRoot 'obj') -Recurse -Force }

Write-Host 'Cleaning project...' -ForegroundColor Cyan
dotnet clean $ProjectFile -c $Configuration "-p:GameVersion=$GameVersion" "-p:GamePath=$ConfiguredGamePath"
if ($LASTEXITCODE -ne 0) {
    throw 'dotnet clean failed.'
}

Write-Host "Building FastMap against Vintage Story $GameVersion..." -ForegroundColor Cyan
dotnet build $ProjectFile -c $Configuration "-p:GameVersion=$GameVersion" "-p:GamePath=$ConfiguredGamePath"
if ($LASTEXITCODE -ne 0) {
    throw 'dotnet build failed.'
}

if (-not (Test-Path $SourceDir)) {
    throw "Packaged mod directory not found at '$SourceDir'."
}

$ModInfoPath = Join-Path $SourceDir 'modinfo.json'
$Version = '0.0.0'
$ModId = 'fastmap'
if (Test-Path $ModInfoPath) {
    $modInfo = Get-Content -Raw $ModInfoPath | ConvertFrom-Json
    $Version = $modInfo.version
    if (-not [string]::IsNullOrWhiteSpace($modInfo.modid)) {
        $ModId = $modInfo.modid
    }
}

$ZipFileName = "${ModId}_${Version}.zip"
$ZipFilePath = Join-Path $ModsDir $ZipFileName

Write-Host "Deploying mod as '$ZipFileName' to '$ModsDir'..." -ForegroundColor Cyan

Get-ChildItem -LiteralPath $ModsDir -Filter "${ModId}_*.zip" -ErrorAction SilentlyContinue |
    Remove-Item -Force

if (Test-Path $TempDir) {
    Remove-Item -LiteralPath $TempDir -Recurse -Force
}

New-Item -ItemType Directory -Path $TempDir | Out-Null
Copy-Item -Path (Join-Path $SourceDir '*') -Destination $TempDir -Recurse

if (Test-Path $ZipFilePath) {
    Remove-Item -LiteralPath $ZipFilePath -Force
}

Write-Host "Creating zip file '$ZipFilePath'..." -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $TempDir '*') -DestinationPath $ZipFilePath

if (Test-Path $ModConfigPath) {
    Write-Host "Removing stale mod config '$ModConfigPath'..." -ForegroundColor Cyan
    Remove-Item -LiteralPath $ModConfigPath -Force
}

if (-not $NoLaunch) {
    Write-Host "`nDeployment complete. Launching Vintage Story $GameVersion..." -ForegroundColor Green
    Start-Process -FilePath $VSExePath
} else {
    Write-Host "`nDeployment complete. Launch skipped for Vintage Story $GameVersion." -ForegroundColor Green
}
