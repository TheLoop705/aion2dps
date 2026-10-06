using System.Text.Json.Serialization;

namespace Aion2Dps.App.Settings;

public enum RowSize { Normal, Compact, Micro }

/// <summary>What the overlay's rows rank and show.</summary>
public enum MeterView { Dps, Total, Taken, Heal }

/// <summary>Bar fill: relative to the top row (default) or share of party damage.</summary>
public enum BarMode { RelativeToTop, ShareOfParty }

public enum BarStyle { Gradient, Solid, Slim }

/// <summary>Row colouring: class colours, a single custom colour, or the theme's bar colour.</summary>
public enum RowColorMode { ClassColors, SingleColor, ThemeColor }

public enum PvpSort { Threat, Damage }

/// <summary>All persisted user settings (%APPDATA%/Aion2Dps/settings.json). Plain POCO so System.Text.Json round-trips it.</summary>
public sealed class AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public OverlaySettings Overlay { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public GeneralSettings General { get; set; } = new();
    public WindowBounds? Dashboard { get; set; }

    public AppSettings Clone() =>
        System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(this, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;
}

public sealed class OverlaySettings
{
    /// <summary>Screen position in device-independent pixels; null = default placement.</summary>
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 380;
    /// <summary>Opacity of the overlay background (text stays opaque), 0.15..1.</summary>
    public double BackgroundOpacity { get; set; } = 0.92;
    public bool Locked { get; set; }
    /// <summary>Mouse clicks pass through to the game (only while locked).</summary>
    public bool ClickThrough { get; set; }
    public bool Visible { get; set; } = true;
    public RowSize RowSize { get; set; } = RowSize.Compact;
    public MeterView View { get; set; } = MeterView.Dps;
    public BarMode BarMode { get; set; } = BarMode.RelativeToTop;
    public MeterMode DefaultMode { get; set; } = MeterMode.BossOnly;
    public PvpSort PvpSort { get; set; } = PvpSort.Threat;
    public int MaxRows { get; set; } = 10;

    // Column visibility
    public bool ShowTotal { get; set; } = true;
    public bool ShowContribution { get; set; } = true;
    public bool ShowCritRate { get; set; }
    public bool ShowMaxHit { get; set; }
    public bool ShowRank { get; set; } = true;
    public bool ShowClassEmblem { get; set; } = true;
    public bool ShowColumnHeader { get; set; } = true;

    /// <summary>Last training length picked from the stopwatch menu.</summary>
    public int TrainingSeconds { get; set; } = 60;
}

public sealed class AppearanceSettings
{
    public string ThemeId { get; set; } = Theming.ThemeCatalog.DefaultId;
    public RowColorMode ColorMode { get; set; } = RowColorMode.ClassColors;
    /// <summary>#RRGGBB used when <see cref="ColorMode"/> is SingleColor.</summary>
    public string SingleColor { get; set; } = "#D9A441";
    /// <summary>Custom palette overrides (#RRGGBB or #AARRGGBB); null = use the theme's.</summary>
    public string? Background { get; set; }
    public string? Accent { get; set; }
    public string? Text { get; set; }
    public double? CornerRadius { get; set; }
    public string? FontFamily { get; set; }
    public double? FontSize { get; set; }
    public BarStyle BarStyle { get; set; } = BarStyle.Gradient;

    public bool HasOverrides => Background is not null || Accent is not null || Text is not null || CornerRadius is not null
                                || FontFamily is not null || FontSize is not null || ColorMode != RowColorMode.ClassColors;
}

public sealed class GeneralSettings
{
    public GameLanguage Language { get; set; } = GameLanguage.English;
    /// <summary>Capture adapter name; null = automatic.</summary>
    public string? AdapterOverride { get; set; }
    public bool LivePlayerClock { get; set; } = true;
    public double IdleTimeoutSeconds { get; set; } = 10;
    public double BossIdleTimeoutSeconds { get; set; } = 30;
    public bool PartyOnly { get; set; }
    public bool SaveTrashFights { get; set; }
    public bool LaunchOverlayOnStart { get; set; } = true;
    public bool OpenDashboardOnStart { get; set; } = true;
    /// <summary>Where recordings go; null = %USERPROFILE%/Documents/Aion2Dps/captures.</summary>
    public string? CaptureFolder { get; set; }
    public bool EnableGlobalHotkeys { get; set; } = true;
}

public sealed class WindowBounds
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    [JsonIgnore] public bool IsValid => Width >= 200 && Height >= 200 && double.IsFinite(Width) &&
                                       double.IsFinite(Height) && double.IsFinite(Left) && double.IsFinite(Top);
}
