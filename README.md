# Fast Map

Fast Map replaces Vintage Story's vanilla terrain map layer with a client-side page cache. Its goal is simple: once the game has rendered map terrain you have discovered, reopening and zooming the world map should be fast instead of repeatedly rebuilding the same tiny textures.

## How It Works

Fast Map watches the same map tile data the vanilla world map uses, but groups explored terrain into larger page textures. Those pages are uploaded to the GPU for rendering and written to disk under `VintagestoryData/FastMap/<world-id>/pages-v1`.

On later sessions, Fast Map loads those page textures from disk instead of asking the vanilla map database and chunk-map generator to rebuild every visible tile again. Newly discovered or changed chunks are patched into the page cache and saved back to disk.

## Things To Know

- Fast Map is client-side only. Servers do not need to install it.
- It respects vanilla fog-of-war semantics: cached terrain only exists for map chunks the client has map data for.
- The first visit to an area still needs map pixels to exist or be generated. The win is that those pixels are then reused instead of regenerated every time.
- Cached page files can consume disk space in large explored worlds. Removing the `VintagestoryData/FastMap/<world-id>` folder resets Fast Map's cache for that world.
- Config Lib is supported. If Config Lib is installed, Fast Map settings are available in the in-game mod settings UI.
- Most settings apply by recreating the Fast Map terrain layer after saving the config. This reloads visible page textures from disk but does not erase the persistent cache.

## Useful Settings

- `ViewportLoadScale`: Loads beyond the exact viewport to reduce visible loading edges while panning and zooming. The default is `1.5`, or 150% of the viewport.
- `PageTextureBudget`: Limits how many GPU page textures are retained before old off-screen pages are evicted.
- `EnablePrewarm` and `PrewarmRadiusChunks`: Generate/cache nearby discovered map tiles in the background.
- `LogStats`: Disabled by default for release. Enable it when diagnosing cache behavior in `client-main.log`.
