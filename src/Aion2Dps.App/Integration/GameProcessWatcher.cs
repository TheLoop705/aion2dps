using System.Diagnostics;
using Aion2Dps.Capture;

namespace Aion2Dps.App.Integration;

/// <summary>
/// Watches for the AION 2 client process. Polls on a thread-pool timer (default every 2 s; never on the UI thread, never
/// overlapping) and raises <see cref="GameStarted"/> / <see cref="GameExited"/> through <c>post</c> (the UI dispatcher in
/// the app). <see cref="GameExited"/> waits for <see cref="ExitGrace"/> of continuous absence, so a launcher or Steam
/// restarting the client within the grace raises nothing. The process source is injectable for tests.
/// </summary>
public sealed class GameProcessWatcher : IDisposable
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultExitGrace = TimeSpan.FromSeconds(5);

    private readonly Func<bool> _isGameRunning;
    private readonly Func<DateTime> _clock;
    private readonly Action<Action> _post;
    private readonly object _gate = new();
    private Timer? _timer;
    private bool _reportedRunning;
    private bool _checked;
    private DateTime? _goneSinceUtc;
    private bool _disposed;

    /// <param name="isGameRunning">Process source (default <see cref="IsGameProcessRunning"/>).</param>
    /// <param name="post">Delivers events to the consumer's thread (default: invoke inline on the polling thread).</param>
    public GameProcessWatcher(Func<bool>? isGameRunning = null, Action<Action>? post = null, TimeSpan? pollInterval = null,
        TimeSpan? exitGrace = null, Func<DateTime>? clock = null)
    {
        _isGameRunning = isGameRunning ?? IsGameProcessRunning;
        _post = post ?? (a => a());
        PollInterval = pollInterval ?? DefaultPollInterval;
        ExitGrace = exitGrace ?? DefaultExitGrace;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public TimeSpan PollInterval { get; }
    public TimeSpan ExitGrace { get; }

    /// <summary>The reported state: true from <see cref="GameStarted"/> until <see cref="GameExited"/>.</summary>
    public bool IsGameRunning { get { lock (_gate) return _reportedRunning; } }

    public bool IsStarted { get { lock (_gate) return _timer is not null; } }

    /// <summary>The game was found (also on the first poll when it is already running).</summary>
    public event Action? GameStarted;

    /// <summary>The game has been gone for <see cref="ExitGrace"/>.</summary>
    public event Action? GameExited;

    /// <summary>The first check after <see cref="Start"/> did not find the game (raised once per start).</summary>
    public event Action? GameNotRunning;

    /// <summary>Starts polling; the first poll runs immediately (on the thread pool).</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_timer is not null) return;
            var timer = new Timer(state => OnTimer((Timer)state!));
            _timer = timer;
            timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Stops polling and forgets the state (a later <see cref="Start"/> reports a running game again).</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _reportedRunning = false;
            _goneSinceUtc = null;
            _checked = false;
        }
    }

    private void OnTimer(Timer owner)
    {
        try { Poll(owner); }
        catch (Exception ex) { AppLog.Warn("Game", $"Game process check failed: {ex.Message}"); }
        lock (_gate)
        {
            // One-shot re-arm: a slow poll never overlaps the next one. A stopped (replaced) timer is not re-armed.
            if (ReferenceEquals(_timer, owner))
                try { owner.Change(PollInterval, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>One check (the timer calls this; tests call it directly with a fake clock and source).</summary>
    internal void Poll() => Poll(null);

    private void Poll(Timer? owner)
    {
        bool running;
        try { running = _isGameRunning(); }
        catch (Exception ex)
        {
            AppLog.Warn("Game", $"Process list unavailable: {ex.Message}");
            return; // unknown: keep the current state
        }
        Action? raise = null;
        lock (_gate)
        {
            if (_disposed || (owner is not null && !ReferenceEquals(_timer, owner))) return; // stopped meanwhile
            bool first = !_checked;
            _checked = true;
            if (running)
            {
                _goneSinceUtc = null;
                if (!_reportedRunning)
                {
                    _reportedRunning = true;
                    raise = () => GameStarted?.Invoke();
                }
            }
            else if (first)
            {
                raise = () => GameNotRunning?.Invoke();
            }
            else if (_reportedRunning)
            {
                var now = _clock();
                _goneSinceUtc ??= now;
                if (now - _goneSinceUtc.Value >= ExitGrace)
                {
                    _reportedRunning = false;
                    _goneSinceUtc = null;
                    raise = () => GameExited?.Invoke();
                }
            }
        }
        if (raise is not null) _post(raise);
    }

    /// <summary>Distinct (case-insensitive) client process names: "AION2" and "Aion2" are the same name on Windows.</summary>
    private static readonly string[] ProcessNames =
        GameProcessLocator.ProcessNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>True when an AION 2 client process runs (Process objects are disposed right away).</summary>
    public static bool IsGameProcessRunning()
    {
        foreach (var name in ProcessNames)
        {
            var found = Process.GetProcessesByName(name);
            foreach (var p in found) p.Dispose();
            if (found.Length > 0) return true;
        }
        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
