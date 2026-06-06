param(
    [ValidateSet("1.21", "1.22")]
    [string]$GameVersion = "1.22",

    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$versionKey = $GameVersion -replace "\.", ""
$pathVarName = "VINTAGE_STORY_$versionKey"
$gamePath = [Environment]::GetEnvironmentVariable($pathVarName, "User")

if ([string]::IsNullOrWhiteSpace($gamePath)) {
    throw "Environment variable '$pathVarName' is not set. Run tools/Set-VintageStoryEnv.ps1 first."
}

if (-not (Test-Path -LiteralPath $gamePath)) {
    throw "Configured game path '$gamePath' does not exist anymore."
}

Write-Host "Building against Vintage Story $GameVersion" -ForegroundColor Cyan
Write-Host "Using game path: $gamePath" -ForegroundColor Cyan

dotnet build "-p:GameVersion=$GameVersion" "-p:GamePath=$gamePath" "-c" $Configuration
