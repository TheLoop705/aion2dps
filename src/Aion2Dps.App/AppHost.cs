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
/// overlay, dashboard, tray icon and global hotkeys. "Start with Windows" (<see cref="AutostartRegistration"/>, launch flag
/// <c>--autostart</c>) and "Show the overlay only while AION 2 is running" (<see cref="GameProcessWatcher"/> +
/// <see cref="GameOverlayVisibilityPolicy"/>) are wired here.
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
    private AutostartRegistration? _autostart;
    private GameProcessWatcher? _gameWatcher;
    private readonly GameOverlayVisibilityPolicy _gamePolicy = new();
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

        _autostart = AutostartRegistration.ForCurrentProcess();
        bool autostartTurnedOn = false;
        if (_options.Mode == LaunchMode.Live)
        {
            ReconcileAutostart();
            autostartTurnedOn = ApplyAutostartDefault();
        }

        _overlay = new OverlayController(_services, _settings, OpenDashboard);
        _overlay.StateChanged += UpdateTray;
        _overlay.TrainingFinished += OnTrainingFinished;
        _overlay.UserVisibilityChanged += visible => { if (visible) _gamePolicy.OnUserShowed(); else _gamePolicy.OnUserHid(); };
        // With "only while AION 2 is running" the overlay starts hidden; the watcher's first check shows it when the game runs.
        if (GameOverlayVisibilityPolicy.ShowAtStartup(GameAwareWanted, _settings.Current.General.LaunchOverlayOnStart,
                _settings.Current.Overlay.Visible, _options.NoOverlay))
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
        _settings.Changed += _ => { ApplyGameAwareness(); UpdateTray(); };   // keeps the tray in sync with the Settings page
        ApplyGameAwareness();
        UpdateTray();
        if (autostartTurnedOn) ShowAutostartTurnedOnNotice();
        else ShowFirstAutostartNotice();

        if (_settings.Current.General.EnableGlobalHotkeys) RegisterHotkeys();

        _tickTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _tickTimer.Tick += (_, _) =>
        {
            try { _services.Engine.Tick(_services.Now()); }
            catch (Exception ex) { AppLog.Error("App", "Engine tick failed", ex); }
        };
        _tickTimer.Start();

        // Started by Windows: stay in the tray (a second launch still opens the dashboard through the single-instance guard).
        if (_settings.Current.General.OpenDashboardOnStart && !_options.Autostart) OpenDashboard(null, activate: false);
        AppLog.Info("App", _options.Autostart ? "Started (autostart, in the tray)" : "Started");
    }

    // ───────────────────────── Autostart & game detection ─────────────────────────

    /// <summary>"Show the overlay only while AION 2 is running" applies (live capture only: demo/sim/replay have no game).</summary>
    private bool GameAwareWanted => _options.Mode == LaunchMode.Live && _settings.Current.General.ShowOverlayOnlyWhileGameRuns;

    private bool AllowAutoShow => _settings.Current.General.LaunchOverlayOnStart && !_options.NoOverlay;

    private void ReconcileAutostart()
    {
        try
        {
            if (_autostart?.Reconcile() == true) AppLog.Info("App", $"Start-with-Windows entry updated to {_autostart.ExePath}");
        }
        catch (Exception ex) { AppLog.Warn("App", $"Could not check the start-with-Windows entry: {ex.Message}"); }
    }

    /// <summary>
    /// Once per user, for installed copies: switch "Start Aion2Dps with Windows" on (the feature exists so the meter is
    /// ready when AION 2 is started from Steam). An entry the user or Windows already decided about is left alone, and after
    /// this one time the registry alone decides, so unticking it in Settings → Startup sticks. Returns true when it was
    /// switched on now.
    /// </summary>
    private bool ApplyAutostartDefault()
    {
        var g = _settings.Current.General;
        if (g.AutostartDefaultApplied || _autostart is null) return false;
        try
        {
            if (_autostart.ApplyInstallDefault() is not { } created) return false;   // build output / loose copy: not now
            g.AutostartDefaultApplied = true;
            _settings.Save();   // before the overlay/tray exist: nothing to notify yet
            if (created) AppLog.Info("App", $"Start with Windows switched on ({_autostart.Command})");
            return created;
        }
        catch (Exception ex)
        {
            AppLog.Warn("App", $"Could not set up the start with Windows: {ex.Message}");
            return false;
        }
    }

    private void ShowAutostartTurnedOnNotice() =>
        _tray?.ShowBalloon("Aion2Dps", "Aion2Dps now starts with Windows in the tray, ready for AION 2. " +
                                       "Turn this off under Settings > Startup.");

    /// <summary>Starts/stops the game watcher to match the setting (also when it changes on the Settings page).</summary>
    private void ApplyGameAwareness()
    {
        bool wanted = GameAwareWanted;
        if (wanted == _gamePolicy.Enabled) return;
        if (wanted)
        {
            _gameWatcher ??= CreateGameWatcher();
            Execute(_gamePolicy.SetEnabled(true, _overlay.IsVisible, _settings.Current.Overlay.Visible, AllowAutoShow));
            _gameWatcher.Start();
            AppLog.Info("App", "Overlay follows the game: waiting for AION 2");
        }
        else
        {
            _gameWatcher?.Stop();
            Execute(_gamePolicy.SetEnabled(false, _overlay.IsVisible, _settings.Current.Overlay.Visible, AllowAutoShow));
        }
    }

    private GameProcessWatcher CreateGameWatcher()
    {
        var watcher = new GameProcessWatcher(post: a => _app.Dispatcher.BeginInvoke(a, DispatcherPriority.Background));
        watcher.GameStarted += () => OnGameEvent(() => _gamePolicy.OnGameStarted(_overlay.IsVisible, AllowAutoShow), "AION 2 started");
        watcher.GameExited += () => OnGameEvent(() => _gamePolicy.OnGameExited(_overlay.IsVisible), "AION 2 exited");
        watcher.GameNotRunning += () => OnGameEvent(() => _gamePolicy.OnGameNotRunning(_overlay.IsVisible), null);
        return watcher;
    }

    /// <summary>Runs on the UI thread. Late events from a watcher that was stopped meanwhile are ignored.</summary>
    private void OnGameEvent(Func<OverlayVisibilityCommand> decide, string? log)
    {
        if (_disposed || !_gamePolicy.Enabled || _gameWatcher?.IsStarted != true) return;
        try
        {
            if (log is not null) AppLog.Info("App", log);
            Execute(decide());
            UpdateTray();
        }
        catch (Exception ex) { AppLog.Error("App", "Game start/exit handling failed", ex); }
    }

    private void Execute(OverlayVisibilityCommand command)
    {
        if (command == OverlayVisibilityCommand.Show && !_overlay.IsVisible) _overlay.ShowAutomatically();
        else if (command == OverlayVisibilityCommand.Hide && _overlay.IsVisible) _overlay.HideAutomatically();
    }

    private void ShowFirstAutostartNotice()
    {
        var g = _settings.Current.General;
        if (!_options.Autostart || g.AutostartNoticeShown || _tray is null) return;
        _tray.ShowBalloon("Aion2Dps", GameOverlayVisibilityPolicy.AutostartNoticeText(_gamePolicy.Enabled, AllowAutoShow));
        g.AutostartNoticeShown = true;
        _settings.NotifyChanged(saveImmediately: true);
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
                Autostart = _autostart,
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
        var status = _gamePolicy.StatusText;
        _tray?.SetStatus(status);
        _tray?.SetTooltip(status is null ? $"Aion2Dps · {_services.ModeLabel}" : $"Aion2Dps · {status}");
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
            _gameWatcher?.Dispose();
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
