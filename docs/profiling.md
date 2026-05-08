# FastMap Profiling Guide

FastMap release builds intentionally do not include profiling command registration, Harmony profiling patches, or server-side profiling hooks. This keeps the public mod client-side only and prevents disabled profiling paths from affecting JIT/runtime behavior.

Use this guide when you need to re-enable profiling for a development build.

## Release Defaults

- `modinfo.json` uses `"side": "Client"`.
- `deploy.ps1` builds `Release`.
- `Config/FastMapConfig.AutoStartProfilingOnStartup` defaults to `false`.
- `FASTMAPPROFILING` is defined only for `Debug` builds in `FastMap.csproj`.
- `Profiling/FastMapProfilingModSystem.cs`, `Profiling/FastMapHarmonyPatches.cs`, and `Profiling/FastMapWorldgenDelegateProfiler.cs` are compiled only when `FASTMAPPROFILING` is defined.
- Release `FastMapProfileRecorder` is a no-op shim. `RecordClient(...)` and `RecordServer(...)` are `[Conditional("FASTMAPPROFILING")]`, so Release callsites do not evaluate profiling arguments.

## Client-Only Profiling

Use this for FastMap map-layer, cache, and client packet timing without making the mod server-side.

1. Build a Debug package:

   ```powershell
   dotnet build -c Debug
   ```

2. Package/deploy from:

   ```text
   bin\Debug\ModPackage\FastMap
   ```

3. Keep `modinfo.json` as `"side": "Client"`.

4. In `VintagestoryData\ModConfig\fastmap.json`, set one of:

   ```json
   {
     "EnableProfiling": true,
     "AutoStartProfilingOnStartup": true
   }
   ```

   `AutoStartProfilingOnStartup` is useful when cold-start map loading must be captured before manual commands are available.

5. In-game client commands:

   ```text
   .fastmapprofile start
   .fastmapprofile status
   .fastmapprofile flush
   .fastmapprofile stop
   ```

6. Profiles are written to:

   ```text
   VintagestoryData\FastMap\profiles\<save-id>\fastmap-profile-client-<timestamp>.csv
   ```

## Full Client + Server Profiling

Use this only for lab builds. Do not ship this configuration.

1. Change `modinfo.json` temporarily:

   ```json
   "side": "Universal"
   ```

2. Re-enable server loading for the profiling ModSystem in `Profiling/FastMapProfilingModSystem.cs`.

   Current release-hardened code contains:

   ```csharp
   public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;
   ```

   Temporarily change it to:

   ```csharp
   public override bool ShouldLoad(EnumAppSide forSide) => true;
   ```

3. Re-enable server command registration in `Profiling/FastMapProfilingModSystem.cs`.

   Add back:

   ```csharp
   public override void StartServerSide(ICoreServerAPI api)
   {
       RegisterCommands(api);
   }
   ```

   If profiling a dedicated server, consider requiring `Privilege.controlserver` for server-side commands.

4. Re-enable server Harmony patch installation in `Profiling/FastMapHarmonyPatches.cs`.

   The release-hardened entry point installs only the client packet patch. For full server profiling, restore an install method that patches:

   - `Vintagestory.Server.ServerEventAPI.ChunkColumnGeneration`
   - `Vintagestory.Server.ServerMain.LoadChunkColumn`
   - `Vintagestory.Server.ServerMain.LoadChunkColumnFast`
   - `Vintagestory.Server.ServerSystemSupplyChunks.loadOrGenerateChunkColumn_OnChunkThread`
   - `Vintagestory.Server.ServerSystemSupplyChunks.TryLoadChunkColumn`
   - `Vintagestory.Server.ServerSystemSupplyChunks.GenerateNewChunkColumn`
   - `Vintagestory.Server.ServerSystemSupplyChunks.PopulateChunk`
   - `Vintagestory.Server.ServerSystemSupplyChunks.mainThreadLoadChunkColumn`
   - `Vintagestory.Server.ServerSystemSendChunks.collectChunk`
   - `Vintagestory.Client.NoObf.ClientWorldMap.LoadChunkFromPacket`

   The server patch classes still exist inside `FastMapHarmonyPatches.cs` behind `FASTMAPPROFILING`; they are intentionally not installed by the release-hardened client-only path.

5. Build Debug:

   ```powershell
   dotnet build -c Debug
   ```

6. Enable startup profiling or use commands.

   Client command:

   ```text
   .fastmapprofile start
   ```

   Server command:

   ```text
   /fastmapprofile start
   ```

   Flush after the run:

   ```text
   .fastmapprofile flush
   /fastmapprofile flush
   ```

7. Server profiles are written next to client profiles:

   ```text
   VintagestoryData\FastMap\profiles\<save-id>\fastmap-profile-server-<timestamp>.csv
   ```

## Post-Profiling Cleanup

Before returning to a release package:

- Restore `modinfo.json` to `"side": "Client"`.
- Restore `FastMapProfilingModSystem.ShouldLoad(...)` to client-only.
- Remove server-side profiling command registration.
- Ensure server Harmony patches are not installed by default.
- Keep `AutoStartProfilingOnStartup` default `false`.
- Run:

  ```powershell
  dotnet build -c Release
  dotnet build -c Debug
  ```

- Verify Release output does not contain profiling entry points:

  ```powershell
  $dll = "bin\Release\FastMap.dll"
  $text = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($dll))
  "FastMapProfilingModSystem","fastmapprofile","Profiling patch installed","server_worldgen_delegate" |
      ForEach-Object { [pscustomobject]@{ Needle = $_; Present = $text.Contains($_) } }
  ```

All `Present` values should be `False` for a release build.
