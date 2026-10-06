using System.Diagnostics;
using Aion2Dps.Contracts;

namespace Aion2Dps.Simulator;

/// <summary>Settings of <see cref="SimulatedCaptureService"/>.</summary>
public sealed class SimulationOptions
{
    /// <summary>Playback speed: 1 = real time, 2 = twice as fast. ≤ 0 = as fast as possible (virtual timestamps, no sleeping).</summary>
    public double Speed { get; set; } = 1.0;
    /// <summary>Seed for scenario content and stream chunking (loop iteration n uses Seed + n for chunking).</summary>
    public int Seed { get; set; } = 1;
    /// <summary>Scenarios to play in order; null or empty = <see cref="ScenarioLibrary.DefaultCycle"/> (BossKill, TrashPull, BossWipeThenKill).</summary>
    public IReadOnlyList<Scenario>? Scenarios { get; set; }
    /// <summary>Start over after the last scenario.</summary>
    public bool Loop { get; set; } = true;
    /// <summary>Heartbeat-only pause between scenarios (scenario time), so idle timeouts can fire.</summary>
    public double GapSeconds { get; set; } = 12;
    /// <summary>Stream generator settings (its Seed is overridden per iteration).</summary>
    public StreamGeneratorOptions? Stream { get; set; }
}

/// <summary>
/// Demo-mode <see cref="ICaptureService"/>: a background thread plays scenarios in real time (or faster) into the
/// <see cref="IStreamSink"/>, with <see cref="DateTime.UtcNow"/>-based timestamps. Status: Capturing, adapter
/// "Simulated", server "simulated:13328". Recording is not supported (no-op).
/// </summary>
public sealed class SimulatedCaptureService : ICaptureService
{
    public const string AdapterName = "Simulated";
    public const string ServerEndpoint = "simulated:13328";
    public const string LocalEndpoint = "127.0.0.1:62311";

    private readonly SimulationOptions _options;
    private readonly object _gate = new();
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private CaptureStatus _status = new() { State = CaptureState.Stopped, Message = "Simulation stopped" };
    private long _packets;
    private long _bytes;
    private DateTime? _lastPacket;
    private volatile Scenario? _current;

    public SimulatedCaptureService(SimulationOptions? options = null) => _options = options ?? new SimulationOptions();

    public CaptureStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public event Action<CaptureStatus>? StatusChanged;

    /// <summary>Raised on the playback thread when a scenario starts.</summary>
    public event Action<Scenario>? ScenarioStarted;

    /// <summary>The scenario being played, or null.</summary>
    public Scenario? CurrentScenario => _current;

    public SimulationOptions Options => _options;

    public string? AdapterOverride { get; set; }

    public IReadOnlyList<AdapterInfo> GetAdapters() =>
        [new AdapterInfo(AdapterName, "Simulated AION 2 traffic (demo mode)", ["127.0.0.1"], true)];

    public void Start(IStreamSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_gate)
        {
            if (_thread is { IsAlive: true }) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _thread = new Thread(() => Run(sink, token)) { IsBackground = true, Name = "Aion2Dps.Simulator" };
            _packets = 0;
            _bytes = 0;
            _lastPacket = null;
            SetStatus(CaptureState.Capturing, "Simulating game traffic");
            _thread.Start();
        }

        RaiseStatus();
    }

    public void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
            if (thread is null) return;
            _cts?.Cancel();
        }

        if (thread != Thread.CurrentThread) thread.Join(TimeSpan.FromSeconds(5));
        lock (_gate)
        {
            _thread = null;
            _cts?.Dispose();
            _cts = null;
            _current = null;
            SetStatus(CaptureState.Stopped, "Simulation stopped");
        }

        RaiseStatus();
    }

    public void StartRecording(string pcapngPath) => AppLog.Info("Simulator", "Recording is not supported in demo mode.");

    public void StopRecording() { }

    public void Dispose() => Stop();

    private void SetStatus(CaptureState state, string message)
    {
        _status = new CaptureStatus
        {
            State = state,
            Message = message,
            AdapterName = AdapterName,
            AdapterDescription = "Simulated AION 2 traffic (demo mode)",
            LocalEndpoint = state == CaptureState.Capturing ? LocalEndpoint : null,
            ServerEndpoint = state == CaptureState.Capturing ? ServerEndpoint : null,
            PacketsSeen = Interlocked.Read(ref _packets),
            BytesDelivered = Interlocked.Read(ref _bytes),
            LastPacketUtc = _lastPacket,
        };
    }

    private void RaiseStatus()
    {
        var status = Status;
        try { StatusChanged?.Invoke(status); }
        catch (Exception ex) { AppLog.Error("Simulator", "StatusChanged handler failed", ex); }
    }

    private void Run(IStreamSink sink, CancellationToken token)
    {
        try
        {
            var scenarios = _options.Scenarios is { Count: > 0 } list ? list : ScenarioLibrary.DefaultCycle(_options.Seed);
            double speed = _options.Speed;
            var clock = Stopwatch.StartNew();
            var lastStatus = TimeSpan.Zero;
            DateTime virtualNow = DateTime.UtcNow;
            int iteration = 0;
            SafeSink(() => sink.OnDiscontinuity(DiscontinuityReason.NewConnection));

            do
            {
                foreach (var scenario in scenarios)
                {
                    if (token.IsCancellationRequested) return;
                    _current = scenario;
                    lock (_gate) SetStatus(CaptureState.Capturing, $"Simulating: {scenario.Name}");
                    RaiseStatus();
                    try { ScenarioStarted?.Invoke(scenario); }
                    catch (Exception ex) { AppLog.Error("Simulator", "ScenarioStarted handler failed", ex); }

                    var genOptions = (_options.Stream ?? new StreamGeneratorOptions()) with { Seed = _options.Seed + iteration };
                    var generator = new StreamGenerator(genOptions);
                    // Map scenario time onto wall time: real start + offset / speed.
                    DateTime realStart = speed > 0 ? DateTime.UtcNow : virtualNow;
                    var stream = generator.Generate(scenario, realStart);
                    var idle = generator.GenerateIdle(realStart + scenario.Duration, TimeSpan.FromSeconds(_options.GapSeconds));
                    var wallStart = clock.Elapsed;
                    foreach (var chunk in stream.Chunks.Concat(idle))
                    {
                        if (token.IsCancellationRequested) return;
                        var offset = chunk.TimeUtc - realStart;
                        var scaled = speed > 0 ? TimeSpan.FromTicks((long)(offset.Ticks / speed)) : offset;
                        if (speed > 0)
                        {
                            while (true)
                            {
                                var wait = wallStart + scaled - clock.Elapsed;
                                if (wait <= TimeSpan.Zero) break;
                                if (token.WaitHandle.WaitOne(wait < TimeSpan.FromMilliseconds(250) ? wait : TimeSpan.FromMilliseconds(250))) return;
                                MaybeStatus(clock, ref lastStatus);
                            }
                        }

                        var time = realStart + scaled;
                        SafeSink(() => sink.OnData(time, chunk.Data));
                        Interlocked.Increment(ref _packets);
                        Interlocked.Add(ref _bytes, chunk.Data.Length);
                        lock (_gate) _lastPacket = time;
                        MaybeStatus(clock, ref lastStatus);
                        virtualNow = time;
                    }

                    if (speed <= 0) virtualNow += TimeSpan.FromMilliseconds(50);
                }

                iteration++;
            }
            while (_options.Loop && !token.IsCancellationRequested);

            lock (_gate)
            {
                _current = null;
                SetStatus(CaptureState.Stopped, "Simulation finished");
            }

            RaiseStatus();
        }
        catch (Exception ex)
        {
            AppLog.Error("Simulator", "Simulation thread failed", ex);
            lock (_gate) SetStatus(CaptureState.Error, "Simulation failed: " + ex.Message);
            RaiseStatus();
        }
    }

    private void MaybeStatus(Stopwatch clock, ref TimeSpan lastStatus)
    {
        if (clock.Elapsed - lastStatus < TimeSpan.FromSeconds(1)) return;
        lastStatus = clock.Elapsed;
        lock (_gate) SetStatus(_status.State, _status.Message);
        RaiseStatus();
    }

    private static void SafeSink(Action action)
    {
        try { action(); }
        catch (Exception ex) { AppLog.Error("Simulator", "Stream sink threw", ex); }
    }
}
