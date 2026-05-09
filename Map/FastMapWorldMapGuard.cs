using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace FastMap.Map;

internal static class FastMapWorldMapGuard
{
    private const string HarmonyId = "chrisunfocused.fastmap.worldmapguard";
    private const float VanillaWorldMapMinZoom = 0.25f;
    private const float VanillaWorldMapMaxZoom = 6f;
    private static readonly object Sync = new();
    private static bool installed;
    private static bool loggedEmptyMapLayers;
    private static bool loggedEmptyLayerGroups;
    private static bool loggedCustomZoomLimit;
    private static ILogger? guardLogger;
    private static FieldInfo? waypointField;
    private static FieldInfo? waypointLayerField;
    private static FieldInfo? waypointMouseOverField;
    private static FieldInfo? waypointColorField;
    private static FieldInfo? waypointMvMatField;
    private static Type? oreMapComponentType;
    private static FieldInfo? oreReadingField;
    private static FieldInfo? oreLayerField;
    private static FieldInfo? oreMouseOverField;
    private static FieldInfo? oreColorField;
    private static FieldInfo? oreMvMatField;
    private static FieldInfo? orePositionField;
    private static FieldInfo? oreTextureField;
    private static FieldInfo? oreQuadModelField;

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
            Type? guiMapType = AccessTools.TypeByName("Vintagestory.GameContent.GuiElementMap");
            MethodInfo? zoomAddMethod = guiMapType?.GetMethod("ZoomAdd", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Type? waypointComponentType = AccessTools.TypeByName("Vintagestory.GameContent.WaypointMapComponent");
            MethodInfo? waypointRenderMethod = waypointComponentType?.GetMethod("Render", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            oreMapComponentType = AccessTools.TypeByName("Vintagestory.GameContent.OreMapComponent");
            Type? oreMapLayerType = AccessTools.TypeByName("Vintagestory.GameContent.OreMapLayer");
            Type? propickReadingType = AccessTools.TypeByName("Vintagestory.GameContent.PropickReading");
            MethodInfo? oreMapRenderMethod = oreMapComponentType?.GetMethod("Render", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (targetMethod == null)
            {
                logger.Warning("[FastMap] World map crash guard target not found.");
                return;
            }

            if (zoomAddMethod == null)
            {
                logger.Warning("[FastMap] World map zoom patch target not found.");
                return;
            }

            if (waypointRenderMethod == null)
            {
                logger.Warning("[FastMap] Waypoint zoom patch target not found.");
                return;
            }

            if (oreMapRenderMethod == null)
            {
                logger.Warning("[FastMap] Ore map zoom patch target not found.");
                return;
            }

            waypointField = AccessTools.Field(typeof(WaypointMapComponent), "waypoint");
            waypointLayerField = AccessTools.Field(typeof(WaypointMapComponent), "wpLayer");
            waypointMouseOverField = AccessTools.Field(typeof(WaypointMapComponent), "mouseOver");
            waypointColorField = AccessTools.Field(typeof(WaypointMapComponent), "color");
            waypointMvMatField = AccessTools.Field(typeof(WaypointMapComponent), "mvMat");
            oreReadingField = oreMapComponentType == null ? null : AccessTools.Field(oreMapComponentType, "reading");
            oreLayerField = oreMapComponentType == null ? null : AccessTools.Field(oreMapComponentType, "oreLayer");
            oreMouseOverField = oreMapComponentType == null ? null : AccessTools.Field(oreMapComponentType, "mouseOver");
            oreColorField = oreMapComponentType == null ? null : AccessTools.Field(oreMapComponentType, "color");
            oreMvMatField = oreMapComponentType == null ? null : AccessTools.Field(oreMapComponentType, "mvMat");
            orePositionField = propickReadingType == null ? null : AccessTools.Field(propickReadingType, "Position");
            oreTextureField = oreMapLayerType == null ? null : AccessTools.Field(oreMapLayerType, "oremapTexture");
            oreQuadModelField = oreMapLayerType == null ? null : AccessTools.Field(oreMapLayerType, "quadModel");

            try
            {
                Harmony harmony = new(HarmonyId);
                harmony.Patch(targetMethod, prefix: new HarmonyMethod(typeof(FastMapWorldMapGuard).GetMethod(nameof(ToggleMapPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                harmony.Patch(zoomAddMethod, prefix: new HarmonyMethod(typeof(FastMapWorldMapGuard).GetMethod(nameof(ZoomAddPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                harmony.Patch(waypointRenderMethod, prefix: new HarmonyMethod(typeof(FastMapWorldMapGuard).GetMethod(nameof(WaypointRenderPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                harmony.Patch(oreMapRenderMethod, prefix: new HarmonyMethod(typeof(FastMapWorldMapGuard).GetMethod(nameof(OreMapRenderPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                installed = true;
                logger.Notification("[FastMap] World map guard and zoom patches installed.");
            }
            catch (Exception ex)
            {
                logger.Warning("[FastMap] World map patches failed to install: {0}", ex.Message);
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

    private static bool ZoomAddPrefix(GuiElementMap __instance, float zoomDiff, float px, float pz)
    {
        FastMap.Config.FastMapConfig? config = FastMapModSystem.Instance?.Config;
        float minZoom = config?.WorldMapMinZoomLevel ?? VanillaWorldMapMinZoom;
        float maxZoom = config?.WorldMapMaxZoomLevel ?? VanillaWorldMapMaxZoom;

        if (Math.Abs(minZoom - VanillaWorldMapMinZoom) < 0.0001f
            && Math.Abs(maxZoom - VanillaWorldMapMaxZoom) < 0.0001f)
        {
            return true;
        }

        if (!loggedCustomZoomLimit)
        {
            loggedCustomZoomLimit = true;
            guardLogger?.Notification("[FastMap] Using custom world map zoom range: min={0:0.##}, max={1:0.##}", minZoom, maxZoom);
        }

        float currentZoom = __instance.ZoomLevel;
        float nextZoom = currentZoom + zoomDiff;
        bool customMin = Math.Abs(minZoom - VanillaWorldMapMinZoom) >= 0.0001f;
        bool customMax = Math.Abs(maxZoom - VanillaWorldMapMaxZoom) >= 0.0001f;
        bool useCustomScaling = customMin || customMax;

        if (useCustomScaling && Math.Abs(zoomDiff) > 0.0001f)
        {
            float zoomFactor = 1f + Math.Abs(zoomDiff);
            nextZoom = zoomDiff > 0f
                ? currentZoom * zoomFactor
                : currentZoom / zoomFactor;
        }

        if (zoomDiff < 0f && nextZoom < minZoom)
        {
            if (!customMin)
            {
                return false;
            }

            nextZoom = minZoom;
        }

        if (zoomDiff > 0f && nextZoom > maxZoom)
        {
            if (!customMax)
            {
                return false;
            }

            nextZoom = maxZoom;
        }

        if (Math.Abs(nextZoom - __instance.ZoomLevel) < 0.0001f)
        {
            return false;
        }

        __instance.ZoomLevel = nextZoom;
        double inverseZoom = 1f / __instance.ZoomLevel;
        double widthDelta = __instance.Bounds.InnerWidth * inverseZoom - __instance.CurrentBlockViewBounds.Width;
        double heightDelta = __instance.Bounds.InnerHeight * inverseZoom - __instance.CurrentBlockViewBounds.Length;
        __instance.CurrentBlockViewBounds.X2 += widthDelta;
        __instance.CurrentBlockViewBounds.Z2 += heightDelta;
        __instance.CurrentBlockViewBounds.Translate(-widthDelta * px, 0.0, -heightDelta * pz);
        __instance.EnsureMapFullyLoaded();
        return false;
    }

    private static bool WaypointRenderPrefix(object __instance, GuiElementMap map, float dt)
    {
        if (__instance is not WaypointMapComponent waypointComponent)
        {
            return true;
        }

        Vec2f viewPos = new();
        object? waypointValue = waypointField?.GetValue(waypointComponent);
        object? wpLayerValue = waypointLayerField?.GetValue(waypointComponent);
        object? mouseOverValue = waypointMouseOverField?.GetValue(waypointComponent);
        object? colorValue = waypointColorField?.GetValue(waypointComponent);
        object? mvMatValue = waypointMvMatField?.GetValue(waypointComponent);
        if (waypointValue is not Waypoint waypoint
            || wpLayerValue is not WaypointMapLayer wpLayer
            || mouseOverValue is not bool mouseOver
            || colorValue is not Vec4f color
            || mvMatValue is not Matrixf mvMat)
        {
            return true;
        }

        map.TranslateWorldPosToViewPos(waypoint.Position, ref viewPos);
        if (waypoint.Pinned)
        {
            map.Api.Render.PushScissor((ElementBounds)null!, false);
            map.ClampButPreserveAngle(ref viewPos, 2);
        }
        else if (viewPos.X < -10f || viewPos.Y < -10f || viewPos.X > map.Bounds.OuterWidth + 10.0 || viewPos.Y > map.Bounds.OuterHeight + 10.0)
        {
            return false;
        }

        float x = (float)(map.Bounds.renderX + viewPos.X);
        float y = (float)(map.Bounds.renderY + viewPos.Y);
        ICoreClientAPI api = map.Api;
        IShaderProgram engineShader = api.Render.GetEngineShader((EnumShaderProgram)17);
        engineShader.Uniform("rgbaIn", color);
        engineShader.Uniform("extraGlow", 0);
        engineShader.Uniform("applyColor", 0);
        engineShader.Uniform("noTexture", 0f);

        float inverseZoom = 1f / Math.Max(map.ZoomLevel, 0.001f);
        float clampedInverseZoom = Math.Min(inverseZoom, 2f);
        float sizeAdjust = (mouseOver ? 6f : 0f) - 1.5f * Math.Max(1f, clampedInverseZoom);

        LoadedTexture? value = null;
        if (wpLayer.texturesByIcon.TryGetValue(waypoint.Icon, out LoadedTexture? iconTexture))
        {
            value = iconTexture;
        }
        else if (wpLayer.texturesByIcon.TryGetValue("circle", out LoadedTexture? fallbackTexture))
        {
            value = fallbackTexture;
        }

        if (value != null)
        {
            engineShader.BindTexture2D("tex2d", value.TextureId, 0);
            engineShader.UniformMatrix("projectionMatrix", api.Render.CurrentProjectionMatrix);
            float halfWidth = Math.Max(2f, (value.Width + sizeAdjust) * 0.5f * WaypointMapComponent.IconScale);
            float halfHeight = Math.Max(2f, (value.Height + sizeAdjust) * 0.5f * WaypointMapComponent.IconScale);
            mvMat.Set(api.Render.CurrentModelviewMatrix).Translate(x, y, 60f).Scale(halfWidth, halfHeight, 0f);
            float guiScale = RuntimeEnv.GUIScale;
            Matrixf shadowMat = mvMat.Clone().Scale(1.1f, 1.1f, 1.1f).Translate(1.25 * guiScale / halfWidth, 1.25 * guiScale / halfHeight, 0.0);
            engineShader.Uniform("rgbaIn", new Vec4f(0f, 0f, 0f, 0.6f));
            engineShader.UniformMatrix("modelViewMatrix", shadowMat.Values);
            api.Render.RenderMesh(wpLayer.quadModel);
            engineShader.Uniform("rgbaIn", color);
            engineShader.UniformMatrix("modelViewMatrix", mvMat.Values);
            api.Render.RenderMesh(wpLayer.quadModel);
        }

        if (waypoint.Pinned)
        {
            map.Api.Render.PopScissor();
        }

        return false;
    }

    private static bool OreMapRenderPrefix(object __instance, GuiElementMap map, float dt)
    {
        if (oreMapComponentType == null || !oreMapComponentType.IsInstanceOfType(__instance))
        {
            return true;
        }

        Vec2f viewPos = new();
        object? reading = oreReadingField?.GetValue(__instance);
        object? oreLayer = oreLayerField?.GetValue(__instance);
        object? mouseOverValue = oreMouseOverField?.GetValue(__instance);
        object? colorValue = oreColorField?.GetValue(__instance);
        object? mvMatValue = oreMvMatField?.GetValue(__instance);

        if (reading == null
            || oreLayer == null
            || mouseOverValue is not bool mouseOver
            || colorValue is not Vec4f color
            || mvMatValue is not Matrixf mvMat)
        {
            return true;
        }

        object? positionValue = orePositionField?.GetValue(reading);
        if (positionValue is not Vec3d position)
        {
            return true;
        }

        map.TranslateWorldPosToViewPos(position, ref viewPos);
        if (viewPos.X < -10f || viewPos.Y < -10f || viewPos.X > map.Bounds.OuterWidth + 10.0 || viewPos.Y > map.Bounds.OuterHeight + 10.0)
        {
            return false;
        }

        float x = (float)(map.Bounds.renderX + viewPos.X);
        float y = (float)(map.Bounds.renderY + viewPos.Y);
        ICoreClientAPI api = map.Api;
        IShaderProgram engineShader = api.Render.GetEngineShader((EnumShaderProgram)17);
        engineShader.Uniform("rgbaIn", color);
        engineShader.Uniform("extraGlow", 0);
        engineShader.Uniform("applyColor", 0);
        engineShader.Uniform("noTexture", 0f);

        LoadedTexture? oreTexture = oreTextureField?.GetValue(oreLayer) as LoadedTexture;
        if (oreTexture == null)
        {
            return false;
        }

        float inverseZoom = 1f / Math.Max(map.ZoomLevel, 0.001f);
        float clampedInverseZoom = Math.Min(inverseZoom, 2f);
        float sizeAdjust = (mouseOver ? 6f : 0f) - 1.5f * Math.Max(1f, clampedInverseZoom);

        engineShader.BindTexture2D("tex2d", oreTexture.TextureId, 0);
        engineShader.UniformMatrix("projectionMatrix", api.Render.CurrentProjectionMatrix);
        float scaleX = Math.Max(2f, oreTexture.Width + sizeAdjust);
        float scaleY = Math.Max(2f, oreTexture.Height + sizeAdjust);
        mvMat.Set(api.Render.CurrentModelviewMatrix).Translate(x, y, 60f).Scale(scaleX, scaleY, 0f).Scale(0.5f * WaypointMapComponent.IconScale, 0.5f * WaypointMapComponent.IconScale, 0f);
        Matrixf shadowMat = mvMat.Clone().Scale(1.25f, 1.25f, 1.25f);
        engineShader.Uniform("rgbaIn", new Vec4f(0f, 0f, 0f, 0.7f));
        engineShader.UniformMatrix("modelViewMatrix", shadowMat.Values);
        object? quadModelValue = oreQuadModelField?.GetValue(oreLayer);
        if (quadModelValue is not MeshRef quadModel)
        {
            return false;
        }

        api.Render.RenderMesh(quadModel);
        engineShader.Uniform("rgbaIn", color);
        engineShader.UniformMatrix("modelViewMatrix", mvMat.Values);
        api.Render.RenderMesh(quadModel);
        return false;
    }
}
