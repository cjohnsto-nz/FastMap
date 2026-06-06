# Vintage Story Multi-Version Setup

This repo can build against different Vintage Story installs by selecting an explicit game version at build time.

## Recommended layout

Keep each game version in its own install folder, for example:

- `C:\Games\VintageStory\1.21.7`
- `C:\Games\VintageStory\1.22.0`

Avoid relying on `C:\Users\<you>\AppData\Roaming\Vintagestory` as the only reference path when you need back-compat builds, because whichever version is installed there becomes your compile target.

## One-time setup

Register your install folders:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Set-VintageStoryEnv.ps1 -Version 1.21 -InstallPath "C:\Users\chris\AppData\Roaming\Vintagestory\1.21.7"
powershell -ExecutionPolicy Bypass -File .\tools\Set-VintageStoryEnv.ps1 -Version 1.22 -InstallPath "C:\Users\chris\AppData\Roaming\Vintagestory"
```

Open a new terminal after setting them so PowerShell picks them up.

## Build commands

```powershell
.\tools\Build-VintageStory.ps1 -GameVersion 1.21
.\tools\Build-VintageStory.ps1 -GameVersion 1.22
```

## Deploy commands

```powershell
powershell -ExecutionPolicy Bypass -File .\deploy.ps1 -GameVersion 1.21
powershell -ExecutionPolicy Bypass -File .\deploy.ps1 -GameVersion 1.22
```
