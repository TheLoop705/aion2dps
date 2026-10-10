using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion2Dps.App.Settings;

/// <summary>
/// Loads/saves <see cref="AppSettings"/> as JSON. Saving is atomic (temp file + replace). A corrupt file is kept as
/// <c>settings.json.bad</c> and defaults are used, so a broken file never prevents start-up.
/// </summary>
public sealed class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _gate = new();
    private DispatcherTimer? _debounce;

    public SettingsStore(string path)
    {
        Path = path;
        Current = new AppSettings();
    }

    public string Path { get; }
    public AppSettings Current { get; private set; }

    /// <summary>Raised on the UI thread after <see cref="NotifyChanged"/> (the dashboard and overlay re-read settings).</summary>
    public event Action<AppSettings>? Changed;

    public AppSettings Load()
    {
        Current = LoadFrom(Path);
        return Current;
    }

    public static AppSettings LoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            Normalize(settings);
            return settings;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Settings", $"Could not read {path}, using defaults: {ex.Message}");
            try { File.Copy(path, path + ".bad", overwrite: true); } catch { /* best effort */ }
            return new AppSettings();
        }
    }

    public static void SaveTo(string path, AppSettings settings)
    {
        var identity = System.IO.Path.GetFullPath(path).ToUpperInvariant();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
        using var mutex = new Mutex(false, "Local\\Aion2Dps-settings-" + hash);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Another instance is still saving the settings.");
            SaveAtomic(path, settings);
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }

    private static void SaveAtomic(string path, AppSettings settings)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* a failed save must not hide its original error */ }
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            try { SaveTo(Path, Current); }
            catch (Exception ex) { AppLog.Warn("Settings", $"Could not save settings: {ex.Message}"); }
        }
    }

    /// <summary>Call after mutating <see cref="Current"/>: notifies listeners now and saves after a short debounce.</summary>
    public void NotifyChanged(bool saveImmediately = false)
    {
        Changed?.Invoke(Current);
        if (saveImmediately || Application.Current is null) { Save(); return; }
        _debounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) =>
        {
            _debounce!.Stop();
            Save();
        }, Dispatcher.CurrentDispatcher);
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Clamps values that would make the UI unusable (e.g. a hand-edited file).</summary>
    public static void Normalize(AppSettings s)
    {
        s.Overlay ??= new OverlaySettings();
        s.Appearance ??= new AppearanceSettings();
        s.General ??= new GeneralSettings();
        s.Timers ??= new TimerSettings();
        s.Timers.Stars ??= new Dictionary<string, bool>();
        s.Timers.Entries ??= new List<TimerEntry>();
        s.Timers.Entries.RemoveAll(e => e is null || string.IsNullOrWhiteSpace(e.Id));
        foreach (var e in s.Timers.Entries)
        {
            if (e.EveryMinutes is { } every) e.EveryMinutes = Math.Clamp(every, 0, 1440);
            if (e.DurationMinutes is { } dur) e.DurationMinutes = Math.Clamp(dur, 0, 1440);
            if (e.CountdownMinutes is { } cd) e.CountdownMinutes = Math.Clamp(cd, 1, 60 * 24 * 7);
        }
        s.Timers.AlertMinutesBefore = Math.Clamp(s.Timers.AlertMinutesBefore, 0, 60);
        s.Timers.UpcomingMinutes = Math.Clamp(s.Timers.UpcomingMinutes, 10, 360);
        if (s.Timers.WindowLeft is { } tl && !double.IsFinite(tl)) s.Timers.WindowLeft = null;
        if (s.Timers.WindowTop is { } tt && !double.IsFinite(tt)) s.Timers.WindowTop = null;
        if (string.IsNullOrWhiteSpace(s.Timers.ServerRegion)) s.Timers.ServerRegion = "EU";
        var o = s.Overlay;
        o.Width = double.IsFinite(o.Width) ? Math.Clamp(o.Width, 300, 1000) : 380;
        o.BackgroundOpacity = double.IsFinite(o.BackgroundOpacity) ? Math.Clamp(o.BackgroundOpacity, 0.15, 1) : 0.92;
        o.MaxRows = Math.Clamp(o.MaxRows, 1, 24);
        o.TrainingSeconds = Math.Clamp(o.TrainingSeconds, 30, 300);
        if (o.Left is { } l && !double.IsFinite(l)) o.Left = null;
        if (o.Top is { } t && !double.IsFinite(t)) o.Top = null;
        var g = s.General;
        g.IdleTimeoutSeconds = double.IsFinite(g.IdleTimeoutSeconds) ? Math.Clamp(g.IdleTimeoutSeconds, 3, 600) : 10;
        g.BossIdleTimeoutSeconds = double.IsFinite(g.BossIdleTimeoutSeconds) ? Math.Clamp(g.BossIdleTimeoutSeconds, 5, 600) : 30;
        g.EndedDisplaySeconds = double.IsFinite(g.EndedDisplaySeconds) ? Math.Clamp(g.EndedDisplaySeconds, 0, 120) : 60;
        var a = s.Appearance;
        if (string.IsNullOrWhiteSpace(a.ThemeId) || Theming.ThemeCatalog.Find(a.ThemeId) is null) a.ThemeId = Theming.ThemeCatalog.DefaultId;
        if (a.CornerRadius is { } cr) a.CornerRadius = double.IsFinite(cr) ? Math.Clamp(cr, 0, 16) : null;
        if (a.FontSize is { } fs) a.FontSize = double.IsFinite(fs) ? Math.Clamp(fs, 9, 18) : null;
        if (s.Dashboard is { IsValid: false }) s.Dashboard = null;
        if (!Enum.IsDefined(o.RowSize)) o.RowSize = RowSize.Compact;
        if (!Enum.IsDefined(o.View)) o.View = MeterView.Dps;
        if (!Enum.IsDefined(o.BarMode)) o.BarMode = BarMode.RelativeToTop;
        if (!Enum.IsDefined(o.DefaultMode)) o.DefaultMode = MeterMode.BossOnly;
        if (!Enum.IsDefined(o.PvpSort)) o.PvpSort = PvpSort.Threat;
        if (!Enum.IsDefined(g.Language)) g.Language = GameLanguage.English;
        if (!Enum.IsDefined(a.ColorMode)) a.ColorMode = RowColorMode.ClassColors;
        if (!Enum.IsDefined(a.BarStyle)) a.BarStyle = BarStyle.Gradient;
        if (!Theming.ColorUtil.TryParse(a.SingleColor, out _)) a.SingleColor = "#D9A441";
        if (a.Background is not null && !Theming.ColorUtil.TryParse(a.Background, out _)) a.Background = null;
        if (a.Accent is not null && !Theming.ColorUtil.TryParse(a.Accent, out _)) a.Accent = null;
        if (a.Text is not null && !Theming.ColorUtil.TryParse(a.Text, out _)) a.Text = null;
    }
}
