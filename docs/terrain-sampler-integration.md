# Terrain Sampler Integration Notes

FastMap can now consume richer Terrain Sampler column samples by reflection. The preferred API shape for Algernon's Terrain Sampler is:

```csharp
public readonly struct TerrainColumnSample
{
    public int Height { get; init; }
    public int ClimateColor { get; init; }
    // Normalized 0..1 values, matching the encoded climate map channels.
    public float Rainfall { get; init; }
    public float Temperature { get; init; }
    public float ForestDensity { get; init; }
    public float ShrubDensity { get; init; }
}

public TerrainColumnSample SampleColumn(int worldX, int worldZ);
```

FastMap currently looks for `SampleColumn`, `GetColumnSample`, `GetTerrainColumnSample`, or `GetTerrainSample`, then falls back to `GetBlockColumnHeight`.

## Vanilla Findings

- `IMapRegion.ClimateMap` stores temperature in red and rainfall in green.
- Terrain Sampler already creates its terrain context with `includeClimate: true`, so climate colour and adjusted rainfall can be exposed beside height without re-generating the terrain.
- `GenMaps` creates `forestGen` and `bushGen` with `GetForestMapGen(seed, scale)`, using `MapLayerWobbledForest`.
- During region generation, vanilla passes the generated `ClimateMap` into those map layers and writes `IMapRegion.ForestMap` and `IMapRegion.ShrubMap`.
- `MapLayerWobbledForest` combines wobbled simplex noise with climate suitability. Rainfall and temperature are read from the climate map's green/red channels, then dry or cold regions are suppressed.
- Actual tree placement still happens later in `GenVegetationAndPatches`, where the forest/shrub maps are bilinearly sampled per chunk column and combined with tree-gen properties. The maps are therefore good density signals, not exact tree positions.

## FastMap Behaviour

- If richer column samples are available, FastMap applies a subtle rainfall/temperature/vegetation tint to non-water fallback terrain colours.
- Water remains flat and untinted.
- If richer samples are unavailable, FastMap remains height-only and behaves as before.
