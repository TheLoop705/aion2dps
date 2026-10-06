using Aion2Dps.Contracts;

namespace Aion2Dps.Capture;

/// <summary>Settings of <see cref="NpcapCaptureService"/>.</summary>
public sealed class CaptureServiceOptions
{
    /// <summary>Npcap check (default <see cref="NpcapAvailability.Check"/>). Re-run every <see cref="NpcapRecheckInterval"/> while missing.</summary>
    public Func<NpcapCheckResult>? NpcapProbe { get; set; }
    /// <summary>Game process/connection lookup (default <see cref="GameProcessLocator"/>).</summary>
    public IGameConnectionLocator? Locator { get; set; }
    public FlowTrackerOptions Tracker { get; set; } = new();
    /// <summary>No packet of the locked flow for this long → back to detection.</summary>
    public TimeSpan WatchdogTimeout { get; set; } = TimeSpan.FromSeconds(90);
    public TimeSpan ProcessPollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan StatusInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan NpcapRecheckInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan AdapterRefreshInterval { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>Delay before retrying an adapter that failed to open.</summary>
    public TimeSpan OpenRetryInterval { get; set; } = TimeSpan.FromSeconds(5);
    public int ReadTimeoutMs { get; set; } = 100;
    public int SnapLength { get; set; } = 65535;
    public int KernelBufferBytes { get; set; } = 16 * 1024 * 1024;
    /// <summary>While the game process is not running, still watch <c>tcp port 13328</c> on all adapters (covers a renamed
    /// executable). False = capture nothing until the process appears.</summary>
    public bool ScanWithoutProcess { get; set; } = true;
}

/// <summary>
/// Live capture of the AION 2 game connection through Npcap (PROTOCOL.md §2). A background supervisor thread polls the
/// game process connection table, selects the adapter, opens it non-promiscuously (snaplen 65535, 100 ms read timeout)
/// with a BPF filter for the game server, locks the flow (process hint or heartbeat signature), reassembles the
/// server→client direction and delivers it to the <see cref="IStreamSink"/> with pcap timestamps.
/// <para>Sink calls are serialised (one at a time, in stream order) and made from the adapter reader thread.
/// <see cref="DiscontinuityReason.CaptureRestarted"/> is emitted at every <see cref="Start"/>,
/// <see cref="DiscontinuityReason.NewConnection"/> when a different flow is locked later, and
/// <see cref="DiscontinuityReason.TcpGap"/> after a reassembly gap.</para>
/// </summary>
public sealed class NpcapCaptureService : ICaptureService
{
    private readonly CaptureServiceOptions _options;
    private readonly Func<NpcapCheckResult> _probe;
    private readonly IGameConnectionLocator _locator;
    private readonly object _lifecycle = new();
    private readonly object _sync = new(); // serialises tracker access (reader threads, supervisor, recording)
    private readonly Dictionary<string, DeviceReader> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _sourceIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, AdapterInfo> _sourceAdapters = new();
    private readonly Dictionary<string, DateTime> _openRetryAfter = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _openErrors = new(StringComparer.OrdinalIgnoreCase);

    private Thread? _thread;
    private ManualResetEventSlim _stopEvent = new(false);
    private StreamSinkGuard? _sink;
    private GameFlowTracker? _tracker;
    private PcapFileWriter? _recorder;
    private string? _recordingPath;
    private volatile string? _adapterOverride;
    private volatile CaptureStatus _status = new() { State = CaptureState.Stopped, Message = "Capture is stopped." };
    private CaptureStatus? _lastPublished;
    private DateTime _lastPublishUtc;
    private bool _disposed;

    public NpcapCaptureService() : this(new CaptureServiceOptions())
    {
    }

    public NpcapCaptureService(CaptureServiceOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _probe = options.NpcapProbe ?? NpcapAvailability.Check;
        _locator = options.Locator ?? new GameProcessLocator();
    }

    public CaptureStatus Status => _status;

    public event Action<CaptureStatus>? StatusChanged;

    public string? AdapterOverride
    {
        get => _adapterOverride;
        set => _adapterOverride = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public IReadOnlyList<AdapterInfo> GetAdapters() => AdapterSelector.ListAdapters();

    public bool IsRunning
    {
        get { lock (_lifecycle) return _thread is not null; }
    }

    public void Start(IStreamSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null) return; // idempotent
            _stopEvent = new ManualResetEventSlim(false);
            _sink = new StreamSinkGuard(sink);
            var tracker = new GameFlowTracker(_sink, _options.Tracker);
            tracker.Locked += info => AppLog.Info("Capture", $"Capturing game flow {info}");
            lock (_sync)
            {
                tracker.Recorder = _recorder;
                _tracker = tracker;
            }
            _lastPublished = null;
            _thread = new Thread(Run) { IsBackground = true, Name = "Aion2Dps capture supervisor" };
            _thread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_lifecycle)
        {
            thread = _thread;
            if (thread is null) return;
            _stopEvent.Set();
        }
        if (Thread.CurrentThread != thread) thread.Join(TimeSpan.FromSeconds(10));
        lock (_lifecycle)
        {
            _thread = null;
        }
        StopRecording();
        Publish(new CaptureStatus { State = CaptureState.Stopped, Message = "Capture is stopped." }, force: true);
    }

    public void StartRecording(string pcapngPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(pcapngPath);
        var writer = PcapFileWriter.Create(pcapngPath, PcapFileWriter.FormatForPath(pcapngPath));
        PcapFileWriter? old;
        lock (_sync)
        {
            old = _recorder;
            _recorder = writer;
            _recordingPath = Path.GetFullPath(pcapngPath);
            if (_tracker is not null) _tracker.Recorder = writer;
        }
        old?.Dispose();
        AppLog.Info("Capture", $"Recording the game flow to {_recordingPath} ({writer.Format})");
        Publish(_status with { RecordingPath = _recordingPath }, force: true);
    }

    public void StopRecording()
    {
        PcapFileWriter? old;
        lock (_sync)
        {
            old = _recorder;
            _recorder = null;
            _recordingPath = null;
            if (_tracker is not null) _tracker.Recorder = null;
        }
        if (old is null) return;
        try
        {
            old.Dispose();
            AppLog.Info("Capture", $"Recording stopped ({old.PacketsWritten} packets)");
        }
        catch (Exception ex)
        {
            AppLog.Error("Capture", "Closing the recording failed", ex);
        }
        Publish(_status with { RecordingPath = null }, force: true);
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try { Stop(); } catch (Exception ex) { AppLog.Error("Capture", "Stop during dispose failed", ex); }
        StopRecording();
    }

    // ───────────────────────────── supervisor ─────────────────────────────

    private sealed class LoopState
    {
        public NpcapCheckResult? Npcap;
        public DateTime NextNpcapCheck = DateTime.MinValue;
        public DateTime NextPoll = DateTime.MinValue;
        public GameLocatorResult Located = GameLocatorResult.Empty;
        public IReadOnlyList<AdapterInfo> Adapters = Array.Empty<AdapterInfo>();
        public DateTime AdaptersAt = DateTime.MinValue;
        public string? LastOverride;
        public int? LockPid;
        public FlowLockInfo? LastLock;
    }

    private void Run()
    {
        var st = new LoopState();
        var stop = _stopEvent;
        var tracker = _tracker!;
        lock (_sync) _sink!.OnDiscontinuity(DiscontinuityReason.CaptureRestarted);
        Publish(new CaptureStatus { State = CaptureState.WaitingForGame, Message = "Starting capture, looking for AION 2..." }, force: true);
        try
        {
            while (!stop.IsSet)
            {
                try
                {
                    Step(st, tracker);
                }
                catch (Exception ex)
                {
                    AppLog.Error("Capture", "Capture supervisor step failed", ex);
                    CloseAllReaders();
                    Publish(_status with { State = CaptureState.Error, Message = $"Capture error: {ex.Message}" }, force: false);
                    stop.Wait(TimeSpan.FromSeconds(2));
                }
                stop.Wait(250);
            }
        }
        finally
        {
            CloseAllReaders();
        }
    }

    private void Step(LoopState st, GameFlowTracker tracker)
    {
        var now = DateTime.UtcNow;

        // 1. Npcap present?
        if (st.Npcap is not { IsAvailable: true })
        {
            if (now >= st.NextNpcapCheck)
            {
                st.Npcap = SafeProbe();
                st.NextNpcapCheck = now + _options.NpcapRecheckInterval;
                if (st.Npcap.IsAvailable) AppLog.Info("Capture", st.Npcap.Reason);
            }
            if (st.Npcap is not { IsAvailable: true })
            {
                CloseAllReaders();
                Publish(new CaptureStatus
                {
                    State = CaptureState.NpcapMissing,
                    Message = st.Npcap?.Reason ?? "Npcap is not installed.",
                    RecordingPath = _recordingPath,
                }, force: false);
                return;
            }
        }

        // 2. Game process and connections; adapters.
        string? ovr = _adapterOverride;
        bool overrideChanged = !string.Equals(ovr, st.LastOverride, StringComparison.OrdinalIgnoreCase);
        bool polled = false;
        if (now >= st.NextPoll || overrideChanged)
        {
            st.Located = _locator.Locate();
            st.NextPoll = now + _options.ProcessPollInterval;
            polled = true;
        }
        if (st.Adapters.Count == 0 || now - st.AdaptersAt > _options.AdapterRefreshInterval || overrideChanged)
        {
            st.Adapters = AdapterSelector.ListAdapters();
            st.AdaptersAt = now;
        }
        st.LastOverride = ovr;
        var located = st.Located;
        var hintConnections = located.Candidates.Where(c => c.IsGamePort).ToList();
        if (hintConnections.Count == 0) hintConnections = located.Candidates.ToList();

        // 3. Watchdog + hints.
        FlowLockInfo? lockInfo;
        lock (_sync)
        {
            tracker.SetHints(hintConnections.Select(c => c.Hint));
            lockInfo = tracker.CurrentLock;
            if (lockInfo is not null)
            {
                if (!ReferenceEquals(lockInfo, st.LastLock))
                {
                    st.LastLock = lockInfo;
                    st.LockPid = located.Candidates.FirstOrDefault(c => c.Hint.Key == lockInfo.Key)?.ProcessId;
                }
                string? reason = null;
                if (tracker.LastLockedPacketUtc is { } last && now - last > _options.WatchdogTimeout)
                    reason = $"no game data for {(int)_options.WatchdogTimeout.TotalSeconds} s";
                else if (polled && st.LockPid is int pid && !_locator.IsProcessAlive(pid))
                    reason = "the game process exited";
                else if (ovr is not null && _sourceAdapters.TryGetValue(lockInfo.SourceId, out var la) && !AdapterSelector.MatchesOverride(la, ovr))
                    reason = "the capture adapter was changed";
                if (reason is not null)
                {
                    tracker.Unlock(reason);
                    lockInfo = null;
                    st.LastLock = null;
                    st.LockPid = null;
                }
            }
            tracker.Tick(now);
        }

        // 4. What should be open?
        IReadOnlyList<AdapterInfo> want;
        string filter;
        CaptureState state;
        string message;
        AdapterInfo? statusAdapter = null;
        string? local = null, server = null;
        int? pidForStatus = located.Processes.Count > 0 ? located.Processes[0].ProcessId : null;

        if (lockInfo is not null)
        {
            _sourceAdapters.TryGetValue(lockInfo.SourceId, out statusAdapter);
            want = statusAdapter is not null ? new[] { statusAdapter } : _readers.Values.Select(r => r.Adapter).ToList();
            filter = CaptureFilters.ForServers(new[] { lockInfo.Server }.Concat(hintConnections.Select(c => c.Remote)), true,
                _options.Tracker.GameServerPort);
            state = CaptureState.Capturing;
            local = lockInfo.Client.ToString();
            server = lockInfo.Server.ToString();
            pidForStatus = st.LockPid ?? pidForStatus;
            message = $"Capturing game traffic {server} -> {local} on {statusAdapter?.Description ?? "adapter"}.";
        }
        else if (located.Best is { } best)
        {
            var sel = AdapterSelector.Select(st.Adapters, best.Local.IPAddress, ovr);
            local = best.Local.ToString();
            server = best.Remote.ToString();
            pidForStatus = best.ProcessId;
            filter = CaptureFilters.ForServers(hintConnections.Select(c => c.Remote), true, _options.Tracker.GameServerPort);
            if (sel.Adapter is not null)
            {
                want = new[] { sel.Adapter };
                statusAdapter = sel.Adapter;
                state = CaptureState.Detecting;
                message = $"AION 2 (pid {best.ProcessId}) is connected to {server} from {local}. " +
                          $"Waiting for game traffic on {sel.Adapter.Description}...";
            }
            else if (ovr is not null)
            {
                want = Array.Empty<AdapterInfo>();
                state = CaptureState.Error;
                message = $"The selected capture adapter '{ovr}' was not found. Choose another adapter or switch to automatic.";
            }
            else
            {
                want = AdapterSelector.SelectScanSet(st.Adapters, null, includeLoopback: true);
                state = CaptureState.Detecting;
                message = $"AION 2 is connected to {server} from {local}, but no capture adapter has the address " +
                          $"{best.Local.AddressString}. Watching {want.Count} adapters...";
            }
        }
        else if (located.ProcessFound)
        {
            want = AdapterSelector.SelectScanSet(st.Adapters, ovr, includeLoopback: true);
            filter = CaptureFilters.AllTcp;
            state = want.Count > 0 ? CaptureState.Detecting : CaptureState.Error;
            message = want.Count > 0
                ? $"AION 2 is running (pid {located.Processes[0].ProcessId}) but its game connection was not found yet. " +
                  $"Looking for the game heartbeat on {DescribeSet(want)}..."
                : ovr is not null ? $"The selected capture adapter '{ovr}' was not found." : "No usable capture adapter was found.";
        }
        else
        {
            want = _options.ScanWithoutProcess ? AdapterSelector.SelectScanSet(st.Adapters, ovr, includeLoopback: false) : Array.Empty<AdapterInfo>();
            filter = CaptureFilters.GamePort(_options.Tracker.GameServerPort);
            state = CaptureState.WaitingForGame;
            message = want.Count > 0
                ? $"Waiting for AION 2. Start the game; watching {DescribeSet(want)} for its connection."
                : "Waiting for AION 2. Start the game.";
        }

        // 5. Open/close adapters.
        ApplyReaders(want, filter, now);
        var failures = want.Where(a => !_readers.ContainsKey(a.Name) && _openErrors.ContainsKey(a.Name)).ToList();
        if (want.Count > 0 && failures.Count == want.Count)
        {
            var a = failures[0];
            state = CaptureState.Error;
            message = $"Cannot open capture adapter {a.Description}: {_openErrors[a.Name]}. If Npcap was installed with " +
                      "\"Restrict Npcap driver's access to Administrators only\", run the meter as administrator or reinstall Npcap " +
                      "without that option.";
        }
        if (statusAdapter is null && _readers.Count == 1) statusAdapter = _readers.Values.First().Adapter;

        // 6. Status.
        CaptureStatus status;
        lock (_sync)
        {
            status = new CaptureStatus
            {
                State = state,
                Message = message,
                AdapterName = statusAdapter?.Name,
                AdapterDescription = statusAdapter?.Description,
                LocalEndpoint = local,
                ServerEndpoint = server,
                GameProcessId = pidForStatus,
                PacketsSeen = tracker.PacketsSeen,
                BytesDelivered = tracker.BytesDelivered,
                GapCount = tracker.GapCount,
                LastPacketUtc = tracker.LastLockedPacketUtc,
                RecordingPath = _recordingPath,
            };
        }
        Publish(status, force: false);
    }

    private static string DescribeSet(IReadOnlyList<AdapterInfo> adapters) =>
        adapters.Count == 1 ? adapters[0].Description : $"{adapters.Count} adapters";

    private NpcapCheckResult SafeProbe()
    {
        try
        {
            return _probe();
        }
        catch (Exception ex)
        {
            return new NpcapCheckResult(false, $"Npcap check failed: {ex.Message}", null, 0);
        }
    }

    private void ApplyReaders(IReadOnlyList<AdapterInfo> want, string filter, DateTime now)
    {
        var names = new HashSet<string>(want.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var name in _readers.Keys.ToList())
        {
            var r = _readers[name];
            if (!names.Contains(name) || !r.IsRunning)
            {
                if (!r.IsRunning && r.Error is not null)
                {
                    _openErrors[name] = r.Error;
                    _openRetryAfter[name] = now + _options.OpenRetryInterval;
                }
                _readers.Remove(name);
                r.Dispose();
            }
        }

        foreach (var adapter in want)
        {
            if (_readers.TryGetValue(adapter.Name, out var existing))
            {
                existing.SetFilter(filter);
                continue;
            }
            if (_openRetryAfter.TryGetValue(adapter.Name, out var retry) && now < retry) continue;
            if (!_sourceIds.TryGetValue(adapter.Name, out int id))
            {
                id = _sourceIds.Count + 1;
                _sourceIds[adapter.Name] = id;
            }
            _sourceAdapters[id] = adapter;
            try
            {
                _readers[adapter.Name] = DeviceReader.Open(adapter, id, filter, _options, OnFrame);
                _openErrors.Remove(adapter.Name);
                _openRetryAfter.Remove(adapter.Name);
            }
            catch (Exception ex)
            {
                string msg = ex is TypeInitializationException { InnerException: { } inner } ? inner.Message : ex.Message;
                _openErrors[adapter.Name] = msg;
                _openRetryAfter[adapter.Name] = now + _options.OpenRetryInterval;
                AppLog.Warn("Capture", $"Cannot open {adapter.Description}: {msg}");
            }
        }
    }

    private void OnFrame(int sourceId, DateTime timestampUtc, int linkType, ReadOnlySpan<byte> frame)
    {
        lock (_sync)
        {
            var tracker = _tracker;
            if (tracker is null) return;
            try
            {
                tracker.OnPacket(timestampUtc, linkType, frame, sourceId);
            }
            catch (Exception ex)
            {
                AppLog.Error("Capture", "Packet processing failed", ex);
            }
        }
    }

    private void CloseAllReaders()
    {
        foreach (var r in _readers.Values) r.Dispose();
        _readers.Clear();
    }

    private void Publish(CaptureStatus status, bool force)
    {
        var now = DateTime.UtcNow;
        CaptureStatus? last;
        lock (_lifecycle) last = _lastPublished;
        bool keyChanged = last is null || last.State != status.State || last.Message != status.Message ||
                          last.AdapterName != status.AdapterName || last.ServerEndpoint != status.ServerEndpoint ||
                          last.LocalEndpoint != status.LocalEndpoint || last.GameProcessId != status.GameProcessId ||
                          last.RecordingPath != status.RecordingPath;
        bool countersChanged = last is not null && (last.PacketsSeen != status.PacketsSeen ||
                                                    last.BytesDelivered != status.BytesDelivered ||
                                                    last.GapCount != status.GapCount);
        _status = status;
        if (!force && !keyChanged && !(countersChanged && now - _lastPublishUtc >= _options.StatusInterval)) return;
        lock (_lifecycle)
        {
            _lastPublished = status;
            _lastPublishUtc = now;
        }
        try
        {
            StatusChanged?.Invoke(status);
        }
        catch (Exception ex)
        {
            AppLog.Error("Capture", "StatusChanged handler threw", ex);
        }
    }
}
