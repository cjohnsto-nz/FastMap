param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("1.21", "1.22")]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$InstallPath
)

$ErrorActionPreference = "Stop"

$resolvedPath = (Resolve-Path -LiteralPath $InstallPath).Path
$apiPath = Join-Path $resolvedPath "VintagestoryAPI.dll"

if (-not (Test-Path -LiteralPath $apiPath)) {
    throw "Could not find VintagestoryAPI.dll in '$resolvedPath'. Point this script at a full Vintage Story install folder."
}

$envName = "VINTAGE_STORY_" + ($Version -replace "\.", "")
[Environment]::SetEnvironmentVariable($envName, $resolvedPath, "User")

Write-Host "Saved $envName=$resolvedPath" -ForegroundColor Green
Write-Host "You can now build with: dotnet build -p:GameVersion=$Version" -ForegroundColor Cyan
