param(
    [ValidateSet("1.21", "1.22")]
    [string]$GameVersion = "1.22",

    [string]$Configuration = "Release",

    [switch]$NoLaunch,

    [switch]$NoCloseVS
)

# FastMap deployment script
# Stops the game unless -NoCloseVS is set, builds the mod, packages it into the VS Mods folder, and optionally relaunches the selected client.

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

function ConvertTo-WslPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if ($fullPath -notmatch '^[A-Za-z]:\\') {
        throw "Cannot convert non-drive-qualified path '$fullPath' to a WSL path."
    }

    $drive = $fullPath.Substring(0, 1).ToLowerInvariant()
    $pathWithoutDrive = $fullPath.Substring(2).Replace('\', '/')
    return "/mnt/$drive$pathWithoutDrive"
}

function Test-WslZipAvailable {
    if (-not (Get-Command 'wsl.exe' -ErrorAction SilentlyContinue)) {
        return $false
    }

    $processInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $processInfo.FileName = 'wsl.exe'
    $processInfo.Arguments = 'sh -lc "command -v zip >/dev/null 2>&1"'
    $processInfo.RedirectStandardOutput = $true
    $processInfo.RedirectStandardError = $true
    $processInfo.UseShellExecute = $false
    $processInfo.CreateNoWindow = $true

    $process = [System.Diagnostics.Process]::Start($processInfo)
    if ($null -eq $process) {
        return $false
    }

    if (-not $process.WaitForExit(3000)) {
        try {
            $process.Kill()
        } catch {
            # Best effort only; falling back to managed zip is safe.
        }

        return $false
    }

    return $process.ExitCode -eq 0
}

function New-WslZip {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory,

        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    $resolvedSource = (Resolve-Path -LiteralPath $SourceDirectory).Path
    $sourceWslPath = ConvertTo-WslPath -Path $resolvedSource
    $destinationWslPath = ConvertTo-WslPath -Path $DestinationPath

    & wsl.exe --cd $sourceWslPath zip -qr $destinationWslPath .
    if ($LASTEXITCODE -ne 0) {
        throw 'WSL zip failed.'
    }
}

function New-ManagedCrossPlatformZip {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory,

        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $resolvedSource = (Resolve-Path -LiteralPath $SourceDirectory).Path.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar
    )

    $archive = [System.IO.Compression.ZipFile]::Open(
        $DestinationPath,
        [System.IO.Compression.ZipArchiveMode]::Create
    )

    try {
        Get-ChildItem -LiteralPath $resolvedSource -File -Recurse | ForEach-Object {
            $relativePath = $_.FullName.Substring($resolvedSource.Length).TrimStart(
                [System.IO.Path]::DirectorySeparatorChar,
                [System.IO.Path]::AltDirectorySeparatorChar
            )
            $entryName = $relativePath.Replace('\', '/')

            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive,
                $_.FullName,
                $entryName,
                [System.IO.Compression.CompressionLevel]::Optimal
            ) | Out-Null
        }
    } finally {
        $archive.Dispose()
    }
}

function New-CrossPlatformZip {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory,

        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    if (Test-WslZipAvailable) {
        Write-Host 'Creating zip with WSL zip for Linux-compatible archive paths.' -ForegroundColor DarkGray
        New-WslZip -SourceDirectory $SourceDirectory -DestinationPath $DestinationPath
        return
    }

    Write-Host 'WSL zip is not available. Falling back to managed zip writer.' -ForegroundColor Yellow
    New-ManagedCrossPlatformZip -SourceDirectory $SourceDirectory -DestinationPath $DestinationPath
}

Write-Host 'Checking for running Vintage Story process...' -ForegroundColor Cyan
$vsProcess = Get-Process -Name $VSProcessName -ErrorAction SilentlyContinue
if ($vsProcess -and $NoCloseVS) {
    Write-Host 'Vintage Story is running. Leaving it open because -NoCloseVS was specified.'
} elseif ($vsProcess) {
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
New-CrossPlatformZip -SourceDirectory $TempDir -DestinationPath $ZipFilePath

if (Test-Path $ModConfigPath) {
    Write-Host "Removing stale mod config '$ModConfigPath'..." -ForegroundColor Cyan
    Remove-Item -LiteralPath $ModConfigPath -Force
}

if (-not $NoLaunch -and -not ($NoCloseVS -and $vsProcess)) {
    Write-Host "`nDeployment complete. Launching Vintage Story $GameVersion..." -ForegroundColor Green
    Start-Process -FilePath $VSExePath
} elseif ($NoCloseVS -and $vsProcess) {
    Write-Host "`nDeployment complete. Vintage Story was left running, so launch was skipped." -ForegroundColor Green
} else {
    Write-Host "`nDeployment complete. Launch skipped for Vintage Story $GameVersion." -ForegroundColor Green
}
