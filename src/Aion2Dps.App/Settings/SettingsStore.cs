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
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tmp, path, overwrite: true);
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
        var o = s.Overlay;
        o.Width = double.IsFinite(o.Width) ? Math.Clamp(o.Width, 300, 1000) : 380;
        o.BackgroundOpacity = double.IsFinite(o.BackgroundOpacity) ? Math.Clamp(o.BackgroundOpacity, 0.15, 1) : 0.92;
        o.MaxRows = Math.Clamp(o.MaxRows, 1, 24);
        o.TrainingSeconds = Math.Clamp(o.TrainingSeconds, 30, 300);
        if (o.Left is { } l && !double.IsFinite(l)) o.Left = null;
        if (o.Top is { } t && !double.IsFinite(t)) o.Top = null;
        var g = s.General;
        g.IdleTimeoutSeconds = Math.Clamp(g.IdleTimeoutSeconds, 3, 600);
        g.BossIdleTimeoutSeconds = Math.Clamp(g.BossIdleTimeoutSeconds, 5, 600);
        var a = s.Appearance;
        if (string.IsNullOrWhiteSpace(a.ThemeId) || Theming.ThemeCatalog.Find(a.ThemeId) is null) a.ThemeId = Theming.ThemeCatalog.DefaultId;
        if (a.CornerRadius is { } cr) a.CornerRadius = Math.Clamp(cr, 0, 16);
        if (a.FontSize is { } fs) a.FontSize = Math.Clamp(fs, 9, 18);
        if (!Theming.ColorUtil.TryParse(a.SingleColor, out _)) a.SingleColor = "#D9A441";
        if (a.Background is not null && !Theming.ColorUtil.TryParse(a.Background, out _)) a.Background = null;
        if (a.Accent is not null && !Theming.ColorUtil.TryParse(a.Accent, out _)) a.Accent = null;
        if (a.Text is not null && !Theming.ColorUtil.TryParse(a.Text, out _)) a.Text = null;
    }
}
