# FastMap / Vintage Story Performance Optimization Plan

This file is the working notebook for the long-horizon optimization effort. Keep it updated whenever we add instrumentation, run a profile, or complete an optimization experiment.

## Goal

Explore performance changes that could make map and world generation dramatically faster, with a BHAG target of 100x for workloads that do not require exact full vanilla chunk simulation.

The near-term focus is not seasonal maps specifically. The broader target is to separate "what the player needs to see or know now" from "a fully generated, fully simulated vanilla chunk column."

## Current Measured Bottlenecks

Latest useful run:

- Profile directory: `C:\Users\chris\AppData\Roaming\VintagestoryData\FastMap\profiles\446089a3-b518-4443-9e5e-c15b9a71da3b`
- Client profile: `fastmap-profile-client-20260507-191047.csv`
- Server profile: `fastmap-profile-server-20260507-191041.csv`

Ranked leaf-ish stages from that run:

- `server_worldgen_delegate`: `97.82%` of measured non-inclusive server work.
- `fastmap_generate_pixel_loop`: `90.02%` of measured non-inclusive client work.
- `fastmap_generate_color_multiply`: `3.12%` of measured non-inclusive client work.
- `fastmap_page_upload`: `2.90%` of measured non-inclusive client work.
- `client_load_chunk_packet`: `2.33%` of measured non-inclusive client work.
- `server_mainthread_load_column`: `1.35%` of measured non-inclusive server work.
- `server_chunk_to_packet`: `0.78%` of measured non-inclusive server work.

Important interpretation:

- `server_supply_column_step` is inclusive wrapper timing and should not be added to `server_populate_worldgen_pass`.
- `fastmap_chunk_repair_generated` is inclusive wrapper timing and should not be added to `fastmap_generate_chunk_image`.

Top server worldgen delegates in the latest run:

- `Vintagestory.ServerMods.GenTerra.OnChunkColumnGen`: `22.73%` of measured delegate work.
- `Vintagestory.ServerMods.GenLightSurvival.OnChunkColumnGeneration`: `20.54%`.
- `Vintagestory.ServerMods.GenDeposits.GenChunkColumn`: `13.37%`.
- `Vintagestory.ServerMods.GenRockStrataNew.GenChunkColumn`: `10.54%`.
- `Vintagestory.ServerMods.GenPartial.GenChunkColumn`: `8.74%`.
- `Vintagestory.ServerMods.GenVegetationAndPatches.OnChunkColumnGen`: `5.23%`.
- `Vintagestory.ServerMods.GenBlockLayers.OnChunkColumnGeneration`: `4.95%`.
- `Vintagestory.ServerMods.GenStructures.OnChunkColumnGen`: `4.40%`.

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

Newest proportional run:

- Profile directory: `C:\Users\chris\AppData\Roaming\VintagestoryData\FastMap\profiles\446089a3-b518-4443-9e5e-c15b9a71da3b`
- Client profile: `fastmap-profile-client-20260507-191047.csv`
- Server profile: `fastmap-profile-server-20260507-191041.csv`

Client proportional shape:

- `fastmap_generate_pixel_loop`: `90.02%` of measured non-inclusive client work, `0.7362 ms` average.
- `fastmap_generate_color_multiply`: `3.12%`, `0.0255 ms` average.
- `fastmap_page_upload`: `2.90%`, `0.2831 ms` average.
- `client_load_chunk_packet`: `2.33%`, `0.0504 ms` average.
- `fastmap_generate_blur`: `0.68%`, `0.0056 ms` average.
- `fastmap_generate_prefetch_chunks`: `0.63%`, `0.0051 ms` average.
- `fastmap_page_load`: `0.09%`, `6.1263 ms` average.

Server proportional shape:

- `server_worldgen_delegate`: `97.82%` of measured non-inclusive server work.
- `server_mainthread_load_column`: `1.35%`.
- `server_chunk_to_packet`: `0.78%`.
- `server_generate_empty_column`: `0.03%`.
- `server_try_load_column`: `0.02%`.

Interpretation:

- Direct chunk data reads remain directionally plausible but are not proven by non-repeatable runs. The latest profile is back near the prior pixel-loop average.
- Proportionally, the pixel loop remains the FastMap hotspot. The optimization helped, but did not change the architecture-level bottleneck.
- Server-side work remains overwhelmingly worldgen delegate execution.
- The client generated `32457` tile images while receiving `12280` client chunk packets. That is about `2.64` generated tiles per streamed chunk packet.
- The minimal fanout experiment now looks directionally useful after a second run, but the pixel loop remains the overwhelmingly dominant client cost.

Repair reason findings from the latest run:

- `fastmap_chunk_repair_queued|chunkdirty;mode=force`: `32246`.
- `fastmap_chunk_repair_generated|chunkdirty`: `31043`, `24621.495 ms`.
- `fastmap_chunk_repair_skipped_missing_mapchunk|prewarm`: `22662`.
- `fastmap_chunk_repair_queued|prewarm;mode=normal`: `906`.
- `fastmap_chunk_repair_generated|prewarm`: `874`, `603.825 ms`.
- `fastmap_chunk_repair_missing_mapchunk|chunkdirty`: `715`.
- `fastmap_chunk_repair_generated|chunkdirty+prewarm`: `509`, `495.407 ms`.

Interpretation:

- `chunkdirty` is the dominant source of successful map image generation.
- `prewarm` missing-mapchunk work is now being skipped before queueing, which validates the source-aware prewarm change.
- `chunkdirty` remains the dominant source of successful map image generation and is now the best target for reducing redundant rasterization.
- Normal continuous map generation returned after disabling the dirty debounce by default.
- Minimal dirty fanout looks provisionally useful, but the next priority is per-tile image generation cost rather than further queue-neighborhood tuning.

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

Validation notes:

- Profile `4ee43fcf-03ad-42a2-a6bd-5ca56fc1f384` showed `fastmap_generate_pixel_loop` at `0.7332 ms` average vs prior `0.8158 ms` average. Treat as directional because the run was not repeatable.
- Profile `77d183ce-4c68-4d78-a3c2-ac79065d3404` showed `fastmap_generate_pixel_loop` at `0.8126 ms` average, so the direct-read improvement is not conclusive from non-repeatable runs.
- Profile `ae03a890-78ca-4ea0-a710-90d3b67d2950` showed prewarm queue entries dropping from `27698` to `753`, with `23868` `fastmap_chunk_repair_skipped_missing_mapchunk|prewarm` events. The prewarm skip is working.
- Profile `32e0f691-73b5-47dd-a8ec-ca18a369d6af` showed the dirty debounce experiment was not gameplay-acceptable: `14312` client chunk packets produced only `1308` generated map tiles, and map generation visibly lagged/discontinued. Treat this as a profiling-only negative result.
- Profile `f5f2e6bb-8762-4a9b-9a14-93fa4a33d9dc` verified normal generation returned with the dirty debounce disabled: `8888` client chunk packets produced `31862` generated map tiles.
- Profile `95bbc456-367e-4d20-b602-5c0d8b970714` tested `UseMinimalChunkDirtyRepairFanout=true`: `10192` client chunk packets produced `33215` generated map tiles. This is a modest ratio improvement over `f5f2e6bb`, but not the hoped-for one-third win.
- Profile `446089a3-b518-4443-9e5e-c15b9a71da3b` strengthened the minimal fanout signal: `12280` client chunk packets produced `32457` generated map tiles, about `2.64x`.
- The next profile should validate the precomputed block-id flag/color optimization. Watch `fastmap_generate_pixel_loop` average and generated tile count ratio together.
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
