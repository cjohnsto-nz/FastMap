using System;
using System.Collections.Generic;

namespace FastMap.Network;

internal static class TerrainPrewarmPlanner
{
    internal const int PageSize = 1024;

    // Ring order keeps spawn/player neighbourhoods useful before filling distant pages.
    public static IEnumerable<TerrainSamplingRequest> Around(IEnumerable<(int X, int Z)> centres,
        int radiusPages, int step, int sizeX, int sizeZ)
    {
        var pages = new List<(int X, int Z)>();
        var unique = new HashSet<(int, int)>();
        foreach (var centre in centres)
        {
            var page = (Math.Clamp(centre.X, 0, sizeX - 1) / PageSize,
                Math.Clamp(centre.Z, 0, sizeZ - 1) / PageSize);
            if (unique.Add(page)) pages.Add(page);
        }
        unique.Clear();
        step = Math.Clamp(step, 1, 32);
        int width = (PageSize + step - 1) / step + 1;
        for (int ring = 0; ring <= Math.Clamp(radiusPages, 0, 16); ring++)
            foreach (var centre in pages)
                for (int z = -ring; z <= ring; z++)
                    for (int x = -ring; x <= ring; x++)
                    {
                        if (Math.Max(Math.Abs(x), Math.Abs(z)) != ring) continue;
                        var page = (X: centre.X + x, Z: centre.Z + z);
                        if (page.X < 0 || page.Z < 0 || !unique.Add(page)) continue;
                        var request = new TerrainSamplingRequest { Id = 1,
                            X = page.X * PageSize - step, Z = page.Z * PageSize - step,
                            Width = width, Height = width, Step = step };
                        if (request.IsValid(sizeX, sizeZ)) yield return request;
                    }
    }
}
