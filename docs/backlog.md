# Backlog

- Seasonal fallback palettes: fallback pages currently bake sampled palette colours into low-resolution textures. Explore runtime tinting or season-banded cache variants so fallback terrain can shift with seasons without regenerating every page too aggressively.
- Terrain Sampler PR: propose a `SampleColumn(int worldX, int worldZ)` API that exposes height, climate colour, rainfall, temperature, forest density, and shrub density. See `docs/terrain-sampler-integration.md`.
