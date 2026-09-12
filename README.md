# Fast Map

Fast Map replaces Vintage Story's vanilla terrain map layer with a client-side page cache. Its goal is simple: once the game has rendered map terrain you have discovered, reopening and zooming the world map should be fast instead of repeatedly rebuilding the same tiny textures.

## How It Works

Fast Map watches the same map tile data the vanilla world map uses, but groups explored terrain into larger page textures. Those pages are uploaded into GPU atlas textures for rendering and written to disk under `VintagestoryData/ModData/FastMap/<world-id>/pages-v2`.

On later sessions, Fast Map loads those page textures from disk instead of asking the vanilla map database and chunk-map generator to rebuild every visible tile again. Newly discovered or changed chunks are patched into the page cache and saved back to disk.

By default, the current cache format stores only discovered chunks inside each page and compresses the result with LZ4. An experimental filtered `pages-v3` format is available, but it is off by default because some map data compresses significantly worse after channel shuffling.

Optional LZ4HC writes can reduce cache size further at the cost of more CPU while saving pages. Compression can be disabled in config if needed, in which case Fast Map writes the older raw `pages-v1` format. `pages-v1`, `pages-v2`, and `pages-v3` caches are readable, so existing worlds can keep using their warmed cache.

## Pregen Layer

If Algernon's Terrain Sampler `1.3.0+` is installed, Fast Map also adds an optional `Pregen` world-map layer. This layer uses Terrain Sampler height and climate samples to draw low-resolution synthetic terrain pages for areas that do not have vanilla map data yet.

Pregen is opt-in. The layer is hidden when a supported sampler is unavailable or the server denies access, and it does not generate pages unless the `Pregen` layer is enabled in the world map. Background pregen while the map is closed is controlled from the Pregen layer's in-map settings and is off by default.

The Pregen layer has two palette modes:

- `Normal`: uses the world map colour style, including true-colour approximation on true-colour worlds.
- `Fog of War`: uses the brown map-background palette for a more subdued planning view.

On true-colour worlds, the Pregen settings also include a season selector. `Auto` tracks the current in-game season; a fixed month can be selected if you prefer a stable palette while planning.

Optional Terrain Sampler overlay layers can be enabled in config for `Rainfall`, `Temperature`, `Forest`, and `Shrubs`. These overlays share low-resolution sampler pages and are disabled by default.

### Multiplayer Pregen

Install the matching Fast Map package on both the client and server, and Terrain Sampler `1.3.0+` on the server. Follow Terrain Sampler's own client installation requirements too (its current package requires clients to install it). The same Fast Map ZIP contains the client map and an optional server sampling bridge; no separate companion download is needed. Restart the server after installation.

Multiplayer Pregen is disabled by default. To allow it, set `EnableTerrainSampling` to `true` in the server's `ModConfig/fastmap-server.json` and restart the server. When disabled, clients hide Pregen and the sampler overlays, sample requests are denied, and server prewarming does not run even if `EnableServerPrewarm` is `true`. Existing explicit server settings are preserved on upgrade. Single-player Pregen is unaffected by this server setting.

Fast Map discovers the server bridge after joining. Pregen transfers completed pixel tiles at the configured resolution, compressed with LZ4 and fragmented into at most 32 KiB packets. A default tile is 256 by 256 pixels (256 KiB before compression), not a grid of full terrain samples. The server samples an area once and renders Normal, Fog of War and True Colour variants; clients download only their selected variant. Packed seasonal grass metadata remains in the pixels, allowing client season controls without resampling. Protocol v7 requires matching client/server builds. The handshake includes a fingerprint of the server rendering settings and effective palette colours. Remote Pregen disk caches use that identity; changing server height/water offsets, other rendering inputs or palettes selects a fresh namespace after reconnect. Unchanged settings retain cache reuse. Legacy caches without a server identity are not reused for remote tiles.

The server creates `VintagestoryData/ModConfig/fastmap-server.json`:

```json
{
  "EnableTerrainSampling": false,
  "RequiredPrivilege": "",
  "SamplingBudgetMilliseconds": 2,
  "MaxSamplesPerTick": 256,
  "BackgroundSampling": true,
  "EnableServerPrewarm": true,
  "PrewarmRadiusPages": 12,
  "PrewarmSampleStep": 4,
  "AdaptiveSampling": true,
  "AdaptiveMaxBudgetMilliseconds": 15,
  "AdaptiveMaxSamplesPerTick": 16384,
  "SampleCacheMegabytes": 64,
  "TileCacheMegabytes": 256,
  "MaxTransferKilobytesPerTick": 256,
  "LogSamplingStats": true
}
```

`EnableServerPrewarm` prepares tiles around spawn and connected, permitted players without waiting for a map request. Spawn prewarming also runs with no connected players. The default radius of 12 pages covers a 25 by 25 page neighbourhood (625 pages), each page spanning 1,024 blocks. This extends at least 12,288 blocks in each direction from the centre position, covering a zoomed-out viewport reaching approximately 11,500 blocks either side. The default tile cache is 256 MiB; actual capacity depends on terrain and compression, and shared areas from multiple players can exceed it. Targets are nearest-first, deduplicated, bounded by the cache budget and refreshed as players move. `PrewarmSampleStep` should match the clients' Pregen resolution; other resolutions are generated on demand. Prewarming pauses under load, sends no unsolicited packets and cannot evict tiles used by clients. Obsolete speculative tiles may be replaced as players move. Live requests get priority after the current page finishes.

The multiplayer **Prefetch locally** switch only controls extra client downloads. Server prewarming is independent of that switch, Pregen visibility and whether the world map or minimap is open. Integrated single-player servers never run automatic server prewarming. In single player the original Background switch continues to control local generation.

Clients submit missing pages in bounded bursts (64 requests per network tick). The server immediately acknowledges each accepted request and pushes its result when ready; queued requests receive an update every five seconds. Up to 2,048 requests per player and 4,096 globally hold only metadata. Cached tiles bypass generation, and the worker continuously processes the remaining queue. The client retains received tiles compressed in a separate 32 MiB pool and expands them when the map worker needs their pixels. Visible requests start near the viewport centre, and completed tiles remain reserved until applied so already-rendered tiles are not reloaded every frame.

`TileCacheMegabytes` bounds compressed pixel variants plus the active worker's memory reservation. Detailed samples are temporary and discarded after rendering. Cache entries are shared across players, palettes and reconnects. The separate `SampleCacheMegabytes` pool serves optional climate overlays using the sample endpoint. Both pools are memory-only and reset on server restart; server prewarming rebuilds nearby tiles. Very distant areas still require cold generation. This is not whole-world pre-generation.

Multiplayer base rendering settings come from the server's `fastmap.json`, or Fast Map defaults if absent; the server uses its packaged palette assets when client texture assets are unavailable. Palette selection and seasonal tint remain client controls. Single-player rendering uses the same shared renderer with local settings and assets.

Leave `RequiredPrivilege` empty for everyone, use `"controlserver"` for administrators only, or disable `EnableTerrainSampling` to hide Pregen and sampler overlays. Restart after configuration changes. Permissions are checked both at request time and during delivery. Previously downloaded pixels are not erased when permissions change.

The audited Terrain Sampler 1.3.0 standard generator uses one dedicated tile worker when `BackgroundSampling` is enabled. It throttles above 75% game-process CPU or when callbacks exceed 100 ms; new speculative jobs pause at 70% CPU. Watersheds, unaudited versions, and `BackgroundSampling=false` retain budgeted main-thread sampling. Rendering and compression of completed grids always run on the worker, outside the server tick. The worker is cancelled and joined at server shutdown before sampler caches are disposed. Each endpoint bounds client subscriptions, packet sizes and bytes independently. Diagnostics distinguish tile generation, prewarming, cache hits, actual transmitted pixel bytes and game-process CPU normalised across available cores.

Servers without the bridge continue to support ordinary Fast Map caching; Pregen remains unavailable there.

## Map Mod Compatibility

Fast Map keeps Vintage Story's vanilla `Maps/<world>.db` as the durable map-piece store. This lets the vanilla map data remain useful if Fast Map is removed, and gives other map mods a stable compatibility target.

For runtime compatibility, Fast Map exposes a small map-piece facade for importing, reading, and invalidating vanilla `MapPieceDB` chunks. Other mods should use this facade when Fast Map is present instead of reflecting private `ChunkMapLayer` fields.

Fast Map also detects external edits to the vanilla map DB and refreshes visible terrain pages from the newer vanilla map pieces instead of serving stale page-cache files. K's Cartography Table is supported through a targeted compatibility shim that routes table map downloads through Fast Map's import path.

## Things To Know

- Ordinary Fast Map caching runs on the client. Server installation is optional and enables multiplayer Pregen when Terrain Sampler is available.
- It respects vanilla fog-of-war semantics: cached terrain only exists for map chunks the client has map data for.
- The first visit to an area still needs map pixels to exist or be generated. The win is that those pixels are then reused instead of regenerated every time.
- Pregen is separate from the main terrain cache. It is an optional synthetic layer for undiscovered or unloaded terrain, not a replacement for vanilla-discovered map data.
- Pregen requires Algernon's Terrain Sampler `1.3.0+`, locally for single player or on a multiplayer server with the Fast Map bridge. If sampling is unavailable or denied, the Pregen and sampler overlay layers are hidden.
- Fast Map uses GPU texture atlases by default to reduce texture object churn when many cached pages are visible.
- Cached page files use a sparse LZ4-compressed format by default, but very large explored worlds can still use noticeable disk space. Removing the `VintagestoryData/ModData/FastMap/<world-id>` folder resets Fast Map's cache for that world.
- Existing cache data from older Fast Map builds is migrated automatically from `VintagestoryData/FastMap` to `VintagestoryData/ModData/FastMap` on startup.
- Fast Map preserves the vanilla map database and watches for external map-piece writes from other mods.
- Config Lib is supported. If Config Lib is installed, Fast Map settings are available in the in-game mod settings UI.
- Config reloads are handled without tearing down the live Fast Map terrain layer. Constructor-only settings apply after the next world load; layer registration settings update safely.
- Localisation files are packaged for the map layer labels and Pregen controls.

## Useful Settings

- `ViewportLoadScale`: Loads beyond the exact viewport to reduce visible loading edges while panning and zooming. The default is `1.5`, or 150% of the viewport.
- `PageTextureBudget`: Limits how many GPU page textures are retained before old off-screen pages are evicted.
- `EnableTextureAtlas`: Groups visible page uploads into larger `4096x4096` GPU textures. Disable this if you need to troubleshoot rendering or driver-specific atlas artifacts.
- `EnableCompressedCache`: Writes sparse LZ4 cache files when enabled. Disable only if you need the raw legacy `pages-v1` format for troubleshooting.
- `UseFilteredCache`: Experimental. Writes `pages-v3` files with an RGBA channel-shuffle filter before compression. This can help some worlds, but may increase cache size; default is off.
- `UseHighCompressionCache`: Uses LZ4HC for future compressed writes. Existing cache files remain readable; loading speed is unchanged, but background saves use more CPU.
- `CleanupKeepLatestPageVersion`: Controls `.fastmap cleanupcache`. When enabled, cleanup keeps the newest `pages-vN` folder in each world cache and removes only older page-version folders.
- `EnableVanillaMapDbWriteback`: Also writes generated terrain tiles back to Vintage Story's vanilla map database so the vanilla map remains useful if Fast Map is later removed or needs to rebuild.
- `CleanupStaleVanillaMapDbSidecarsOnStartup`: Deletes stale vanilla map database `-wal` and `-shm` sidecar files once on startup before Fast Map opens the vanilla map DB.
- `DisableTerrainSamplerFallbackLayer`: Hides the `Pregen` map layer even when Terrain Sampler is installed.
- `TerrainSamplerFallbackResolutionScale`: Controls synthetic Pregen page resolution. Higher values are smaller and faster; lower values retain more detail.
- `TerrainSamplerFallbackHeightOffset`, `TerrainSamplerFallbackTerraPretyHeightOffset`, and `TerrainSamplerFallbackWaterLevelOffset`: Tune Pregen height and water classification when terrain generators report heights differently.
- `TerrainSamplerFallbackSeasonUploadBucketsPerYear`: Controls how often Auto-season Pregen textures are re-tinted per in-game year.
- `TerrainSamplerFallbackWaterBaseColor`, `TerrainSamplerFallbackWaterRainfallColor`, `TerrainSamplerFallbackWaterTemperatureColor`, and related strength/noise settings: Tune synthetic Pregen water colours.
- `EnableTerrainSamplerRainfallLayer`, `EnableTerrainSamplerTemperatureLayer`, `EnableTerrainSamplerForestDensityLayer`, and `EnableTerrainSamplerShrubDensityLayer`: Enable optional Terrain Sampler overlay map layers.
- `EnablePrewarm` and `PrewarmRadiusChunks`: Generate/cache nearby discovered map tiles in the background.
- `UseMinimalChunkDirtyRepairFanout`: Uses the smaller renderer-derived dirty repair set instead of the legacy 3x3 neighborhood. Disable if you need to compare against the older conservative behavior.
- `ExperimentalChunkDirtyRepairDelayMilliseconds`: Disabled by default. Delays chunk-dirty map repairs for profiling experiments; not recommended for normal gameplay.
- `LogStats`: Disabled by default for release. Enable it when diagnosing cache behavior in `client-main.log`.
- `EnableHitchDiagnostics`: Disabled by default. Logs slow Fast Map tick/render/off-thread passes when diagnosing intermittent frame drops.

## Cache Cleanup

Run `.fastmap cleanupcache` in chat to clean versioned page-cache folders. The command reports how many page-version folders and files were deleted, plus the estimated disk space freed.

By default, cleanup preserves the newest `pages-vN` folder in each world cache. Disable `CleanupKeepLatestPageVersion` only if you intentionally want to remove all versioned page caches and let Fast Map rebuild them.

Run `.fastmap sealevel` in chat to print the world sea level, Pregen water cutoff, Terrain Sampler height at your position, and any detected Terra Prety height offset. This is mostly useful when tuning Pregen coastlines.

## Development Profiling

Release builds include the optional server sampling bridge and exclude profiling command registration and Harmony profiling patches. See [docs/profiling.md](docs/profiling.md) for the Debug/dev workflow to re-enable client profiling or full client/server profiling.

Run the streaming and scheduler regression checks with `dotnet run --project tools/FastMap.NetworkTests -c Release`. Set `GamePath` or `VINTAGE_STORY_121` to a game install containing `Lib/protobuf-net.dll`.
