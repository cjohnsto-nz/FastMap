using System;
using System.Threading;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace FastMap.Map;

public sealed class FastMapTerrainFallbackLayer : MapLayer
{
    private const string SettingKeyPrefix = "fastmapShowTerrainFallbackLayer:";
    private const string StyleSettingKeyPrefix = "fastmapTerrainFallbackStyle:";
    private const string SeasonSettingKeyPrefix = "fastmapTerrainFallbackSeason:";
    private const string BackgroundSettingKeyPrefix = "fastmapTerrainFallbackBackground:";
    private const string ComposerCode = "worldmap-layer-fastmap-terrain-fallback";
    private const string DropdownCode = "fastmap-pregen-style";
    private const string SeasonDropdownCode = "fastmap-pregen-season";
    private const string BackgroundSwitchCode = "fastmap-pregen-background";
    private const string BackgroundHoverCode = "fastmap-pregen-background-hover";
    private const string NormalStyleCode = "normal";
    private const string FogOfWarStyleCode = "fogofwar";
    private const string AutoSeasonCode = "auto";
    private static int active;
    private static int fogOfWarMode;
    private static int seasonOverrideMonth = -1;
    private static int backgroundActive;
    private static int generationModeVersion;
    private readonly ICoreClientAPI capi;
    private readonly string settingKey;
    private readonly string styleSettingKey;
    private readonly string seasonSettingKey;
    private readonly string backgroundSettingKey;
    private bool persistedActive;

    public FastMapTerrainFallbackLayer(ICoreAPI api, IWorldMapManager mapSink)
        : base(api, mapSink)
    {
        capi = (ICoreClientAPI)api;
        settingKey = SettingKeyPrefix + api.World.SavegameIdentifier;
        styleSettingKey = StyleSettingKeyPrefix + api.World.SavegameIdentifier;
        seasonSettingKey = SeasonSettingKeyPrefix + api.World.SavegameIdentifier;
        backgroundSettingKey = BackgroundSettingKeyPrefix + api.World.SavegameIdentifier;
        persistedActive = capi.Settings.Bool.Exists(settingKey) && capi.Settings.Bool[settingKey];
        Active = persistedActive;
        PublishStyleState(ReadPersistedStyle());
        PublishSeasonState(ReadPersistedSeasonOverrideMonth());
        PublishBackgroundState(ReadPersistedBackgroundState());
        PublishActiveState();
    }

    public static bool IsFallbackLayerActive => Volatile.Read(ref active) != 0;

    public static int GenerationModeVersion => Volatile.Read(ref generationModeVersion);

    public static bool UseFogOfWarStyle => Volatile.Read(ref fogOfWarMode) != 0;

    public static int SeasonOverrideMonth => Volatile.Read(ref seasonOverrideMonth);

    public static bool IsBackgroundGenerationActive => Volatile.Read(ref backgroundActive) != 0;

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

    public override void ComposeDialogExtras(GuiDialogWorldMap guiDialogWorldMap, GuiComposer compo)
    {
        string[] values = { NormalStyleCode, FogOfWarStyleCode };
        string[] names = { "Normal", "Fog of War" };
        int selectedIndex = UseFogOfWarStyle ? 1 : 0;
        bool showSeasonSelector = IsColorAccurateWorldmap();

        ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
            .WithFixedPosition(
                (compo.Bounds.renderX + compo.Bounds.OuterWidth) / RuntimeEnv.GUIScale + 10.0,
                compo.Bounds.renderY / RuntimeEnv.GUIScale + 122.0)
            .WithAlignment(EnumDialogArea.None);
        ElementBounds insetBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        insetBounds.BothSizing = ElementSizing.FitToChildren;
        ElementBounds paletteLabelBounds = ElementBounds.Fixed(0.0, 30.0, 160.0, 24.0);
        ElementBounds dropdownBounds = ElementBounds.Fixed(0.0, 56.0, 160.0, 35.0);
        ElementBounds? seasonLabelBounds = showSeasonSelector ? ElementBounds.Fixed(0.0, 108.0, 160.0, 24.0) : null;
        ElementBounds? seasonDropdownBounds = showSeasonSelector ? ElementBounds.Fixed(0.0, 134.0, 160.0, 35.0) : null;
        double backgroundY = showSeasonSelector ? 192.0 : 112.0;
        ElementBounds backgroundLabelBounds = ElementBounds.Fixed(0.0, backgroundY, 130.0, 24.0);
        ElementBounds backgroundSwitchBounds = ElementBounds.Fixed(130.0, backgroundY - 6.0, 30.0, 30.0);

        GuiComposer composer = capi.Gui.CreateCompo(ComposerCode, dialogBounds)
            .AddShadedDialogBG(insetBounds, withTitleBar: false, 5.0, 0.75f)
            .AddDialogTitleBar("Pregen", () => guiDialogWorldMap.Composers[ComposerCode].Enabled = false)
            .BeginChildElements(insetBounds)
            .AddStaticText("Palette", CairoFont.WhiteSmallText(), paletteLabelBounds)
            .AddDropDown(values, names, selectedIndex, OnPregenStyleChanged, dropdownBounds, DropdownCode);

        if (showSeasonSelector && seasonLabelBounds != null && seasonDropdownBounds != null)
        {
            string[] seasonValues =
            {
                AutoSeasonCode,
                "0",
                "1",
                "2",
                "3",
                "4",
                "5",
                "6",
                "7",
                "8",
                "9",
                "10",
                "11"
            };
            string[] seasonNames =
            {
                "Auto",
                "January",
                "February",
                "March",
                "April",
                "May",
                "June",
                "July",
                "August",
                "September",
                "October",
                "November",
                "December"
            };
            int seasonIndex = Math.Clamp(SeasonOverrideMonth + 1, 0, 12);
            composer
                .AddStaticText("Season", CairoFont.WhiteSmallText(), seasonLabelBounds)
                .AddDropDown(seasonValues, seasonNames, seasonIndex, OnSeasonChanged, seasonDropdownBounds, SeasonDropdownCode);
        }

        guiDialogWorldMap.Composers[ComposerCode] = composer
            .AddStaticText("Background", CairoFont.WhiteSmallText(), backgroundLabelBounds)
            .AddHoverText(
                "Pregenerate terrain while the map is closed. Will impact performance while generating.",
                CairoFont.WhiteSmallText(),
                220,
                backgroundLabelBounds,
                BackgroundHoverCode)
            .AddSwitch(OnBackgroundGenerationChanged, backgroundSwitchBounds, BackgroundSwitchCode)
            .EndChildElements()
            .Compose(true);
        guiDialogWorldMap.Composers[ComposerCode].GetSwitch(BackgroundSwitchCode).SetValue(IsBackgroundGenerationActive);
        guiDialogWorldMap.Composers[ComposerCode].Enabled = false;
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
        capi.Settings.Bool.Set(settingKey, Active, false);
    }

    private void PublishActiveState()
    {
        Volatile.Write(ref active, Active ? 1 : 0);
    }

    private void OnPregenStyleChanged(string code, bool selected)
    {
        if (!selected)
        {
            return;
        }

        bool useFogOfWar = code == FogOfWarStyleCode;
        if (UseFogOfWarStyle == useFogOfWar)
        {
            return;
        }

        capi.Settings.String.Set(styleSettingKey, useFogOfWar ? FogOfWarStyleCode : NormalStyleCode, false);
        PublishStyleState(useFogOfWar);
    }

    private void OnSeasonChanged(string code, bool selected)
    {
        if (!selected)
        {
            return;
        }

        int month = -1;
        if (code != AutoSeasonCode && (!int.TryParse(code, out month) || month < 0 || month > 11))
        {
            month = -1;
        }

        if (SeasonOverrideMonth == month)
        {
            return;
        }

        capi.Settings.Int.Set(seasonSettingKey, month, false);
        PublishSeasonState(month);
    }

    private void OnBackgroundGenerationChanged(bool enabled)
    {
        capi.Settings.Bool.Set(backgroundSettingKey, enabled, false);
        PublishBackgroundState(enabled);
    }

    private bool ReadPersistedStyle()
    {
        if (capi.Settings.String.Exists(styleSettingKey))
        {
            return capi.Settings.String[styleSettingKey] == FogOfWarStyleCode;
        }

        return false;
    }

    private int ReadPersistedSeasonOverrideMonth()
    {
        if (capi.Settings.Int.Exists(seasonSettingKey))
        {
            return Math.Clamp(capi.Settings.Int[seasonSettingKey], -1, 11);
        }

        return -1;
    }

    private bool ReadPersistedBackgroundState()
    {
        return capi.Settings.Bool.Exists(backgroundSettingKey) && capi.Settings.Bool[backgroundSettingKey];
    }

    private static void PublishStyleState(bool useFogOfWar)
    {
        int value = useFogOfWar ? 1 : 0;
        if (Interlocked.Exchange(ref fogOfWarMode, value) != value)
        {
            Interlocked.Increment(ref generationModeVersion);
        }
    }

    private static void PublishSeasonState(int month)
    {
        month = Math.Clamp(month, -1, 11);
        if (Interlocked.Exchange(ref seasonOverrideMonth, month) != month)
        {
            Interlocked.Increment(ref generationModeVersion);
        }
    }

    private static void PublishBackgroundState(bool enabled)
    {
        Volatile.Write(ref backgroundActive, enabled ? 1 : 0);
    }

    private bool IsColorAccurateWorldmap()
    {
        string[] privileges = capi.World.Player?.Privileges ?? Array.Empty<string>();
        return capi.World.Config.GetAsBool("colorAccurateWorldmap", false)
            || Array.IndexOf(privileges, "colorAccurateWorldmap") >= 0;
    }
}
