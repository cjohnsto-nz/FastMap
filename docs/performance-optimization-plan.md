# FastMap / Vintage Story Performance Optimization Plan

This file is the working notebook for the long-horizon optimization effort. Keep it updated whenever we add instrumentation, run a profile, or complete an optimization experiment.

## Goal

Explore performance changes that could make map and world generation dramatically faster, with a BHAG target of 100x for workloads that do not require exact full vanilla chunk simulation.

The near-term focus is not seasonal maps specifically. The broader target is to separate "what the player needs to see or know now" from "a fully generated, fully simulated vanilla chunk column."

## Current Measured Bottlenecks

Latest useful run:

- Profile directory: `C:\Users\chris\AppData\Roaming\VintagestoryData\FastMap\profiles\0a9f599b-89ef-4855-9dfb-ed4d08796f07`
- Client profile: `fastmap-profile-client-20260507-123452.csv`
- Server profile: `fastmap-profile-server-20260507-123448.csv`

Ranked leaf-ish stages from that run:

- `server_worldgen_delegate`: about `51.8s` total across individual worldgen delegate calls.
- `fastmap_generate_pixel_loop`: about `39.6s` total, now isolated as the dominant FastMap image substage.
- `fastmap_generate_color_multiply`: about `1.35s` total.
- `server_mainthread_load_column`: about `1.09s` total.
- `fastmap_page_upload`: about `1.02s` total.
- `client_load_chunk_packet`: about `0.66s` total.
- `fastmap_generate_blur`: about `0.57s` total.
- `server_chunk_to_packet`: about `0.43s` total.

Important interpretation:

- `server_supply_column_step` is inclusive wrapper timing and should not be added to `server_populate_worldgen_pass`.
- `fastmap_chunk_repair_generated` is inclusive wrapper timing and should not be added to `fastmap_generate_chunk_image`.

Top server worldgen delegates in the latest run:

- `Vintagestory.ServerMods.GenTerra.OnChunkColumnGen`: `12994.861 ms`, `2386` calls, `5.4463 ms` average.
- `Vintagestory.ServerMods.GenLightSurvival.OnChunkColumnGeneration`: `12115.847 ms`, `1993` calls, `6.0792 ms` average.
- `Vintagestory.ServerMods.GenDeposits.GenChunkColumn`: `6654.167 ms`, `2206` calls, `3.0164 ms` average.
- `Vintagestory.ServerMods.GenPartial.GenChunkColumn`: `4210.208 ms`, `2387` calls, `1.7638 ms` average.
- `Vintagestory.ServerMods.GenVegetationAndPatches.OnChunkColumnGen`: `3002.680 ms`, `1993` calls, `1.5066 ms` average.
- `Vintagestory.ServerMods.GenBlockLayers.OnChunkColumnGeneration`: `2623.334 ms`, `2387` calls, `1.0990 ms` average.
- `Vintagestory.ServerMods.GenRockStrataNew.GenChunkColumn`: `2258.349 ms`, `2387` calls, `0.9461 ms` average.
- `Vintagestory.ServerMods.GenLightSurvival.OnChunkColumnGenerationFlood`: `2192.739 ms`, `1774` calls, `1.2360 ms` average.

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

- [x] Add basic client/server CSV profiling.
- [x] Add server chunk supply, worldgen pass, chunk serialization, and client chunk packet timings.
- [x] Add FastMap chunk image, repair, patch, upload, and save timings.
- [x] Add `kind` and `category` fields so summaries can ignore inclusive wrapper stages.
- [x] Add per-worldgen-generator timing by wrapping `ServerEventAPI.ChunkColumnGeneration(...)`.
- [x] Add `GenerateChunkImage` substage timings for chunk prefetch, mapchunk fetch, pixel loop, blur, and final color multiply.
- [ ] Add repair reason tracking: `prewarm`, `chunkdirty`, `viewport`, `manual`, `season-invalidated`.

Implemented on 2026-05-08:

- `Profiling/FastMapProfileRecorder.cs` now writes `kind` and `category` columns.
- `Profiling/FastMapHarmonyPatches.cs` patches `Vintagestory.Server.ServerEventAPI.ChunkColumnGeneration(...)`.
- `Profiling/FastMapWorldgenDelegateProfiler.cs` wraps registered `ChunkColumnGenerationDelegate` handlers and records `server_worldgen_delegate`.
- `Map/FastPageMapLayer.cs` records `fastmap_generate_prefetch_chunks`, `fastmap_generate_fetch_mapchunks`, `fastmap_generate_pixel_loop`, `fastmap_generate_blur`, and `fastmap_generate_color_multiply`.
- `tools/Summarize-FastMapProfile.ps1` now ignores inclusive wrapper rows by default and can include them with `-IncludeInclusive`.

Measure after Phase 1:

- Fresh world fast flight.
- Existing generated world fast flight.
- Standing still with map open.
- `/wgen pregen [radius]`.
- `/wgen regen [radius]`.

### Phase 2: FastMap Image Generation Wins

Status: In progress. Last updated: 2026-05-08.

- [x] Prototype direct chunk data reads: call `Unpack_ReadOnly()` once per vertical chunk, then read from `chunk.Data` rather than `UnpackAndReadBlock()` per pixel.
- [ ] Add `FastMapSurfaceTile` cache containing `height[1024]`, `topBlockId[1024]`, and flags.
- [ ] Split surface extraction from tile rendering so repeated map rendering can reuse surface data.
- [ ] Prototype page-level generation instead of chunk-level generation.
- [ ] Evaluate GPU page rasterization after surface buffers exist.

Implemented on 2026-05-08:

- `Map/FastPageMapLayer.cs` now pre-unpacks vertical chunks once in `GenerateChunkImage(...)`.
- `Map/FastPageMapLayer.cs` now reads top blocks with `chunk.Data.GetBlockId(...)` through `ReadBlockId(...)` instead of `UnpackAndReadBlock(...)` per pixel.
- `fastmap_chunk_repair_generated` and `fastmap_chunk_repair_missing_source` are now marked `kind=inclusive`.

Needs validation:

- Re-run client profiling and compare `fastmap_generate_pixel_loop` against the `0a9f599b-89ef-4855-9dfb-ed4d08796f07` baseline.
- Watch for correctness issues around water edges and unloaded neighbor chunks.

Measurement target:

- Compare `fastmap_generate_chunk_image` before/after.
- Compare surface extraction plus render against current full tile generation.
- Track queue waste: queued jobs, missing mapchunk jobs, missing source jobs, successful jobs.

### Phase 3: Server Worldgen Understanding

Status: In progress. Last updated: 2026-05-08.

- [x] Rank vanilla and modded worldgen delegates by total time.
- [ ] Add p95/p99 reporting by worldgen delegate.
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
