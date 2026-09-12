using System.Collections.Generic;

namespace FastMap.Map;

// Vanilla's map worker keeps enumerating the previous list after publication.
// Never modify a list that has already been assigned to WorldMapManager.
internal static class MapLayerList
{
    public static List<T> Replace<T>(List<T> layers, int index, T replacement)
    {
        var result = new List<T>(layers);
        result[index] = replacement;
        return result;
    }

    public static List<T> Insert<T>(List<T> layers, int index, T layer)
    {
        var result = new List<T>(layers);
        result.Insert(index, layer);
        return result;
    }

    public static List<T> RemoveAt<T>(List<T> layers, int index)
    {
        var result = new List<T>(layers);
        result.RemoveAt(index);
        return result;
    }
}
