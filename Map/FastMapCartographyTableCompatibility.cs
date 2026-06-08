using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace FastMap.Map;

internal static class FastMapCartographyTableCompatibility
{
    private const string HarmonyId = "chrisunfocused.fastmap.compat.kscartographytable";
    private const string PlayerMapManagerTypeName = "Kaisentlaia.KsCartographyTableMod.GameContent.PlayerMapManager";
    private const string UpdateMapMethodName = "UpdateMap";
    private static readonly object Sync = new();
    private static bool installed;
    private static bool loggedImport;
    private static bool loggedFailure;
    private static ILogger? logger;
    private static PropertyInfo? piecesProperty;

    public static void Install(ICoreAPI api)
    {
        FastMapModDetectionResult detection = FastMapModDetection.DetectMod(api, "kscartographytable");
        if (!detection.Present)
        {
            return;
        }

        lock (Sync)
        {
            if (installed)
            {
                return;
            }

            logger = api.Logger;
            Type? playerMapManagerType = AccessTools.TypeByName(PlayerMapManagerTypeName);
            MethodInfo? updateMapMethod = playerMapManagerType?
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == UpdateMapMethodName && method.GetParameters().Length == 1);

            if (playerMapManagerType == null || updateMapMethod == null)
            {
                api.Logger.Warning(
                    "[FastMap] K's Cartography Table was detected via {0}, but its player map update method could not be found. FastMap compatibility shim was not installed.",
                    detection.Source);
                return;
            }

            piecesProperty = updateMapMethod.GetParameters()[0].ParameterType.GetProperty(
                "Pieces",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (piecesProperty == null)
            {
                api.Logger.Warning("[FastMap] K's Cartography Table map packet did not expose a Pieces property. FastMap compatibility shim was not installed.");
                return;
            }

            try
            {
                Harmony harmony = new(HarmonyId);
                harmony.Patch(
                    updateMapMethod,
                    prefix: new HarmonyMethod(typeof(FastMapCartographyTableCompatibility).GetMethod(nameof(UpdateMapPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                installed = true;
                api.Logger.Notification("[FastMap] K's Cartography Table compatibility shim installed.");
            }
            catch (Exception ex)
            {
                api.Logger.Warning("[FastMap] Failed to install K's Cartography Table compatibility shim: {0}", ex.Message);
            }
        }
    }

    private static bool UpdateMapPrefix(object packet)
    {
        try
        {
            if (packet == null || piecesProperty?.GetValue(packet) is not IReadOnlyDictionary<FastVec2i, MapPieceDB> pieces)
            {
                return true;
            }

            FastMapMapPieceImportResult result = FastMapModSystem.Instance?.ImportMapPieces(pieces, "kscartographytable")
                ?? FastMapMapPieceImportResult.NotHandled;
            if (result == FastMapMapPieceImportResult.NotHandled)
            {
                return true;
            }

            if (!loggedImport)
            {
                loggedImport = true;
                logger?.Notification("[FastMap] K's Cartography Table map download routed through FastMap map-piece compatibility API.");
            }

            return false;
        }
        catch (Exception ex)
        {
            if (!loggedFailure)
            {
                loggedFailure = true;
                logger?.Warning("[FastMap] K's Cartography Table compatibility shim failed; falling back to original handler: {0}", ex.Message);
            }

            return true;
        }
    }
}
