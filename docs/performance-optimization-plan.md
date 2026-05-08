# FastMap / Vintage Story Performance Optimization Plan

This file is the working notebook for the long-horizon optimization effort. Keep it updated whenever we add instrumentation, run a profile, or complete an optimization experiment.

## Goal

Explore performance changes that could make map and world generation dramatically faster, with a BHAG target of 100x for workloads that do not require exact full vanilla chunk simulation.

The near-term focus is not seasonal maps specifically. The broader target is to separate "what the player needs to see or know now" from "a fully generated, fully simulated vanilla chunk column."

## Current Measured Bottlenecks

Latest useful run:

- Profile directory: `C:\Users\chris\AppData\Roaming\VintagestoryData\FastMap\profiles\4afec452-9f37-4a6d-97c8-94e9477ea3d6`
- Client profile: `fastmap-profile-client-20260508-060310.csv`
- Server profile: `fastmap-profile-server-20260508-060307.csv`

Ranked leaf-ish stages from that run:

- `server_worldgen_delegate`: `97.50%` of measured non-inclusive server work.
- `fastmap_generate_pixel_loop`: `85.03%` of measured non-inclusive client work.
- `fastmap_page_load`: `5.71%` of measured non-inclusive client work.
- `fastmap_page_upload`: `3.01%` of measured non-inclusive client work.
- `fastmap_generate_color_multiply`: `2.90%` of measured non-inclusive client work.
- `client_load_chunk_packet`: `1.81%` of measured non-inclusive client work.
- `server_mainthread_load_column`: `1.56%` of measured non-inclusive server work.
- `server_chunk_to_packet`: `0.89%` of measured non-inclusive server work.

Important interpretation:

- `server_supply_column_step` is inclusive wrapper timing and should not be added to `server_populate_worldgen_pass`.
- `fastmap_chunk_repair_generated` is inclusive wrapper timing and should not be added to `fastmap_generate_chunk_image`.

Top server worldgen delegates in the latest run:

- `Vintagestory.ServerMods.GenTerra.OnChunkColumnGen`: `24.01%` of measured delegate work.
- `Vintagestory.ServerMods.GenLightSurvival.OnChunkColumnGeneration`: `19.11%`.
- `Vintagestory.ServerMods.GenDeposits.GenChunkColumn`: `13.33%`.
- `Vintagestory.ServerMods.GenRockStrataNew.GenChunkColumn`: `9.78%`.
- `Vintagestory.ServerMods.GenPartial.GenChunkColumn`: `9.07%`.
- `Vintagestory.ServerMods.GenVegetationAndPatches.OnChunkColumnGen`: `4.98%`.
- `Vintagestory.ServerMods.GenBlockLayers.OnChunkColumnGeneration`: `4.71%`.
- `Vintagestory.ServerMods.GenStructures.OnChunkColumnGen`: `4.64%`.

## Code References

FastMap profiling:

- `Profiling/FastMapProfileRecorder.cs`
- `Profiling/FastMapProfilingModSystem.cs`
- `Profiling/FastMapHarmonyPatches.cs`
- `tools/Summarize-FastMapProfile.ps1`

FastMap map generation:

- `Map/FastPageMapLayer.cs`
- `Map/FastMapPageComponent.cs`
- `Map/FastMapPageDiskCache.cs`

Vanilla/decompiled server worldgen:

- `_ilspy_vslib/Vintagestory.Server/ServerSystemSupplyChunks.cs`
- `_ilspy_vslib/Vintagestory.Server/ServerEventAPI.cs`
- `_ilspy_vslib/Vintagestory.Server/WorldGenHandler.cs`
- `_ilspy_vsessentials/Vintagestory.ServerMods/GenTerra.cs`
- `_ilspy_vsessentials/Vintagestory.ServerMods/GenCaves.cs`
- `_ilspy_vsessentials/Vintagestory.ServerMods/GenVegetationAndPatches.cs`
- `_ilspy_vsessentials/Vintagestory.ServerMods/GenLightSurvival.cs`

Vanilla/decompiled map rendering comparison:

- `_ilspy_vsessentials/Vintagestory.GameContent/ChunkMapLayer.cs`

Chunk data access:

- `_ilspy_vslib/Vintagestory.Common/WorldChunk.cs`
- `_ilspy_vslib/Vintagestory.Common/ChunkData.cs`
- `_ilspy_vslib/Vintagestory.Common/ChunkDataLayer.cs`
- `_ilspy_vslib/Vintagestory.Client.NoObf/ClientChunk.cs`

## Work Plan

### Phase 1: Better Measurements

Status: In progress. Last updated: 2026-05-08.

Newest proportional runs:

Fresh-world fast-flight profile:

- Profile directory: `C:\Users\chris\AppData\Roaming\VintagestoryData\FastMap\profiles\4b0c0635-09b4-4ef9-8a12-b6c486d55ad4`
- Client profile: `fastmap-profile-client-20260508-061645.csv`
- Server profile: `fastmap-profile-server-20260508-061642.csv`

Existing-world cold-cache profile:

- Profile directory: `C:\Users\chris\AppData\Roaming\VintagestoryData\FastMap\profiles\406737b8-ec30-402f-8e13-323a0d7647fa`
- Client profile: `fastmap-profile-client-20260508-061940.csv`
- Server profile: `fastmap-profile-server-20260508-061937.csv`

Fresh-world fast-flight client shape:

- `fastmap_generate_surface_render`: `76.63%` of measured non-inclusive client work, `0.6703 ms` average.
- `fastmap_generate_surface_extract`: `6.69%`, `0.0585 ms` average.
- `fastmap_page_load`: `5.87%`, `12.9110 ms` average.
- `fastmap_page_upload`: `3.32%`, `0.3470 ms` average.
- `fastmap_generate_color_multiply`: `2.95%`, `0.0258 ms` average.
- `client_load_chunk_packet`: `2.59%`, `0.0620 ms` average.

Existing-world cold-cache client shape:

- `fastmap_page_db_build`: `57.90%` of measured non-inclusive client work, `78.1248 ms` average.
- `fastmap_page_load`: `41.18%`, `17.2736 ms` average.
- `fastmap_page_upload`: `0.48%`, `0.4893 ms` average.
- `fastmap_generate_surface_render`: `0.15%`, `0.1082 ms` average.
- `fastmap_page_disk_load`: `0.15%`, `6.1250 ms` average.
- `fastmap_generate_surface_extract`: `0.09%`, `0.0609 ms` average.

Server proportional shape from the fresh-world run:

- `server_worldgen_delegate`: `97.72%` of measured non-inclusive server work.
- `server_mainthread_load_column`: `1.45%`.
- `server_chunk_to_packet`: `0.78%`.
- `server_generate_empty_column`: `0.03%`.
- `server_try_load_column`: `0.02%`.

Interpretation:

- Fresh-world streaming is now mostly surface rendering, not surface extraction. This points toward shade/color math, page-level rendering, or GPU rasterization before `FastMapSurfaceTile` for that workload.
- Existing-world cold-cache startup is a different bottleneck: rebuilding FastMap pages from vanilla `MapDB` dominates, while `GenerateChunkImage` is almost irrelevant.
- Server-side work remains overwhelmingly worldgen delegate execution in fresh-world tests.
- Fresh-world profile generated `34200` tile images for `12512` client chunk packets, about `2.73x`.
- Existing-world cold-cache profile generated only `114` tile images; most work was page reconstruction from the vanilla map DB.

Fresh-world repair reason findings:

- `fastmap_chunk_repair_queued|chunkdirty;mode=force`: `34513`.
- `fastmap_chunk_repair_generated|chunkdirty`: `32815`, `26077.705 ms`.
- `fastmap_chunk_repair_skipped_missing_mapchunk|prewarm`: `24792`.
- `fastmap_chunk_repair_missing_mapchunk|chunkdirty`: `1146`.
- `fastmap_chunk_repair_generated|prewarm`: `749`, `550.755 ms`.

Existing-world cold-cache repair reason findings:

- `fastmap_chunk_repair_skipped_missing_mapchunk|prewarm`: `1960`.
- `fastmap_chunk_repair_generated|chunkdirty`: `114`, `27.126 ms`.

Interpretation:

- `chunkdirty` remains the dominant source of successful map image generation in fresh-world streaming.
- `prewarm` missing-mapchunk work is being skipped before queueing.
- Cold-cache page reconstruction has emerged as a separate bottleneck for existing worlds with populated vanilla map DBs but no FastMap pages.

- [x] Add basic client/server CSV profiling.
- [x] Add server chunk supply, worldgen pass, chunk serialization, and client chunk packet timings.
- [x] Add FastMap chunk image, repair, patch, upload, and save timings.
- [x] Add `kind` and `category` fields so summaries can ignore inclusive wrapper stages.
- [x] Add per-worldgen-generator timing by wrapping `ServerEventAPI.ChunkColumnGeneration(...)`.
- [x] Add `GenerateChunkImage` substage timings for chunk prefetch, mapchunk fetch, pixel loop, blur, and final color multiply.
- [x] Add repair reason tracking for current sources: `prewarm`, `chunkdirty`, and combined duplicate reasons.

Implemented on 2026-05-08:

- `Profiling/FastMapProfileRecorder.cs` now writes `kind` and `category` columns.
- `Profiling/FastMapHarmonyPatches.cs` patches `Vintagestory.Server.ServerEventAPI.ChunkColumnGeneration(...)`.
- `Profiling/FastMapWorldgenDelegateProfiler.cs` wraps registered `ChunkColumnGenerationDelegate` handlers and records `server_worldgen_delegate`.
- `Map/FastPageMapLayer.cs` records `fastmap_generate_prefetch_chunks`, `fastmap_generate_fetch_mapchunks`, `fastmap_generate_pixel_loop`, `fastmap_generate_blur`, and `fastmap_generate_color_multiply`.
- `tools/Summarize-FastMapProfile.ps1` now ignores inclusive wrapper rows by default and can include them with `-IncludeInclusive`.
- `tools/Summarize-FastMapProfile.ps1` now includes `sharePct` so non-repeatable runs can be compared by proportional shape.
- `tools/Summarize-FastMapWorldgenDelegates.ps1` reports worldgen delegate count, total, share, average, p95, and p99.
- `tools/Summarize-FastMapRepairReasons.ps1` reports repair stages grouped by detail/reason.
- `Map/FastPageMapLayer.cs` now carries repair queue reasons through to missing/success rows.

Measure after Phase 1:

- Fresh world fast flight.
- Existing generated world fast flight.
- Standing still with map open.
- `/wgen pregen [radius]`.
- `/wgen regen [radius]`.

### Phase 2: FastMap Image Generation Wins

Status: In progress. Last updated: 2026-05-08.

- [x] Prototype direct chunk data reads: call `Unpack_ReadOnly()` once per vertical chunk, then read from `chunk.Data` rather than `UnpackAndReadBlock()` per pixel.
- [x] Make prewarm source-aware so it skips repair queueing when the source mapchunk is unavailable.
- [x] Add a configurable dirty-repair debounce experiment so streaming bursts can coalesce before FastMap regenerates affected tiles.
- [x] Add a minimal chunk-dirty repair fanout experiment: `6` renderer-derived affected tiles instead of legacy `3x3`.
- [x] Optimize the normal non-color-accurate pixel loop with precomputed block-id flags and colors.
- [x] Split `GenerateChunkImage` pixel work into surface extraction and surface rendering substages.
- [x] Add a lazy vanilla map DB position index so cold-cache page reconstruction can skip absent map pieces.
- [ ] Add `FastMapSurfaceTile` cache containing `height[1024]`, `topBlockId[1024]`, and flags.
- [ ] Split surface extraction from tile rendering so repeated map rendering can reuse surface data.
- [ ] Prototype page-level generation instead of chunk-level generation.
- [ ] Evaluate GPU page rasterization after surface buffers exist.

Implemented on 2026-05-08:

- `Map/FastPageMapLayer.cs` now pre-unpacks vertical chunks once in `GenerateChunkImage(...)`.
- `Map/FastPageMapLayer.cs` now reads top blocks with `chunk.Data.GetBlockId(...)` through `ReadBlockId(...)` instead of `UnpackAndReadBlock(...)` per pixel.
- `fastmap_chunk_repair_generated` and `fastmap_chunk_repair_missing_source` are now marked `kind=inclusive`.
- `Map/FastPageMapLayer.cs` now skips prewarm repair queue entries whose mapchunk is unavailable and records `fastmap_chunk_repair_skipped_missing_mapchunk`.
- `Config/FastMapConfig.cs` now exposes `ExperimentalChunkDirtyRepairDelayMilliseconds`, default `0`.
- `Map/FastPageMapLayer.cs` can delay `chunkdirty` repair entries by `ExperimentalChunkDirtyRepairDelayMilliseconds` for profiling only. The default is disabled because the `500 ms` experiment caused visible map starvation during fast flight.
- `Config/FastMapConfig.cs` now exposes `UseMinimalChunkDirtyRepairFanout`, default `true`.
- `Map/FastPageMapLayer.cs` now queues the dirty chunk itself plus west/east/north/south/southeast neighbors by default, instead of the legacy full `3x3` neighborhood. This matches the current renderer dependencies from `CalculateShade(...)` and `GetLakeColor(...)`.
- `Map/FastPageMapLayer.cs` now builds `blockIsLakeByBlockId` and `blockIsSnowByBlockId` alongside `blockColorByBlockId`.
- `Map/FastPageMapLayer.cs` now avoids per-pixel `Block` object lookups and `BlockPos.Set(...)` in the normal non-color-accurate path. The color-accurate path still uses vanilla `Block.GetColor(...)` and `Block.GetRandomColor(...)`.
- `Map/FastPageMapLayer.cs` now catches color-accurate block color failures and falls back to the cached map color, recording `fastmap_coloraccurate_fallback`. This addresses crashes like `BlockGroundStorage.GetColorWithoutTint(...)` throwing inside vanilla color lookup.
- `Map/FastPageMapLayer.cs` now records `fastmap_generate_surface_extract` and `fastmap_generate_surface_render`; `fastmap_generate_pixel_loop` is now an inclusive wrapper around those two stages.
- `Map/FastPageMapLayer.cs` now lazily builds `mapDbKnownPositions` from the vanilla `mappiece` table and records `fastmap_page_db_index_build`. `TryBuildPageFromDb(...)` uses this index to skip chunks that are definitely absent instead of blindly probing all `256` chunks in each page.
- `Map/FastPageMapLayer.cs` now falls back from `sqliteConn` reflection to `getMapPieceCmd.Connection`, and records `fastmap_page_db_index_unavailable` if the vanilla DB connection still cannot be found.
- `Config/FastMapConfig.cs` now exposes `AutoStartProfilingOnStartup`, default `true` for this profiling branch, so cold-cache startup events are captured before manual `.fastmapprofile start`.
- `Profiling/FastMapProfilingModSystem.cs` now starts profiling when either `EnableProfiling` or `AutoStartProfilingOnStartup` is enabled.
- `Map/FastPageMapLayer.cs` now records cold-cache page DB substages: `fastmap_page_db_lock_wait`, `fastmap_page_db_index_lookup`, `fastmap_page_db_get_pieces`, and `fastmap_page_db_copy_tiles`, with candidate/loaded tile counts in `detail`.
- `Map/FastPageMapLayer.cs` now lazily allocates DB-reconstructed page buffers only after a vanilla map piece is actually loaded. Indexed pages with `candidates=0` no longer allocate a full page pixel buffer before returning a miss.
- `Map/FastPageMapLayer.cs` now accumulates DB substage timings as fractional milliseconds instead of truncating each per-piece timing to whole milliseconds.

Validation notes:

- Profile `4ee43fcf-03ad-42a2-a6bd-5ca56fc1f384` showed `fastmap_generate_pixel_loop` at `0.7332 ms` average vs prior `0.8158 ms` average. Treat as directional because the run was not repeatable.
- Profile `77d183ce-4c68-4d78-a3c2-ac79065d3404` showed `fastmap_generate_pixel_loop` at `0.8126 ms` average, so the direct-read improvement is not conclusive from non-repeatable runs.
- Profile `ae03a890-78ca-4ea0-a710-90d3b67d2950` showed prewarm queue entries dropping from `27698` to `753`, with `23868` `fastmap_chunk_repair_skipped_missing_mapchunk|prewarm` events. The prewarm skip is working.
- Profile `32e0f691-73b5-47dd-a8ec-ca18a369d6af` showed the dirty debounce experiment was not gameplay-acceptable: `14312` client chunk packets produced only `1308` generated map tiles, and map generation visibly lagged/discontinued. Treat this as a profiling-only negative result.
- Profile `f5f2e6bb-8762-4a9b-9a14-93fa4a33d9dc` verified normal generation returned with the dirty debounce disabled: `8888` client chunk packets produced `31862` generated map tiles.
- Profile `95bbc456-367e-4d20-b602-5c0d8b970714` tested `UseMinimalChunkDirtyRepairFanout=true`: `10192` client chunk packets produced `33215` generated map tiles. This is a modest ratio improvement over `f5f2e6bb`, but not the hoped-for one-third win.
- Profile `446089a3-b518-4443-9e5e-c15b9a71da3b` strengthened the minimal fanout signal: `12280` client chunk packets produced `32457` generated map tiles, about `2.64x`.
- Profile `4afec452-9f37-4a6d-97c8-94e9477ea3d6` validated the precomputed block-id flag/color optimization directionally: `fastmap_generate_pixel_loop` average moved to `0.6769 ms`, down from `0.7362 ms` in `446089a3`.
- The next profile should watch for `fastmap_coloraccurate_fallback` events if color-accurate map mode is enabled.
- Profile `4b0c0635-09b4-4ef9-8a12-b6c486d55ad4` showed `fastmap_generate_surface_render` dominates `GenerateChunkImage` in fresh-world streaming: `0.6703 ms` render vs `0.0585 ms` extraction.
- Profile `406737b8-ec30-402f-8e13-323a0d7647fa` showed existing-world cold-cache startup is dominated by vanilla DB page reconstruction: `fastmap_page_db_build` at `57.90%` and `fastmap_page_load` at `41.18%`.
- A follow-up run in the same world after the first DB-index attempt still showed no `fastmap_page_db_index_build` row, meaning the first reflection path failed and page DB rebuild remained dominant: `fastmap_page_db_build` `60.72%`, `fastmap_page_load` `31.34%`.
- A second follow-up run still showed no `fastmap_page_db_index_build` or `fastmap_page_db_index_unavailable` rows. `fastmap_page_db_build` remained dominant: `74.47%`, `89.4437 ms` average, with `fastmap_page_load` at `24.04%`.
- `Map/FastPageMapLayer.cs` now records `fastmap_page_db_index_attempt` before trying to build the index so the next profile can distinguish "build not deployed / code path not reached" from "connection lookup failed".
- Profile `406737b8-ec30-402f-8e13-323a0d7647fa/fastmap-profile-client-20260508-063708.csv` still showed no `fastmap_page_db_index_attempt`, `fastmap_page_db_index_build`, or `fastmap_page_db_index_unavailable`. `fastmap_page_db_build` remained dominant: `76.68%`, `96.4673 ms` average.
- The next profile should validate `fastmap_page_db_index_attempt`, then either `fastmap_page_db_index_build` or `fastmap_page_db_index_unavailable`. If no attempt row appears again, assume the deployed DLL does not include the current local build or the client session was not restarted after deployment.
- Manual profiler start is too late for some cold-cache page work. Use startup auto-profiling for the next existing-world pageless-load test.
- Profile `406737b8-ec30-402f-8e13-323a0d7647fa/fastmap-profile-client-20260508-064155.csv` confirmed startup profiling and index build are active: `fastmap_page_db_index_attempt` appeared, and `fastmap_page_db_index_build` indexed `41777` vanilla map pieces in `216.530 ms`.
- The DB index did not reduce `fastmap_page_db_build`; it remained dominant at `70.27%`, `111.3806 ms` average. This suggests queried pages are dense enough that absent-piece skipping is not the main cost.
- The next profile should use the new DB substage rows to rank lock wait vs `MapDB.GetMapPiece` deserialization vs tile copy.
- Profile `406737b8-ec30-402f-8e13-323a0d7647fa/fastmap-profile-client-20260508-064521.csv` showed the DB index working for absent pages: `fastmap_page_db_get_pieces` and `fastmap_page_db_copy_tiles` were `0`, while `fastmap_page_load` dropped to `879.211 ms` total. The remaining measured DB-related cost is mostly index build/lookup and lock wait: `fastmap_page_db_index_build` `224.747 ms`, `fastmap_page_db_index_lookup` `225.000 ms`, `fastmap_page_db_lock_wait` `414.000 ms`.
- Interpretation: for this run, many requested pages had no vanilla map pieces. The index prevented blind DB probes, but the profiler still sums parallel lock wait as cost. The next cold-cache optimization should avoid launching many DB-build workers that serialize on `dbLock`, or make page DB probing single-worker/budgeted so summed wait time and thread contention disappear.
- Follow-up change after `064521`: avoid allocating `FastMapPageComponent.PixelCount` and `ChunksPerPage` buffers for DB page misses. The next pageless-load profile should compare `fastmap_page_load` average against `4.0704 ms` and watch whether `fastmap_page_db_index_lookup`/`fastmap_page_db_lock_wait` become the clear remaining costs.
- Profile `406737b8-ec30-402f-8e13-323a0d7647fa/fastmap-profile-client-20260508-065114.csv` had both sparse misses and dense DB hits: `243` page misses with `candidates=0`, plus `72` page hits loading `46067` vanilla map pieces. `fastmap_page_db_build` returned as the top cost at `5994.833 ms`, `83.2616 ms` average, and `fastmap_page_db_lock_wait` was `5462.000 ms`.
- The `065114` DB substage rows revealed a profiler precision problem: `fastmap_page_db_copy_tiles` reported `0` despite `46067` copied tiles, because per-piece substage timings were truncated to whole milliseconds before accumulation. The next profile should use fractional accumulation to rank `GetMapPiece` vs copy time reliably.
- Next client-side diagnostic, if we stay here briefly: instrument raw `OnChunkDirty` event counts by chunk column and vertical chunk Y to prove whether repeated dirty waves are causing full tile regeneration after the queue already drained.
- Decision point: after one dirty-event multiplicity diagnostic, move the main optimization effort to either `FastMapSurfaceTile` caching or server worldgen delegate optimization. Do not spend many more cycles on queue delay/fanout heuristics.
- Still watch for correctness issues around water edges and unloaded neighbor chunks.

Measurement target:

- Compare `fastmap_generate_chunk_image` before/after.
- Compare surface extraction plus render against current full tile generation.
- Track queue waste: queued jobs, missing mapchunk jobs, missing source jobs, successful jobs.

### Phase 3: Server Worldgen Understanding

Status: In progress. Last updated: 2026-05-08.

- [x] Rank vanilla and modded worldgen delegates by total time.
- [x] Add p95/p99 reporting by worldgen delegate.
- [ ] Identify whether terrain, caves, vegetation, lighting, structures, or modded generators dominate.
- [ ] Add output hashing for controlled chunk columns so optimization prototypes can prove exactness.

Measurement target:

- Per delegate: count, total time, average, p95, pass, world type, declaring type, method.

### Phase 4: Server Worldgen Optimization Prototypes

Status: Not started.

- [ ] Prototype optimized terrain solid mask storage in `GenTerra`-equivalent logic: replace per-column `BitArray` with packed arrays.
- [ ] Investigate thread-local layer masks in terrain generation to reduce contention on shared `layerFullySolid` / `layerFullyEmpty`.
- [ ] Prototype map-first/surface-only generation that produces map chunks and surface samples without full chunk volumes.
- [ ] Prototype a budgeted generation scheduler for lower-priority work.

Measurement target:

- Exact full generation: aim for `2x-10x` in specific dominated passes.
- Surface/map-first generation: aim for `20x-100x` for non-interactive map/preview workloads.

## Current Hypotheses

- The fastest path to 100x is not exact full vanilla worldgen. It is a lower-tier representation: region maps, map chunks, heightmaps, and surface samples.
- `GenerateChunkImage` is a good candidate for large practical wins because repeated rasterization can be avoided with surface caching.
- Full server worldgen may still have meaningful wins, but we need per-generator timing before choosing a target.
- GPU acceleration is more promising for page recolor/shade/raster work than for exact vanilla chunk generation.

## Next Run Instructions

Start both profiles before testing:

- Client: `.fastmapprofile start`
- Server: `/fastmapprofile start`

After the test:

- Client: `.fastmapprofile flush`
- Server: `/fastmapprofile flush`

Summarize a profile:

- Default non-inclusive summary: `.\tools\Summarize-FastMapProfile.ps1 -Path <csv>`
- Include wrapper timings too: `.\tools\Summarize-FastMapProfile.ps1 -Path <csv> -IncludeInclusive`

Profiles will be written under:

- `C:\Users\chris\AppData\Roaming\VintagestoryData\FastMap\profiles\<save-id>\`
