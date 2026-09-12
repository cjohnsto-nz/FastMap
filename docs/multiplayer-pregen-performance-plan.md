# Multiplayer Pregen performance

Current implementation plan, retaining 121.6.0 / 122.6.0 while under test:

Server sampling is opt-in: `EnableTerrainSampling=false` by default, matching x3200. This disables client Pregen access and all server prewarming until an administrator enables it.

1. Use completed pixel tiles for Pregen networking. Share the existing renderer between local rendering and the server; render all three palette variants from one sample grid. Preserve packed seasonal metadata. Keep detailed sample networking only for optional climate overlays.
2. Cache compressed tile variants in bounded server memory, including a reservation for the active build. Discard detailed samples after rendering. Stream only the requested variant in bounded fragments; enforce dimensions, offsets, decoded size and permissions.
3. Independently prewarm around spawn and online players on dedicated servers only. Run at spawn with no clients connected. Integrated single-player servers retain local Background Pregen without a second prewarm queue. Prioritise live requests, throttle under load, bound the desired area and avoid evicting useful client-requested data for speculative work. Clarify that the multiplayer client switch controls local prefetch only.
4. Preserve one persistent sampling worker for audited Terrain Sampler 1.3.0; retain main-thread sampling for unaudited generators. Always render and compress completed grids on the worker. Cancel and join at shutdown.
5. Test no-client prewarming with zero packets, pixel round trips including seasonal metadata, palette reuse without resampling, foreground priority, cache bounds, cancellation, permissions and malformed fragments. Build both game lines.
6. Deploy through Server Manager MCP. Verify prewarming with no connected players, then join with an empty client Pregen cache. Measure tile wire size and request-to-send time, verify rendered pixels against previous client-generated tiles, and inspect palette controls in game.
7. Submit every missing desired page through an acknowledged queue. Keep queued requests as metadata, send cached results independently of generation, and push periodic queue updates until the result is ready. Verify cached delivery with 100 cold requests blocked for 75 simulated seconds without network polling.
8. Hold page-load reservations until snapshots are applied, and skip resident fallback pages. Verify live counters no longer show repeated uploads of the same tiles starving missing pages. Keep received tiles compressed until consumption, and test a 300-page burst without refetching.
9. Cover the fully zoomed-out viewport: default radius 12 pages (25 by 25, reaching at least 12,288 blocks from the centre) and a 256 MiB tile cache. Retain bounds on actual compressed cache bytes and the active worker reservation. Measure sustained cold page production and client rendering separately from cached network delivery.

10. Negotiate a server rendering fingerprint and include it in remote disk-cache namespaces. Preserve unchanged-settings cache reuse and isolate changed server offsets, palettes and rendering parameters, including during a late handshake.

Earlier experiments sent full sample grids (1.65 MB raw / about 870 KB compressed per default page). These are superseded for Pregen. The coarse-first preview pass remains removed. The feature is under review in PR #11.
