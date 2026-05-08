using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace FastMap.Map;

internal static class FastMapWorldMapGuard
{
    private const string HarmonyId = "chrisunfocused.fastmap.worldmapguard";
    private static readonly object Sync = new();
    private static bool installed;
    private static bool loggedEmptyMapLayers;
    private static bool loggedEmptyLayerGroups;
    private static ILogger? guardLogger;

    public static void Install(ILogger logger)
    {
        lock (Sync)
        {
            if (installed)
            {
                return;
            }

            guardLogger = logger;
            Type? targetType = AccessTools.TypeByName("Vintagestory.GameContent.WorldMapManager");
            MethodInfo? targetMethod = targetType?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == "ToggleMap" && method.GetParameters().Length == 1);
            if (targetMethod == null)
            {
                logger.Warning("[FastMap] World map crash guard target not found.");
                return;
            }

            try
            {
                Harmony harmony = new(HarmonyId);
                harmony.Patch(targetMethod, prefix: new HarmonyMethod(typeof(FastMapWorldMapGuard).GetMethod(nameof(ToggleMapPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                installed = true;
                logger.Notification("[FastMap] World map crash guard installed.");
            }
            catch (Exception ex)
            {
                logger.Warning("[FastMap] World map crash guard failed to install: {0}", ex.Message);
            }
        }
    }

    private static bool ToggleMapPrefix(WorldMapManager __instance)
    {
        if (__instance.MapLayers == null || __instance.MapLayers.Count == 0)
        {
            if (!loggedEmptyMapLayers)
            {
                loggedEmptyMapLayers = true;
                guardLogger?.Warning(
                    "[FastMap] Prevented world map open while no map layers were loaded. This avoids a vanilla empty-tab crash; try opening the map again after the world finishes loading.");
            }

            return false;
        }

        bool hasLayerGroup = false;
        for (int i = 0; i < __instance.MapLayers.Count; i++)
        {
            if (!string.IsNullOrEmpty(__instance.MapLayers[i]?.LayerGroupCode))
            {
                hasLayerGroup = true;
                break;
            }
        }

        if (!hasLayerGroup)
        {
            if (!loggedEmptyLayerGroups)
            {
                loggedEmptyLayerGroups = true;
                guardLogger?.Warning(
                    "[FastMap] Prevented world map open because loaded map layers had no layer groups. This avoids a vanilla empty-tab crash.");
            }

            return false;
        }

        return true;
    }
}
