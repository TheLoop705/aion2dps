namespace Aion2Dps.App.Demo;

/// <summary>Demo <see cref="ICaptureService"/>: pretends to detect and lock a game connection; delivers no bytes.</summary>
public sealed class FakeCaptureService : ICaptureService
{
    private readonly object _gate = new();
    private readonly Timer _timer;
    private DateTime _startedUtc;
    private bool _running;
    private CaptureStatus _status = new() { State = CaptureState.Stopped, Message = "Not started" };

    public FakeCaptureService()
    {
        _timer = new Timer(_ => Pulse(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public CaptureStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public event Action<CaptureStatus>? StatusChanged;

    public string? AdapterOverride { get; set; }

    /// <summary>Seconds spent in "Detecting" before the fake lock (0 = lock immediately).</summary>
    public double DetectSeconds { get; set; } = 1.5;

    public IReadOnlyList<AdapterInfo> GetAdapters() =>
    [
        new("\\Device\\NPF_{3F2A0C11-DEMO-0001}", "Intel(R) Ethernet Controller I225-V", ["192.168.1.42"], false),
        new("\\Device\\NPF_{7B9E4D22-DEMO-0002}", "Wi-Fi 6E AX211 160MHz", ["192.168.1.57"], false),
        new("\\Device\\NPF_{A1C3E5F7-DEMO-0003}", "Gaming VPN Tunnel Adapter", ["10.66.0.12"], false),
        new("\\Device\\NPF_Loopback", "Adapter for loopback traffic capture", ["127.0.0.1"], true),
    ];

    public void Start(IStreamSink sink)
    {
        lock (_gate)
        {
            _running = true;
            _startedUtc = DateTime.UtcNow;
            _status = new CaptureStatus { State = CaptureState.Detecting, Message = "Checking flows for the game heartbeat (demo)" };
        }
        _timer.Change(0, 1000);
        Raise();
    }

    public void Stop()
    {
        lock (_gate)
        {
            _running = false;
            _status = _status with { State = CaptureState.Stopped, Message = "Stopped" };
        }
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        Raise();
    }

    public void StartRecording(string pcapngPath)
    {
        lock (_gate) _status = _status with { RecordingPath = pcapngPath };
        AppLog.Info("Demo", $"(demo) recording would be written to {pcapngPath}");
        Raise();
    }

    public void StopRecording()
    {
        lock (_gate) _status = _status with { RecordingPath = null };
        Raise();
    }

    /// <summary>Sets the status directly (previews/tests).</summary>
    public void SetStatus(CaptureStatus status)
    {
        lock (_gate) _status = status;
        Raise();
    }

    private void Pulse()
    {
        lock (_gate)
        {
            if (!_running) return;
            double t = (DateTime.UtcNow - _startedUtc).TotalSeconds;
            if (t < DetectSeconds) return;
            var adapter = GetAdapters().FirstOrDefault(a => a.Name == AdapterOverride) ?? GetAdapters()[0];
            long packets = (long)(t * 180);
            _status = _status with
            {
                State = CaptureState.Capturing,
                Message = "Locked on game connection (demo data)",
                AdapterName = adapter.Name,
                AdapterDescription = adapter.Description,
                LocalEndpoint = adapter.IPv4Addresses[0] + ":52144",
                ServerEndpoint = "203.0.113.24:7101",
                GameProcessId = 21436,
                PacketsSeen = packets,
                BytesDelivered = packets * 412,
                GapCount = (long)(t / 900),
                LastPacketUtc = DateTime.UtcNow,
            };
        }
        Raise();
    }

    private void Raise()
    {
        try { StatusChanged?.Invoke(Status); }
        catch (Exception ex) { AppLog.Error("Demo", "StatusChanged handler failed", ex); }
    }

    public void Dispose()
    {
        Stop();
        _timer.Dispose();
    }
}

/// <summary>Sink that discards bytes (demo mode has no protocol pipeline).</summary>
public sealed class NullStreamSink : IStreamSink
{
    public void OnData(DateTime timeUtc, ReadOnlySpan<byte> data) { }
    public void OnDiscontinuity(DiscontinuityReason reason) { }
}

/// <summary>Demo <see cref="IProtocolDiagnostics"/> with counters that grow over time and a plausible opcode census.</summary>
public sealed class FakeDiagnostics : IProtocolDiagnostics
{
    private readonly DateTime _start;
    private readonly Func<DateTime> _clock;

    private static readonly (ushort Op, double Rate, int Size, double FailRate)[] Census =
    [
        (Opcodes.Damage, 38, 46, 0.0004), (Opcodes.DotTick, 9, 31, 0), (Opcodes.EntityStats, 21, 28, 0.001), (Opcodes.Heartbeat, 1, 12, 0),
        (Opcodes.Spawn, 1.4, 96, 0.002), (Opcodes.BuffApplied, 6, 38, 0), (Opcodes.BuffRemoved, 5, 14, 0), (Opcodes.Cast, 7, 22, 0),
        (Opcodes.Ping, 0.5, 14, 0), (Opcodes.HpUpdate, 2, 20, 0), (Opcodes.PlayerInfo, 0.2, 140, 0), (Opcodes.Kill, 0.05, 40, 0),
        (Opcodes.Death, 0.03, 10, 0), (Opcodes.PartyRoster, 0.01, 410, 0), (0x1A92, 3.5, 18, 1), (0x0C36, 0.8, 64, 1),
    ];

    public FakeDiagnostics(Func<DateTime>? clock = null, DateTime? startUtc = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
        _start = startUtc ?? _clock();
    }

    private double T => Math.Max(0, (_clock() - _start).TotalSeconds);

    public long BytesIn => (long)(T * 74_000);
    public long Frames => (long)(T * Census.Sum(c => c.Rate));
    public long Bundles => (long)(T * 11);
    public long BundleErrors => 0;
    public long Resyncs => (long)(T / 1800);
    public long DecodeErrors => (long)(T * 0.02);
    public long EventsEmitted => (long)(T * 84);
    public DateTime? LastFrameUtc => _clock();

    public IReadOnlyList<OpcodeStat> GetCensus() => Census
        .Select(c =>
        {
            long count = (long)(T * c.Rate);
            long failed = (long)(count * c.FailRate);
            return new OpcodeStat(c.Op, count, count * c.Size, c.FailRate >= 1 ? 0 : count - failed, c.FailRate >= 1 ? 0 : failed);
        })
        .OrderByDescending(s => s.Count)
        .ToList();

    public IReadOnlyList<string> GetRecentErrors() => DecodeErrors == 0
        ? []
        : [$"{_clock().ToLocalTime():HH:mm:ss} 04 38: effect validator failed (demo)", $"{_clock().ToLocalTime():HH:mm:ss} 00 8D: unexpected stat kind 0x2F (demo)"];
}
