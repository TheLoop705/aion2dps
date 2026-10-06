using System.Diagnostics;
using Aion2Dps.Capture;

namespace Aion2Dps.App.Integration;

/// <summary>
/// <see cref="ICaptureService"/> facade over <see cref="IReplaySource"/> for <c>--replay &lt;file&gt;</c>: Start runs the
/// replay in the background into the pipeline and <see cref="Now"/> is the replay clock (capture time, advancing in real
/// time after the file ends so idle timeouts can close the last encounter). Every game flow of the file is replayed
/// (interleaved by timestamp) when the sink is flow-aware (<see cref="IStreamSinkFactory"/>, e.g. the multi-flow pipeline).
/// </summary>
public sealed class ReplayCaptureService : ICaptureService
{
    private readonly string _path;
    private readonly double _speed;
    private readonly IReplaySource _source;
    private readonly object _gate = new();
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private CancellationTokenSource? _cts;
    private Task? _task;
    private CaptureStatus _status;
    private DateTime? _lastCapture;
    private TimeSpan _lastWall;
    private bool _finished;
    private long _packets;
    private TimeSpan _lastStatus;

    public ReplayCaptureService(string path, double speed, IReplaySource? source = null)
    {
        _path = path;
        _speed = Math.Max(0, speed);
        _source = source ?? new PcapReplaySource();
        _status = new CaptureStatus { State = CaptureState.Stopped, Message = $"Replay of {Path.GetFileName(path)} not started", AdapterName = "Replay", AdapterDescription = path };
    }

    public CaptureStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public event Action<CaptureStatus>? StatusChanged;

    public string? AdapterOverride { get; set; }

    /// <summary>True once the file has been fully delivered (or failed).</summary>
    public bool Finished
    {
        get { lock (_gate) return _finished; }
    }

    /// <summary>The replay task (null before Start).</summary>
    public Task? Completion => _task;

    public IReadOnlyList<AdapterInfo> GetAdapters() => [new AdapterInfo("Replay", "Replay: " + Path.GetFileName(_path), [], false)];

    /// <summary>Replay clock: the capture time of the latest packet, extrapolated with wall time.</summary>
    public DateTime Now()
    {
        lock (_gate)
        {
            if (_lastCapture is not { } last) return DateTime.UtcNow;
            var sinceWall = _wall.Elapsed - _lastWall;
            if (_finished) return last + sinceWall;
            if (_speed <= 0) return last;
            var capped = sinceWall < TimeSpan.FromSeconds(1) ? sinceWall : TimeSpan.FromSeconds(1);
            return last + TimeSpan.FromTicks((long)(capped.Ticks * _speed));
        }
    }

    public void Start(IStreamSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_gate)
        {
            if (_task is { IsCompleted: false }) return;
            _cts = new CancellationTokenSource();
            _finished = false;
            _packets = 0;
            _status = _status with { State = CaptureState.Replaying, Message = $"Replaying {Path.GetFileName(_path)}" + (_speed > 0 ? $" at {_speed:0.##}×" : " (max speed)") };
        }

        Raise();
        if (!File.Exists(_path))
        {
            Finish(CaptureState.Error, $"Replay file not found: {_path}");
            return;
        }

        var token = _cts.Token;
        _task = _source.ReplayAsync(_path, sink, _speed, OnClock, token).ContinueWith(t =>
        {
            if (t.IsCanceled) Finish(CaptureState.Stopped, "Replay stopped");
            else if (t.IsFaulted) Finish(CaptureState.Error, "Replay failed: " + t.Exception?.GetBaseException().Message);
            else
            {
                var stats = (_source as PcapReplaySource)?.LastStatistics;
                if (stats?.Servers is { } servers)
                {
                    lock (_gate) _status = _status with { ServerEndpoint = servers, LocalEndpoint = stats.LastClient, BytesDelivered = stats.BytesDelivered, GapCount = stats.Gaps };
                }

                Finish(CaptureState.Stopped, stats is null
                    ? "Replay finished"
                    : stats.BytesDelivered == 0
                        ? $"Replay finished: no AION 2 game traffic found in {Path.GetFileName(_path)}"
                        : $"Replay finished: {stats.Packets:N0} packets, {stats.Flows} game flow(s) (max {stats.MaxConcurrentFlows} at once), {stats.Gaps} gap(s)");
            }
        }, TaskScheduler.Default);
    }

    private void OnClock(DateTime captureUtc)
    {
        bool raise = false;
        lock (_gate)
        {
            if (_lastCapture is null || captureUtc >= _lastCapture) _lastCapture = captureUtc;
            _lastWall = _wall.Elapsed;
            _packets++;
            if (_wall.Elapsed - _lastStatus >= TimeSpan.FromSeconds(1))
            {
                _lastStatus = _wall.Elapsed;
                _status = _status with { PacketsSeen = _packets, LastPacketUtc = captureUtc };
                raise = true;
            }
        }

        if (raise) Raise();
    }

    private void Finish(CaptureState state, string message)
    {
        lock (_gate)
        {
            _finished = true;
            _lastWall = _wall.Elapsed;
            _status = _status with { State = state, Message = message, PacketsSeen = _packets };
        }

        AppLog.Info("Replay", message);
        Raise();
    }

    private void Raise()
    {
        var s = Status;
        try { StatusChanged?.Invoke(s); }
        catch (Exception ex) { AppLog.Error("Replay", "StatusChanged handler failed", ex); }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate) cts = _cts;
        cts?.Cancel();
        try { _task?.Wait(TimeSpan.FromSeconds(3)); } catch { /* cancellation */ }
    }

    public void StartRecording(string pcapngPath) => AppLog.Info("Replay", "Recording is not available while replaying a file.");

    public void StopRecording() { }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
