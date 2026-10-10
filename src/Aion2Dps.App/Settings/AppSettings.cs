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
    /// <summary>2: the overlay lists the top 5 rows by default (was 10; files still on the old default move to 5).</summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public OverlaySettings Overlay { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public GeneralSettings General { get; set; } = new();
    public TimerSettings Timers { get; set; } = new();
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
    /// <summary>Out of combat the overlay shrinks to a slim one-row status bar; it expands for fights (and on click).</summary>
    public bool ShrinkWhenIdle { get; set; } = true;
    /// <summary>
    /// Bars only: the overlay is just the DPS bars (name + DPS per row) with no header, footer or window box. Click a
    /// bar for the fight breakdown, drag a bar to move the overlay (while unlocked); settings live in the tray menu.
    /// </summary>
    public bool BarsOnly { get; set; } = true;
    public RowSize RowSize { get; set; } = RowSize.Compact;
    public MeterView View { get; set; } = MeterView.Dps;
    public BarMode BarMode { get; set; } = BarMode.RelativeToTop;
    public MeterMode DefaultMode { get; set; } = MeterMode.BossOnly;
    public PvpSort PvpSort { get; set; } = PvpSort.Threat;
    /// <summary>
    /// How many rows the overlay lists (the top N of the ranking). When you are not in the top N, the last row is yours,
    /// with your real rank, so you always compare yourself against the top N − 1.
    /// </summary>
    public int MaxRows { get; set; } = DefaultMaxRows;

    public const int DefaultMaxRows = 5;

    // Column visibility
    public bool ShowTotal { get; set; } = true;
    public bool ShowContribution { get; set; } = true;
    public bool ShowCritRate { get; set; }
    public bool ShowMaxHit { get; set; }
    public bool ShowGearScore { get; set; }
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
    /// <summary>How long a finished fight stays on the full overlay before it shrinks to the slim bar (zero = 15 s). The
    /// fight itself is never cleared: it stays (on the bar, click to expand) until the next fight.</summary>
    public double EndedDisplaySeconds { get; set; } = 60;
    /// <summary>
    /// Track boss fights only (default on): trash mobs never start or replace a fight on the meter; a finished boss fight
    /// stays up until the next boss is engaged. Training runs, dummies and PvP still count.
    /// </summary>
    public bool BossFightsOnly { get; set; } = true;
    public bool PartyOnly { get; set; }
    public bool SaveTrashFights { get; set; }
    public bool LaunchOverlayOnStart { get; set; } = true;
    public bool OpenDashboardOnStart { get; set; } = true;
    /// <summary>
    /// Live capture: the overlay appears while the AION 2 client runs and hides a few seconds after it exits (default on;
    /// files written before this setting existed get the default). Off = the overlay follows the options above as before.
    /// </summary>
    public bool ShowOverlayOnlyWhileGameRuns { get; set; } = true;
    /// <summary>The one-time "runs in the tray" notice after the first Windows autostart was shown.</summary>
    public bool AutostartNoticeShown { get; set; }
    /// <summary>
    /// The one-time "start with Windows on" default for installed copies was applied (or found an existing decision). After
    /// that only the registry decides, so the user's untick sticks. Not a mirror of the registry state.
    /// </summary>
    public bool AutostartDefaultApplied { get; set; }
    /// <summary>Where recordings go; null = %USERPROFILE%/Documents/Aion2Dps/captures.</summary>
    public string? CaptureFolder { get; set; }
    public bool EnableGlobalHotkeys { get; set; } = true;
}

/// <summary>Rift / event / field-boss timers (Dashboard → Timers).</summary>
public sealed class TimerSettings
{
    /// <summary>Server clock: a region code of <c>timers.json</c> (EU, NAE, NAW, …) or a time zone id.</summary>
    public string ServerRegion { get; set; } = "EU";
    /// <summary>Tray notifications before starred timers.</summary>
    public bool AlertsEnabled { get; set; } = true;
    public int AlertMinutesBefore { get; set; } = 5;
    /// <summary>Also notify when a starred field boss is up (its respawn time arrives).</summary>
    public bool AlertOnSpawn { get; set; } = true;
    /// <summary>
    /// Star overrides: "event:rift" / "boss:111021" → starred or not. Missing keys use the default (starred events of
    /// timers.json, important field bosses).
    /// </summary>
    public Dictionary<string, bool> Stars { get; set; } = new();
    /// <summary>Field-boss list shows starred bosses only.</summary>
    public bool StarredBossesOnly { get; set; }
    /// <summary>The timers window (shown with the DPS overlay, in and out of combat) lists the timers due within <see cref="UpcomingMinutes"/>.</summary>
    public bool ShowTimerWindow { get; set; } = true;
    /// <summary>How far ahead the timers window looks (minutes).</summary>
    public int UpcomingMinutes { get; set; } = 60;
    /// <summary>Timers window position (device-independent pixels); null = next to the overlay.</summary>
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    /// <summary>Your own timers and your changes to the built-in ones (Settings → Timers).</summary>
    public List<TimerEntry> Entries { get; set; } = new();
}

/// <summary>
/// A timer entered in Settings: an override of a built-in event of timers.json (same <see cref="Id"/>, only the set
/// fields change it), or your own (<see cref="Custom"/>): a server-time schedule or a countdown you start yourself.
/// </summary>
public sealed class TimerEntry
{
    public string Id { get; set; } = "";
    public bool Custom { get; set; }
    /// <summary>False hides the timer (built-in or own).</summary>
    public bool Enabled { get; set; } = true;
    public string? Name { get; set; }
    /// <summary>Server time of day "HH:mm".</summary>
    public string? Start { get; set; }
    /// <summary>Repeat within the day (0 = once per day).</summary>
    public int? EveryMinutes { get; set; }
    /// <summary>Only on these days (null/empty = every day).</summary>
    public List<DayOfWeek>? Days { get; set; }
    public int? DurationMinutes { get; set; }
    /// <summary>Countdown length; set = this entry is a countdown (started from the Timers page), not a schedule.</summary>
    public int? CountdownMinutes { get; set; }
    public DateTime? CountdownStartedUtc { get; set; }
    /// <summary>A boss respawn: when due the boss is "Up", and Killed restarts the <see cref="CountdownMinutes"/> respawn.</summary>
    public bool Respawn { get; set; }
    /// <summary>Starts the next round on its own every <see cref="CountdownMinutes"/> (a boss on a fixed cycle).</summary>
    public bool AutoRepeat { get; set; }

    [JsonIgnore]
    public bool IsCountdown => Custom && CountdownMinutes is > 0;
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
