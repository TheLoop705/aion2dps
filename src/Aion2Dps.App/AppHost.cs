using System.Collections.Concurrent;
using Aion2Dps.App.Dashboard;
using Aion2Dps.App.Formatting;
using Aion2Dps.App.Infrastructure;
using Aion2Dps.App.Integration;
using Aion2Dps.App.Overlay;
using Aion2Dps.App.Settings;
using Aion2Dps.App.Theming;

namespace Aion2Dps.App;

/// <summary>
/// Runtime orchestration: settings → theme → services (ServiceFactory) → capture into the pipeline → engine;
/// engine.EncounterCompleted → Store.Save on a background worker + personal-best toast; 1 Hz engine Tick;
/// overlay, dashboard, tray icon and global hotkeys.
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly Application _app;
    private readonly LaunchOptions _options;
    private readonly BlockingCollection<EncounterRecord> _saveQueue = new(256);
    private readonly ConcurrentDictionary<Guid, byte> _trainingRecords = new();
    private FileLogSink? _log;
    private SettingsStore _settings = null!;
    private AppServices _services = null!;
    private OverlayController _overlay = null!;
    private DashboardWindow? _dashboard;
    private TrayIcon? _tray;
    private GlobalHotkeys? _hotkeys;
    private DispatcherTimer? _tickTimer;
    private Thread? _saveWorker;
    private bool _disposed;

    public AppHost(Application app, LaunchOptions options)
    {
        _app = app;
        _options = options;
    }

    public AppServices Services => _services;

    public void Start()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        _log = new FileLogSink(AppPaths.LogDirectory);
        _log.Install();
        AppLog.Info("App", $"Aion2Dps {AppPaths.Version} starting ({_options.Mode})");

        _settings = new SettingsStore(AppPaths.SettingsFile);
        _settings.Load();
        ThemeManager.ApplyToApplication(_settings.Current.Appearance);

        _services = CreateServices();
        ApplyServiceSettings();
        _services.Engine.Mode = _settings.Current.Overlay.DefaultMode;
        _services.Engine.EncounterCompleted += OnEncounterCompleted;
        _saveWorker = new Thread(SaveLoop) { IsBackground = true, Name = "Aion2Dps fight saver" };
        _saveWorker.Start();

        StartCapture();

        _overlay = new OverlayController(_services, _settings, OpenDashboard);
        _overlay.StateChanged += UpdateTray;
        _overlay.TrainingFinished += OnTrainingFinished;
        if (_settings.Current.General.LaunchOverlayOnStart && _settings.Current.Overlay.Visible && !_options.NoOverlay)
            _overlay.Show();

        _tray = new TrayIcon(new TrayIcon.Actions(
            ToggleOverlay: () => _overlay.Toggle(),
            OpenDashboard: () => OpenDashboard(null),
            Reset: () => _overlay.Reset(),
            StartTraining: len => { if (!_overlay.IsVisible) _overlay.Show(); _overlay.StartTraining(len); },
            ToggleLock: () => _overlay.ToggleLockAndClickThrough(),
            Quit: Quit)
        {
            TogglePartyOnly = () => _overlay.TogglePartyOnly(),
        });
        _tray.SetTooltip($"Aion2Dps · {_services.ModeLabel}");
        _settings.Changed += _ => UpdateTray();   // keeps the tray checkmarks in sync with the Settings page
        UpdateTray();

        if (_settings.Current.General.EnableGlobalHotkeys) RegisterHotkeys();

        _tickTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _tickTimer.Tick += (_, _) =>
        {
            try { _services.Engine.Tick(_services.Now()); }
            catch (Exception ex) { AppLog.Error("App", "Engine tick failed", ex); }
        };
        _tickTimer.Start();

        if (_settings.Current.General.OpenDashboardOnStart) OpenDashboard(null, activate: false);
        AppLog.Info("App", "Started");
    }

    private AppServices CreateServices()
    {
        // --demo is the only mode with fake data. Live capture without Npcap is not an error here: the capture
        // service reports NpcapMissing and the overlay/dashboard show how to install it.
        if (_options.Mode == LaunchMode.Demo) return ServiceFactory.CreateDemo();
        return ServiceFactory.CreateReal(_options, AppPaths.DataDirectory);
    }

    /// <summary>Pushes settings that live in services (engine options, language, adapter).</summary>
    public void ApplyServiceSettings()
    {
        var g = _settings.Current.General;
        var opt = _services.Engine.Options;
        opt.IdleTimeoutSeconds = g.IdleTimeoutSeconds;
        opt.BossIdleTimeoutSeconds = g.BossIdleTimeoutSeconds;
        opt.EndedDisplaySeconds = g.EndedDisplaySeconds;
        opt.LivePlayerClock = g.LivePlayerClock;
        opt.PartyOnly = g.PartyOnly;
        opt.SaveTrashFights = g.SaveTrashFights;
        try { _services.GameData.Language = g.Language; } catch (Exception ex) { AppLog.Warn("App", $"Language change failed: {ex.Message}"); }
        _services.Capture.AdapterOverride = g.AdapterOverride;
    }

    private void StartCapture()
    {
        try
        {
            _services.Capture.Start(_services.PipelineInput);
            _services.AfterStart?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Error("App", "Capture start failed", ex);
        }
    }

    public void RestartCapture()
    {
        try { _services.Capture.Stop(); } catch (Exception ex) { AppLog.Warn("App", $"Capture stop failed: {ex.Message}"); }
        ApplyServiceSettings();
        StartCapture();
        _overlay.Flash("Capture restarted");
    }

    private void RegisterHotkeys()
    {
        try
        {
            _hotkeys = new GlobalHotkeys();
            foreach (var b in GlobalHotkeys.Defaults())
            {
                Action action = b.Id switch
                {
                    "reset" => () => _overlay.Reset(),
                    "copy" => () => _overlay.CopySummary(),
                    "overlay" => () => _overlay.Toggle(),
                    "lock" => () => { if (!_overlay.IsVisible) _overlay.Show(); _overlay.ToggleLockAndClickThrough(); },
                    "party" => () => _overlay.TogglePartyOnly(),
                    _ => () => { },
                };
                _hotkeys.Register(b, action);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("App", "Hotkey registration failed", ex);
        }
    }

    public void OpenDashboard(string? page) => OpenDashboard(page, activate: true);

    public void OpenDashboard(string? page, bool activate)
    {
        if (_dashboard is null)
        {
            var ctx = new DashboardContext
            {
                Services = _services,
                Settings = _settings,
                Log = _log,
                Hotkeys = () => _hotkeys?.Bindings ?? GlobalHotkeys.Defaults(),
                ApplyServiceSettings = ApplyServiceSettings,
                RestartCapture = RestartCapture,
                ToggleOverlay = () => _overlay.Toggle(),
                OverlayVisible = () => _overlay.IsVisible,
            };
            _dashboard = new DashboardWindow(ctx);
            _app.MainWindow = _dashboard;
        }
        _dashboard.ShowPage(page, activate);
    }

    private void UpdateTray()
    {
        _tray?.SetOverlayVisible(_overlay.IsVisible);
        _tray?.SetLocked(_settings.Current.Overlay.Locked && _settings.Current.Overlay.ClickThrough);
        _tray?.SetPartyOnly(_settings.Current.General.PartyOnly);
    }

    // ───────────────────────── Encounter completion ─────────────────────────

    /// <summary>Raised on the capture thread: queue only (never block or throw back into the pipeline).</summary>
    private void OnEncounterCompleted(EncounterRecord record)
    {
        try
        {
            if (record.Kind == EncounterKind.Training) _trainingRecords[record.Id] = 0;
            if (!_saveQueue.TryAdd(record)) AppLog.Warn("App", "Save queue full; dropping an encounter");
        }
        catch (Exception ex) { AppLog.Error("App", "EncounterCompleted handling failed", ex); }
    }

    private void SaveLoop()
    {
        try
        {
            foreach (var record in _saveQueue.GetConsumingEnumerable())
            {
                try
                {
                    var toast = PersonalBest.Check(_services.Store, record, _services.GameData);
                    _services.Store.Save(record);
                    AppLog.Info("App", $"Saved {record.Kind} encounter {record.Id} ({record.Outcome}, {record.DurationSeconds:0} s)");
                    if (toast is { } t)
                        _app.Dispatcher.BeginInvoke(() => _overlay.ShowToastOnce(record.Id, t.Title, t.Message));
                }
                catch (Exception ex) { AppLog.Error("App", "Saving an encounter failed", ex); }
            }
        }
        catch (ObjectDisposedException) { }
    }

    private void OnTrainingFinished(TimeSpan length)
    {
        int before = _trainingRecords.Count;
        // Give the engine a moment to publish the training record; say so when nothing qualified.
        var check = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        check.Tick += (_, _) =>
        {
            check.Stop();
            if (_trainingRecords.Count == before)
                _overlay.ShowToastOnce(null, "Training run not saved", $"The {Fmt.Duration(length)} run was too short or did no damage.");
            _services.Engine.Mode = _settings.Current.Overlay.DefaultMode;
        };
        check.Start();
    }

    // ───────────────────────── Shutdown ─────────────────────────

    public void Quit()
    {
        AppLog.Info("App", "Quit requested");
        if (_dashboard is not null) _dashboard.AllowClose = true;
        _app.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _tickTimer?.Stop();
            _settings?.Save();
            _hotkeys?.Dispose();
            _tray?.Dispose();
            _overlay?.Dispose();
            _saveQueue.CompleteAdding();
            _saveWorker?.Join(TimeSpan.FromSeconds(3));
            _services?.Dispose();
        }
        catch (Exception ex) { AppLog.Error("App", "Shutdown error", ex); }
        AppLog.Info("App", "Stopped");
        _log?.Dispose();
        AppLog.SetSink(null);
    }
}

/// <summary>Personal-best check for a completed encounter (run BEFORE saving it).</summary>
public static class PersonalBest
{
    public readonly record struct Toast(string Title, string Message);

    public static Toast? Check(IFightStore store, EncounterRecord record, IGameData gameData)
    {
        if (record.Kind != EncounterKind.Boss || record.Outcome != EncounterOutcome.Kill) return null;
        if (record.BossNpcCode is not { } boss || string.IsNullOrEmpty(record.LocalPlayerName)) return null;
        var local = record.Combatants.FirstOrDefault(c => c.IsLocal);
        if (local is null || local.Dps <= 0) return null;
        TrendPoint? prev;
        try { prev = store.GetPersonalBest(boss, record.LocalPlayerName!); }
        catch (Exception ex) { AppLog.Warn("App", $"Personal best lookup failed: {ex.Message}"); return null; }
        string bossName = gameData.GetNpcName(boss);
        if (prev is null)
            return new Toast("First kill recorded", $"{bossName}: {Fmt.Abbrev(local.Dps)} DPS in {Fmt.Duration(record.DurationSeconds)}");
        if (prev.FightId == record.Id || local.Dps <= prev.Dps) return null;
        double gain = prev.Dps > 0 ? local.Dps / prev.Dps - 1 : 0;
        return new Toast("New personal best!", $"{bossName}: {Fmt.Abbrev(local.Dps)} DPS (+{Fmt.Percent(gain)} vs {Fmt.Abbrev(prev.Dps)})");
    }
}
