using System.Threading;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace FastMap.Map;

public sealed class FastMapTerrainFallbackLayer : MapLayer
{
    private const string SettingKey = "fastmapShowTerrainFallbackLayer";
    private static int active;
    private readonly ICoreClientAPI capi;
    private bool persistedActive;

    public FastMapTerrainFallbackLayer(ICoreAPI api, IWorldMapManager mapSink)
        : base(api, mapSink)
    {
        capi = (ICoreClientAPI)api;
        persistedActive = capi.Settings.Bool.Exists(SettingKey) && capi.Settings.Bool[SettingKey];
        Active = persistedActive;
        PublishActiveState();
    }

    public static bool IsFallbackLayerActive => Volatile.Read(ref active) != 0;

    public override string Title => "Pregen";

    public override string LayerGroupCode => "fastmap-terrain-fallback";

    public override EnumMapAppSide DataSide => EnumMapAppSide.Client;

    public override bool RequireChunkLoaded => false;

    public override void OnLoaded()
    {
        PublishActiveState();
    }

    public override void OnTick(float dt)
    {
        PersistIfChanged();
        PublishActiveState();
    }

    public override void Render(GuiElementMap mapElem, float dt)
    {
        PersistIfChanged();
        PublishActiveState();
    }

    public override void Dispose()
    {
        Volatile.Write(ref active, 0);
    }

    private void PersistIfChanged()
    {
        if (Active == persistedActive)
        {
            return;
        }

        persistedActive = Active;
        capi.Settings.Bool.Set(SettingKey, Active, false);
    }

    private void PublishActiveState()
    {
        Volatile.Write(ref active, Active ? 1 : 0);
    }
}
