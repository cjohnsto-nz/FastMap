# Multiplayer Pregen validation

Issue: https://github.com/cjohnsto-nz/FastMap/issues/5

Fast Map's former adapter reflected a Terrain Sampler singleton in the client process. Terrain Sampler initializes that singleton on the server. This works in single player, but does not expose the dedicated server's sampler to a remote client.

The optional `fastmap-terrain-v6` channel negotiates availability and permissions. Pregen requests compressed pixel tiles; the server independently prewarms nearby areas and caches all three palette variants. The client submits missing pages through an acknowledged queue; cached pixels stream independently of cold generation, which pushes its results when ready. Detailed grids remain available for optional climate overlays. Timeout, cancellation, and disconnect handling release pending work. Standard Terrain Sampler 1.3.0 uses one background worker; other sampler paths retain a main-thread budget. Earlier measurements below describe the successive development builds explicitly.

The client map system remains client-only. The package is universal with both required-on-side flags false, so ordinary map caching still works without a server installation. Multiplayer clients use the server channel rather than reflecting any locally loaded Terrain Sampler assembly. Single player retains direct sampling.

## Automated checks

```powershell
dotnet run --project tools/FastMap.NetworkTests -c Release
dotnet build FastMap.csproj -c Release -p:GameVersion=1.21 -p:GamePath="$env:VINTAGE_STORY_121" -p:OutputPath=bin/issue5-121/
dotnet build FastMap.csproj -c Release -p:GameVersion=1.22 -p:GamePath="$env:VINTAGE_STORY_122" -p:OutputPath=bin/issue5-122/
```

Run game builds sequentially because they share intermediate build output. The network harness exercises the production grid client, scheduler and wire codec through protobuf serialization. It covers a complete 257 × 257 grid, all sample fields, cache reuse, simultaneous worker deduplication, per-player and global queue limits, time and sample budgets, permission denial/revocation, cancellation/disconnect cleanup, timeout, malformed offsets, and world-edge clamping.

## Verification performed on 2026-09-12

- Release builds against Vintage Story 1.21.7 and 1.22.1: passed without warnings.
- Network regression harness: passed, including a complete Pregen page transferred in 259 batches. This is a serialized transport simulation, not an actual game-client connection.
- Dedicated 1.22.1 server with the development ZIP and Terrain Sampler 1.3.0: reached RunGame, loaded only the Fast Map server bridge, and detected the sampler. An isolated test probe called the packaged adapter for 1,089 columns: all contained climate data, heights ranged from 90 to 225, and sampling took 389 ms. Server shut down cleanly.
- Dedicated 1.21.7 server with the development ZIP and no Terrain Sampler: reached RunGame, correctly reported the sampler unavailable, and shut down cleanly.
- Development ZIPs: validated metadata, DLL, assets, CRCs, and forward-slash archive paths. They retain the current 121.5.2 / 122.5.2 package versions and are identified as development artifacts by filename; no release version was selected or published.

## x3200 graphical multiplayer retest on 2026-09-12

- Reproduced the original issue on Vintage Story 1.22.7 with Fast Map 122.5.2: the map listed Terrain, Waypoints, Prospecting and Owned creatures, with no Pregen layer.
- Built 121.6.0 against Vintage Story 1.21.7 and 122.6.0 against 1.22.7; both Release builds passed without warnings. Assembly metadata is 0.6.0.0.
- Installed 122.6.0 using Server Manager MCP `mod_upload`, including its recovery copy and server restart. Installed the same ZIP locally and relaunched the client. Server inventory and local SHA-256 matched: `16d8671a72c6fe8eabe4415b6b75abeb54f9764f89a12d298c52b7e94f7a187a`.
- Rejoined x3200. Server log confirmed the terrain bridge and sampler were available; client log confirmed server sampling availability after the handshake. Pregen appeared in the map.
- Enabled Pregen and zoomed out. Sampled pages appeared beyond the explored boundary and were saved to the client fallback cache. Three new pages were observed at 14:42:53, 14:42:59 and 14:43:19 NZST. Initial page generation took roughly two minutes after the sampling log at 14:41:05, so first-load performance under the default 2 ms server budget needs further work.
- No public release, push or PR was made. Graphical 1.21 testing, permission scenarios, climate overlays and reconnect/cache persistence beyond this initial join remain unverified.

## Additional graphical multiplayer checks

1. Join a dedicated server with the matching development Fast Map package and Terrain Sampler 1.3.0+. Follow Terrain Sampler's own client requirements. Verify Pregen appears after joining, including when the minimap or world map was already open during the handshake.
2. Enable Pregen and pan into undiscovered terrain. Wait for samples to arrive; confirm terrain, shading borders, palette/season controls, and each enabled climate overlay. Reopen the map and reconnect to confirm cached pages load.
3. Pan rapidly, close the map while requests are pending, and leave/rejoin another world. Confirm responsive rendering and no stale samples or errors. Test the opt-in client prefetch switch separately from default-on server prewarming.
4. Set server `RequiredPrivilege` to `controlserver`, restart, and test both an administrator and a normal player. Set `EnableTerrainSampling` to false and verify sampler layers remain hidden. Previously downloaded data is not erased by these settings.
5. Join a server without Fast Map, and a server with Fast Map but no sampler. Confirm ordinary discovered map caching works and Pregen remains hidden.
6. Recheck single-player Pregen and normal map caching. These graphical flows have not been exercised by the headless server probes.

Server settings and installation instructions are in the [README](../README.md#multiplayer-pregen).

## Full-resolution worker performance, 2026-09-12

The coarse-first experiment was removed following user feedback. Every Pregen request now uses the configured final resolution directly. Protocol v3 byte-shuffles sample fields before LZ4 compression and uses raw blocks only when smaller.

- Audited the installed Terrain Sampler 1.3.0 assembly: its sampling contexts and temporary arrays are thread-local, region caches use striped locks, and climate-derived map generation locks its generator. The worker is restricted to that audited version with Watersheds disabled. Other versions and Watersheds fall back to the budgeted path.
- Network regression harness passed under .NET 8 with the 1.21.7 protobuf assembly and .NET 10 with the 1.22.7 assembly (over 450 assertions each; callback counts vary with worker timing). Includes worker isolation, shared full-grid sampling, cancellation/rejoin, reservation retention, failure propagation and cancellation/join at shutdown, as well as codec, permissions, budgets and cache tests.
- Release builds against 1.21.7 and 1.22.7 passed with zero warnings/errors. Graphical testing below used 1.22.7; graphical 1.21 and Watersheds tests remain outstanding.
- Installed `fastmap_122.6.0-perf3.zip` through Server Manager MCP, operation `9e6af4bb-c471-4e0a-ba5e-f0db83d21073`, then verified the server loaded the bridge with `background: True`. The matching local ZIP SHA-256 was `82ced35a09aa9590f0304958198729117d030d49b683c99b9800933ff8e1becd`.
- VM: x3200, two-core Standard_D2alds_v6, Vintage Story 1.22.7, Terrain Sampler 1.3.0. CPU percentages below are the game process normalised across both cores, not host-wide CPU measurements.
- Cold test: restarted server, moved only the client's synthetic Pregen cache into a backup, rejoined through the game UI, and viewed/zoomed Pregen. The first four 257×257 step-4 grids completed in 7,208 / 7,275 / 7,342 / 7,409 ms. The preceding preview experiment took approximately 30 seconds for its first four full grids, after also sampling previews.
- Across 95 uncached full-grid completions captured from 03:14–03:16 UTC, median server request-to-last-send latency was 4,167 ms (range 1,953–7,409 ms). This includes sharing the worker among up to four active requests. It excludes client queueing before a request and final rendering, so it is not a whole-viewport load time.
- Mean encoded payload per full grid was 870,493 bytes versus 1,651,225 raw bytes, a 47.3% reduction. These are sample payload bytes, excluding protobuf and transport overhead. No coarse grids were requested in this test.
- Sustained process CPU observations were 41–51%, with 62.1% during the initial load. Callback observations were generally 66–68 ms, initially 84 ms. Main-thread service work totalled 10–27 ms per ten-second interval. The pool stayed below its 64 MiB reservation limit (largest cold-test observation 66,315,367 bytes).
- Wider-area sampling filled Pregen pages without the previous preview atlas-size crash. Two intervening client startup failures reported the base game's `lightPosition` shader lookup before any mods loaded; a subsequent launch succeeded. No FastMap error was observed in the successful worker test.
- Server-cache replay: the wide-area test had evicted the initial pages from the bounded LRU pool, so those six pages were first warmed again in a compact viewport. Closed the client, backed up its synthetic cache again, and rejoined without restarting the server. At 03:21:27 UTC all six requests logged `cache=True`: first four completed in 938 ms, next two in 436 ms. The following stats interval recorded `samples=0`, `workerMs=0`, `cacheHits=6`, and 5,302,429 encoded payload bytes. This verifies reuse across a fresh client process, independent of its memory/disk sample caches.

Both package lines remain n.6.0; this is an installed development build, with no public release, push or PR.

## Server-rendered pixel tiles and independent prewarming, 2026-09-12

Pregen now uses completed, losslessly compressed pixel tiles. Detailed sample grids remain only for optional climate overlays. The server prepares Normal, Fog of War and True Colour variants from one full-resolution sample grid and then discards that grid. There is no coarse preview pass.

- Release builds against 1.21.7 and 1.22.7 passed with zero warnings/errors. The production network regression harness passed on .NET 8 and .NET 10, with 635 assertions in the final runs. Additional tests cover no-player prewarming without packets, all pixel bits and fragments, palette reuse, memory bounds including active reservations, eviction, shared cancellation/rejoin, shutdown, and cached delivery while four generation requests are deliberately blocked.
- Installed `fastmap_122.6.0-perf5.zip` through Server Manager MCP, operation `e26c7bd8-d2c7-4629-8e91-86c02189905b`. Matching server/local SHA-256: `84ab4b851d6d97d050cfd4979741a0167d57a9084bd5ae3bcab89f1fd7288fb9`. The 121.6.0 package SHA-256 is `db34e6e4160589f56aa3e49015d7955e0820d639a5a60e99294ed7ff84f1b7b9`. These remain development artifacts.
- With nobody connected, x3200 completed the default 25 spawn pages by 03:57:01 UTC. The 03:57:08 stats recorded `cached=25`, `poolBytes=6540479`, `prewarmed=25`, `wireBytes=0`, and no pending work. That is **6.24 MiB for all three variants**, including cache accounting overhead. Game-process CPU returned to 0.5% after completion. This independently reproduces the preceding perf4 no-client test.
- Joining added five prewarm pages around the player, bringing the cache to 30 pages / 7,777,255 bytes before Pregen requested data. The six initial Normal pages were all cache hits: 107,585 bytes total, each completing its server send in 66 ms at 03:58:19 UTC. The preceding perf4 run sent the same six in 30–33 ms and verified all six `.fmf` files byte-for-byte identical to the previous client-rendered files.
- The six Normal payloads were 1,041 / 1,041 / 2,836 / 9,803 / 40,019 / 52,845 bytes. Their total is about **49 times smaller** than the old 5,302,429-byte detailed-grid cache replay. These counts exclude protobuf and transport headers.
- The first pixel build exposed a scheduling issue: four cold requests could occupy every client request slot and prevent requests for ready pages elsewhere. Protocol v5 separates 16 cache-only fetch slots from four generation slots. Cache misses do not enqueue generation or consume failure retries. Viewport requests prefer the centre, and remote fetching no longer waits for explored-map cache loading.
- Graphical palette switching exercised this correction with an empty alternate-palette disk cache. At 04:00:06–04:00:07 UTC, **25 cached Fog of War tiles** completed delivery, totalling **3,235,263 bytes**, while an uncached tile was also generating. Individual cached request-to-last-send durations were **65–538 ms**. The uncached tile completed in 1,285 ms. Later Normal cache hits completed in 32–67 ms. Fog of War's texture produces larger payloads, approximately 123–142 KiB per measured tile, compared with roughly 1–52 KiB for the initial Normal view.
- Normal and Fog of War were exercised in the game UI. True Colour transport preserves packed seasonal metadata in automated tests, but graphical True Colour/season controls, graphical 1.21, Watersheds and optional climate overlays remain unverified in this iteration.

Timing above is measured at the server from request receipt to final fragment send. The approximately one-second palette burst comes from second-resolution server timestamps; it is not a measured end-to-end GPU render time. No subsecond guarantee is made for an entirely uncached wide viewport. The default server prewarm area is a bounded 5×5-page neighbourhood around spawn and each permitted player; areas outside it still require sampling. Both server pools are memory-only and rebuild after restart. The client Pregen caches were moved into backups for testing; explored-map data was preserved.

## Acknowledged requests and wider viewport defaults, 2026-09-12

- Perf6 corrected a client reservation bug that repeatedly loaded and uploaded resident fallback pages. The perf5 baseline recorded 6,873 sampler pages and 6,856 uploads with only 58 fallback pages resident. With perf6, 238 prewarmed pages reached the rendering pipeline in the initial zoom-out burst; resident pages then increased continuously without repeated sampler cache hits. Server logs from 04:23:50–04:26:25 UTC recorded 169 builds, median 932 ms, with sustained process CPU approximately 49–57% across the two-core VM.
- Protocol v6 accepts the client's missing viewport pages as metadata, acknowledges immediately, updates waiting requests every five seconds, and pushes finished tiles. Cached results bypass cold generation. Perf7 keeps received pages compressed until consumption to avoid refetching tiles evicted during a large burst. The final regression runs passed 2,849 assertions on both .NET 8 and .NET 10, including a 300-page compressed burst without refetching. The perf7 graphical reconnect check was interrupted by the user and remains incomplete.
- The subsequent viewport-default change (perf8) sets `PrewarmRadiusPages=12` and `TileCacheMegabytes=256`. A single centre targets 625 pages, spanning 25,600 blocks and extending at least 12,288 blocks in each direction regardless of its position within the centre page. This interprets the requested 11,500-block X extent as centre-to-edge. The larger memory allowance replaces the previous 64 MiB pool, which filled at 238 pages in the no-player test; actual capacity still depends on compression and shared player areas.
- Perf8 Release builds against 1.21.7 and 1.22.7 passed with zero warnings/errors, and both archives passed metadata and CRC checks. SHA-256: 121.6.0 `4059f02aa5178235afeef6f2618a5c9c0ecec670343a9bacf0fc5ffd8ed60c46`; 122.6.0 `876629eff4e46b4b14b4f22fc59be5bbbf001c13dcb400c29532a009dc2449b4`.
- Updated the existing x3200 config through Server Manager MCP, preserving other settings and backing up its prior contents, and installed the perf8 server archive (operation `4e754932-6746-4317-8dfb-61182e03457a`). The local perf7 client uses the same protocol and needs no client change for these server settings. No new graphical test or completed 625-page warm-up measurement is claimed for this default-only change.

## Requested perf8 retest, 2026-09-12

- Re-ran the production network harness against 1.21.7/.NET 8 and 1.22.7/.NET 10: both passed 2,849 assertions. `git diff --check` passed.
- Confirmed x3200 running and prewarming without clients: at 04:35:41 UTC, 121 of 625 targets were cached, 34,901,312 pool bytes including the active reservation, and zero transmitted bytes. This snapshot verifies continued progress, not completion of the expanded area.
- Installed the matching perf8 archive locally (SHA-256 matches the server), relaunched, and reconnected successfully. The six initial Normal pages loaded from disk with zero tile requests. The existing Normal cache contained 478 files before joining; the Fog of War cache was empty.
- Live zooming, panning and switching between Normal and Fog of War exercised cache delivery and cancellation while prewarming was still incomplete. Client completed-tile count increased from 58 to 402 between 16:39:01 and 16:39:16 NZST (344 tiles in 15 seconds; includes both cached and generated deliveries). During this interval approximately 46 MiB of additional payload arrived, predominantly the larger Fog of War tiles. The compressed receive pool remained below 32 MiB. Subsequent five-second intervals completed five or six tiles, with server CPU approximately 50–53% across two cores and no former minute-long idle gaps.
- Visually verified the Normal map populated across the zoomed-out view after returning from Fog of War; cold edges continued to fill. Panning and palette changes occurred during the test, so these observations are not a controlled end-to-end whole-viewport timing. Full 625-page warm-up completion and subsecond rendering remain unverified.
