using Aion2Dps.App.Formatting;
using Aion2Dps.App.Infrastructure;
using Aion2Dps.App.Integration;
using Aion2Dps.App.Settings;

namespace Aion2Dps.App.Overlay;

/// <summary>
/// Drives the overlay: refreshes it ~4×/s from <see cref="ICombatEngine.GetSnapshot"/>, routes its buttons to the
/// engine/settings, runs the training countdown, ctrl+click compare and the copy-to-chat action. Out of combat it
/// shrinks the overlay to the compact bar (<see cref="OverlayPresentationTracker"/>; setting "Shrink overlay when not in
/// combat").
/// </summary>
public sealed class OverlayController : IDisposable
{
    private readonly AppServices _services;
    private readonly SettingsStore _settings;
    private readonly Action<string?> _openDashboard;
    private readonly DispatcherTimer _timer;
    private OverlayWindow? _window;
    private DateTime? _trainingEndsUtc;
    private TimeSpan _trainingLength;
    private string? _flash;
    private DateTime _flashUntil;
    private uint? _pinned;
    private CaptureStatus _capture;
    private readonly HashSet<Guid> _toasted = new();
    private readonly OverlayPresentationTracker _presentation = new();
    private readonly OverlayInteractionTracker _interaction = new();
    private double? _lastFightDps;
    private bool _refreshing;

    public OverlayController(AppServices services, SettingsStore settings, Action<string?> openDashboard)
    {
        _services = services;
        _settings = settings;
        _openDashboard = openDashboard;
        _capture = services.Capture.Status;
        services.Capture.StatusChanged += s => _capture = s;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Refresh();
        settings.Changed += _ => { if (_window is not null) { ApplyLockState(); Refresh(); SyncTimerWindow(_services.Now(), force: true); } };
    }

    public bool IsVisible => _window?.IsVisible == true;

    /// <summary>Raised when the overlay is shown/hidden (tray text) or locked/unlocked.</summary>
    public event Action? StateChanged;

    /// <summary>
    /// Raised when the user (or a user-triggered host action) shows (true) or hides (false) the overlay through
    /// <see cref="Show"/> / <see cref="Hide"/> / <see cref="Toggle"/>; not for <see cref="ShowAutomatically"/> /
    /// <see cref="HideAutomatically"/>.
    /// </summary>
    public event Action<bool>? UserVisibilityChanged;

    /// <summary>Raised when a training run ends (length) so the host can check that it produced a record.</summary>
    public event Action<TimeSpan>? TrainingFinished;

    public OverlayView? View => _window?.View;

    /// <summary>When the running/armed training countdown ends (null = none). For tests.</summary>
    internal DateTime? TrainingEndsUtc => _trainingEndsUtc;

    /// <summary>The current presentation (compact bar or full overlay).</summary>
    public OverlayPresentation Presentation => _presentation.Current;

    /// <summary>Expands the compact bar to the full overlay until the next fight (click on the bar / its chevron).</summary>
    public void Expand()
    {
        _presentation.Expand();
        Refresh();
    }

    /// <summary>Shrinks the full overlay to the compact bar (toolbar button); a fight in progress keeps it small until the next one.</summary>
    public void Collapse()
    {
        _presentation.Collapse();
        _window?.View.HideToast();
        Refresh();
    }

    public void Show()
    {
        ShowCore();
        UserVisibilityChanged?.Invoke(true);
    }

    /// <summary>Shows the overlay for an automatic reason (AION 2 started); not reported as a user choice.</summary>
    public void ShowAutomatically() => ShowCore();

    private void ShowCore()
    {
        if (_window is null)
        {
            _window = new OverlayWindow(_settings.Current.Overlay);
            Wire(_window.View);
            _window.BoundsChanged += SaveBounds;
            _window.CompactBarClicked += Expand;
            _window.MouseLeave += (_, _) => _interaction.Touch(DateTime.UtcNow);
            _window.SourceInitialized += (_, _) => ApplyLockState();
        }
        Refresh();
        _window.Show();
        _timer.Start();
        if (!_settings.Current.Overlay.Visible)
        {
            _settings.Current.Overlay.Visible = true;
            _settings.NotifyChanged();
        }
        StateChanged?.Invoke();
    }

    public void Hide()
    {
        HideCore(persist: true);
        UserVisibilityChanged?.Invoke(false);
    }

    /// <summary>
    /// Hides the overlay for an automatic reason (AION 2 exited). The persisted "overlay visible" choice is kept, so this is
    /// never mistaken for the user hiding it.
    /// </summary>
    public void HideAutomatically() => HideCore(persist: false);

    private void HideCore(bool persist)
    {
        _window?.Hide();
        _timerWindow?.Hide();
        _timer.Stop();
        if (persist)
        {
            _settings.Current.Overlay.Visible = false;
            _settings.NotifyChanged();
        }
        StateChanged?.Invoke();
    }

    public void Toggle()
    {
        if (IsVisible) Hide(); else Show();
    }

    private void Wire(OverlayView v)
    {
        v.ResetRequested += Reset;
        v.CopyRequested += () => CopySummary();
        v.SettingsRequested += () => _openDashboard("Settings");
        v.HideRequested += Hide;
        v.LockToggled += () =>
        {
            var o = _settings.Current.Overlay;
            o.Locked = !o.Locked;
            if (!o.Locked) o.ClickThrough = false;
            _settings.NotifyChanged();
            ApplyLockState();
            StateChanged?.Invoke();
        };
        v.CycleModeRequested += () =>
        {
            var next = _services.Engine.Mode switch
            {
                MeterMode.BossOnly => MeterMode.AllTargets,
                MeterMode.AllTargets => MeterMode.Pvp,
                _ => MeterMode.BossOnly,
            };
            _services.Engine.Mode = next;
            Refresh();
        };
        v.CycleRowSizeRequested += () =>
        {
            var o = _settings.Current.Overlay;
            o.RowSize = o.RowSize switch { RowSize.Normal => RowSize.Compact, RowSize.Compact => RowSize.Micro, _ => RowSize.Normal };
            _settings.NotifyChanged();
        };
        v.CycleViewRequested += () =>
        {
            var o = _settings.Current.Overlay;
            o.View = o.View switch { MeterView.Dps => MeterView.Total, MeterView.Total => MeterView.Taken, MeterView.Taken => MeterView.Heal, _ => MeterView.Dps };
            _settings.NotifyChanged();
        };
        v.CyclePvpSortRequested += () =>
        {
            var o = _settings.Current.Overlay;
            o.PvpSort = o.PvpSort == PvpSort.Threat ? PvpSort.Damage : PvpSort.Threat;
            _settings.NotifyChanged();
        };
        v.CycleTargetRequested += dir => { _services.Engine.CycleTarget(dir); Refresh(); };
        v.TrainingRequested += StartTraining;
        v.TrainingCancelRequested += StopTraining;
        v.OpacityChanged += value =>
        {
            _settings.Current.Overlay.BackgroundOpacity = Math.Round(value, 2);
            _settings.NotifyChanged();
        };
        v.RowClicked += OnRowClicked;
        v.PartyFilterToggled += TogglePartyOnly;
        v.ExpandRequested += Expand;
        v.CollapseRequested += Collapse;
    }

    /// <summary>Flips "party members only" (overlay chip, tray menu, Ctrl+Alt+P), persists it and applies it to the engine.</summary>
    public void TogglePartyOnly()
    {
        var g = _settings.Current.General;
        g.PartyOnly = !g.PartyOnly;
        _services.Engine.Options.PartyOnly = g.PartyOnly;
        _settings.NotifyChanged();
        Refresh();
        Flash(g.PartyOnly ? "Only your group, instances too" : "Instances: everyone there");
        StateChanged?.Invoke();
    }

    public void ToggleLockAndClickThrough()
    {
        var o = _settings.Current.Overlay;
        bool enable = !(o.Locked && o.ClickThrough);
        o.Locked = enable;
        o.ClickThrough = enable;
        _settings.NotifyChanged();
        ApplyLockState();
        Flash(enable ? "Locked · click-through on" : "Unlocked");
        StateChanged?.Invoke();
    }

    private void ApplyLockState()
    {
        var o = _settings.Current.Overlay;
        _window?.SetLockState(o.Locked, o.ClickThrough);
        _timerWindow?.SetLockState(o.Locked, o.ClickThrough);
    }

    private void SaveBounds()
    {
        if (_window is null) return;
        var o = _settings.Current.Overlay;
        // The anchor (not a temporary work-area shift) and the expanded width (not the compact bar's).
        o.Left = Math.Round(_window.Anchor.X);
        o.Top = Math.Round(_window.Anchor.Y);
        o.Width = Math.Round(_window.ExpandedWidth);
        _settings.NotifyChanged();
    }

    public void Reset()
    {
        _services.Engine.Reset();
        // Engine.Reset disarms (or discards) a training run, so its countdown goes too (as in StopTraining):
        // otherwise the overlay keeps counting down and the end fires a misleading "Training run not saved".
        _trainingEndsUtc = null;
        _pinned = null;
        _lastFightDps = null;
        Flash("Meter reset");
        Refresh();
    }

    public void StartTraining(TimeSpan length)
    {
        _services.Engine.StartTraining(length);
        _trainingLength = length;
        _trainingEndsUtc = DateTime.UtcNow + length;
        _settings.Current.Overlay.TrainingSeconds = (int)length.TotalSeconds;
        _settings.NotifyChanged();
        Flash($"Training {Fmt.Duration(length)} started");
        Refresh();
    }

    private void StopTraining()
    {
        _trainingEndsUtc = null;
        _services.Engine.Reset();
        _services.Engine.Mode = _settings.Current.Overlay.DefaultMode;
        Flash("Training stopped");
        Refresh();
    }

    /// <summary>Copies the chat summary line to the clipboard. Returns the line (null when nothing to copy).</summary>
    public string? CopySummary()
    {
        var snap = _services.Engine.GetSnapshot(_services.Now());
        var line = ChatSummary.FromSnapshot(snap);
        if (line is null)
        {
            Flash("Nothing to copy yet");
            return null;
        }
        try
        {
            Clipboard.SetDataObject(line, true);
            Flash("Summary copied — paste in chat");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Overlay", $"Clipboard busy: {ex.Message}");
            Flash("Clipboard busy, try again");
        }
        return line;
    }

    private void OnRowClicked(PlayerRow row, bool ctrl)
    {
        var record = _services.Engine.GetCurrentEncounter();
        if (record is null) return;
        if (ctrl)
        {
            if (_pinned is null || _pinned == row.EntityId)
            {
                _pinned = _pinned == row.EntityId ? null : row.EntityId;
                Flash(_pinned is null ? "Compare cleared" : $"{row.Name} pinned · ctrl+click another row");
            }
            else
            {
                AnalysisBridge.OpenCompare(record, _pinned.Value, row.EntityId, _services.GameData);
                _pinned = null;
            }
            Refresh();
            return;
        }
        AnalysisBridge.OpenBreakdown(record, row.EntityId, _services.GameData, _window);
    }

    public void Flash(string text, double seconds = 2.5)
    {
        _flash = text;
        _flashUntil = DateTime.UtcNow.AddSeconds(seconds);
        Refresh();
    }

    public void ShowToast(string title, string message)
    {
        if (_window is null) return;
        _window.View.ShowToast(title, message);
        if (!_refreshing) Refresh(); // a toast expands the compact bar while it shows
    }

    /// <summary>Shows a toast at most once per encounter id (null key = always).</summary>
    public void ShowToastOnce(Guid? key, string title, string message)
    {
        if (key is { } k && !_toasted.Add(k)) return;
        ShowToast(title, message);
    }

    /// <summary>Timers due soon for the timers window (set by the host; null = no timers window).</summary>
    public Func<DateTime, IReadOnlyList<UpcomingTimer>>? Upcoming { get; set; }

    private TimerOverlayWindow? _timerWindow;
    private DateTime _upcomingAt;

    /// <summary>The always-on timers window (null until first shown).</summary>
    public TimerOverlayWindow? TimerWindow => _timerWindow;

    /// <summary>The timers window is wanted: the overlay is shown, the host feeds timers and the setting is on.</summary>
    private bool TimerWindowWanted => Upcoming is not null && _settings.Current.Timers.ShowTimerWindow && IsVisible;

    /// <summary>Shows / hides / refreshes the timers window alongside the DPS overlay (1×/s).</summary>
    private void SyncTimerWindow(DateTime clock, bool force = false)
    {
        try
        {
            if (!TimerWindowWanted)
            {
                _timerWindow?.Hide();
                return;
            }
            if (_timerWindow is null)
            {
                _timerWindow = new TimerOverlayWindow(_settings.Current.Timers, _settings.Current.Overlay);
                _timerWindow.Clicked += () => _openDashboard("Timers");
                _timerWindow.Moved += () =>
                {
                    _settings.Current.Timers.WindowLeft = _timerWindow.Left;
                    _settings.Current.Timers.WindowTop = _timerWindow.Top;
                    _settings.NotifyChanged();
                };
                _timerWindow.SourceInitialized += (_, _) => ApplyLockState();
                force = true;
            }
            if (force || (DateTime.UtcNow - _upcomingAt).TotalSeconds >= 1)
            {
                _upcomingAt = DateTime.UtcNow;
                _timerWindow.View.BackgroundOpacity = _settings.Current.Overlay.BackgroundOpacity;
                _timerWindow.View.Update(Upcoming!(clock), _settings.Current.Timers.UpcomingMinutes);
            }
            if (!_timerWindow.IsVisible) _timerWindow.Show();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Overlay", $"Timers window failed: {ex.Message}");
        }
    }

    public void Refresh()
    {
        if (_window is null || _refreshing) return;
        _refreshing = true;
        try
        {
            var now = DateTime.UtcNow;
            if (_flash is not null && now > _flashUntil) _flash = null;
            TimeSpan? remaining = null;
            if (_trainingEndsUtc is { } end)
            {
                remaining = end - now;
                if (remaining <= TimeSpan.Zero)
                {
                    _trainingEndsUtc = null;
                    remaining = null;
                    TrainingFinished?.Invoke(_trainingLength);
                }
            }
            var clock = _services.Now();
            var snap = _services.Engine.GetSnapshot(clock);
            if (snap.PersonalBestMessage is { Length: > 0 } pb && snap.EncounterId is { } eid)
                ShowToastOnce(eid, "New personal best!", pb);
            if (snap.State is MeterState.InCombat or MeterState.Ended && snap.Rows.FirstOrDefault(r => r.IsLocal && r.Damage > 0) is { } me
                && double.IsFinite(me.Dps))
                _lastFightDps = me.Dps;
            var status = new OverlayStatus
            {
                Capture = _capture, TrainingRemaining = remaining, Flash = _flash, PinnedEntityId = _pinned, LastFightDps = _lastFightDps,
            };
            var settings = _settings.Current;
            _window.View.Update(snap, status, OverlayViewOptions.From(settings, AppPaths.Version));
            // Pointer / menu use is wall-clock UI state (the snapshot clock may be a replay clock).
            bool interacting = _interaction.Update(_window.IsMouseOver, _window.View.MenuOpen, now);
            var presentation = _presentation.Update(snap.State, snap.EncounterId, clock,
                settings.Overlay.ShrinkWhenIdle, settings.General.EndedDisplaySeconds, _window.View.ToastVisible, interacting);
            _window.SetPresentation(presentation == OverlayPresentation.Compact, settings.Overlay.ShrinkWhenIdle);
            SyncTimerWindow(clock);
        }
        catch (Exception ex)
        {
            AppLog.Error("Overlay", "Refresh failed", ex);
        }
        finally
        {
            _refreshing = false;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _window?.Close();
        _window = null;
        _timerWindow?.Close();
        _timerWindow = null;
    }
}
