# Fast Map

Fast Map replaces Vintage Story's vanilla terrain map layer with a client-side page cache. Its goal is simple: once the game has rendered map terrain you have discovered, reopening and zooming the world map should be fast instead of repeatedly rebuilding the same tiny textures.

## How It Works

Fast Map watches the same map tile data the vanilla world map uses, but groups explored terrain into larger page textures. Those pages are uploaded into GPU atlas textures for rendering and written to disk under `VintagestoryData/FastMap/<world-id>/pages-v2`.

On later sessions, Fast Map loads those page textures from disk instead of asking the vanilla map database and chunk-map generator to rebuild every visible tile again. Newly discovered or changed chunks are patched into the page cache and saved back to disk.

By default, the current cache format stores only discovered chunks inside each page and compresses the result with LZ4. An experimental filtered `pages-v3` format is available, but it is off by default because some map data compresses significantly worse after channel shuffling.

Optional LZ4HC writes can reduce cache size further at the cost of more CPU while saving pages. Compression can be disabled in config if needed, in which case Fast Map writes the older raw `pages-v1` format. `pages-v1`, `pages-v2`, and `pages-v3` caches are readable, so existing worlds can keep using their warmed cache.

## Things To Know

- Fast Map works client-side for local caching. Multiplayer page sync requires the mod on the server too.
- It respects vanilla fog-of-war semantics: cached terrain only exists for map chunks the client has map data for.
- The first visit to an area still needs map pixels to exist or be generated. The win is that those pixels are then reused instead of regenerated every time.
- Fast Map uses GPU texture atlases by default to reduce texture object churn when many cached pages are visible.
- Cached page files use a sparse LZ4-compressed format by default, but very large explored worlds can still use noticeable disk space. Removing the `VintagestoryData/FastMap/<world-id>` folder resets Fast Map's cache for that world.
- Multiplayer page sync is optional and requires Fast Map on the server and participating clients. It syncs compressed Fast Map pages, not vanilla chunks or custom world data.
- Config Lib is supported. If Config Lib is installed, Fast Map settings are available in the in-game mod settings UI.
- Most settings apply by recreating the Fast Map terrain layer after saving the config. This reloads visible page textures from disk but does not erase the persistent cache.

## Useful Settings

- `ViewportLoadScale`: Loads beyond the exact viewport to reduce visible loading edges while panning and zooming. The default is `1.5`, or 150% of the viewport.
- `PageTextureBudget`: Limits how many GPU page textures are retained before old off-screen pages are evicted.
- `EnableTextureAtlas`: Groups visible page uploads into larger `4096x4096` GPU textures. Disable this if you need to troubleshoot rendering or driver-specific atlas artifacts.
- `EnableCompressedCache`: Writes sparse LZ4 cache files when enabled. Disable only if you need the raw legacy `pages-v1` format for troubleshooting.
- `UseFilteredCache`: Experimental. Writes `pages-v3` files with an RGBA channel-shuffle filter before compression. This can help some worlds, but may increase cache size; default is off.
- `UseHighCompressionCache`: Uses LZ4HC for future compressed writes. Existing cache files remain readable; loading speed is unchanged, but background saves use more CPU.
- `CleanupKeepLatestPageVersion`: Controls `.fastmap cleanupcache`. When enabled, cleanup keeps the newest `pages-vN` folder in each world cache and removes only older page-version folders.
- `EnablePageSync`: Allows generated Fast Map pages to be uploaded to a server and shared with subscribed players.
- `PageSyncMaxUploadsPerTick`, `PageSyncMaxPagesPerRequest`, and related page-sync limits: Throttle sync bandwidth and how many received pages are merged per tick.
- `EnablePrewarm` and `PrewarmRadiusChunks`: Generate/cache nearby discovered map tiles in the background.
- `LogStats`: Disabled by default for release. Enable it when diagnosing cache behavior in `client-main.log`.

## Cache Cleanup

Run `.fastmap cleanupcache` in chat to clean versioned page-cache folders. The command reports how many page-version folders and files were deleted, plus the estimated disk space freed.

By default, cleanup preserves the newest `pages-vN` folder in each world cache. Disable `CleanupKeepLatestPageVersion` only if you intentionally want to remove all versioned page caches and let Fast Map rebuild them.

## Multiplayer Page Sync

Page sync is command-driven for now:

- `/fastmap share true` uploads and allows your generated Fast Map pages to be used by subscribers.
- `/fastmap share false` keeps uploading disabled for other players, but does not delete pages already stored by the server.
- `/fastmap sources` lists available online sources and selected offline source ids.
- `/fastmap subscribe PlayerName` subscribes to an online player's shared pages.
- `/fastmap unsubscribe PlayerName` removes that source.

Synced pages are merged into the local Fast Map page cache and rendered through the same terrain layer as locally discovered pages. The server stores compressed page payloads under `VintagestoryData/FastMapSync/<world-id>/<player-uid>`.
