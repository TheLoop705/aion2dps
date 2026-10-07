using System.Collections.Concurrent;
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
    /// <summary>A game flow without packets for this long ends and its sink is released (copied into
    /// <see cref="FlowTrackerOptions.FlowIdleTimeout"/> at <see cref="NpcapCaptureService.Start"/>).</summary>
    public TimeSpan WatchdogTimeout { get; set; } = TimeSpan.FromSeconds(90);
    /// <summary>How often the game process's connection table (<c>GetExtendedTcpTable</c>) is polled for game flows.</summary>
    public TimeSpan ProcessPollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan StatusInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan NpcapRecheckInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan AdapterRefreshInterval { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>Delay before retrying an adapter that failed to open; doubled after every further failure up to
    /// <see cref="MaxOpenRetryInterval"/>.</summary>
    public TimeSpan OpenRetryInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxOpenRetryInterval { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>After a filter change the old handle keeps reading this long next to the new one, so the packets it had
    /// buffered are not lost (the overlap is delivered twice; the reassembler drops the duplicates).</summary>
    public TimeSpan FilterSwapOverlap { get; set; } = TimeSpan.FromMilliseconds(600);
    /// <summary>A game flow whose connection has left the game process's connection table and that has had no packet for
    /// this long ends at once (instead of after <see cref="WatchdogTimeout"/>).</summary>
    public TimeSpan ClosedConnectionIdle { get; set; } = TimeSpan.FromSeconds(10);
    public int ReadTimeoutMs { get; set; } = 100;
    public int SnapLength { get; set; } = 65535;
    public int KernelBufferBytes { get; set; } = 16 * 1024 * 1024;
    /// <summary>While the game process is not running, still watch <c>tcp port 13328</c> on all adapters (covers a renamed
    /// executable). False = capture nothing until the process appears.</summary>
    public bool ScanWithoutProcess { get; set; } = true;
    /// <summary>Packets waiting for the dispatch thread beyond this many bytes are dropped (and counted) instead of
    /// growing memory without bound.</summary>
    public long MaxQueuedBytes { get; set; } = 128L * 1024 * 1024;
    /// <summary>Bounded public shutdown wait; the supervisor continues draining after a timeout.</summary>
    internal TimeSpan StopTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Live capture of the AION 2 game connections through Npcap (PROTOCOL.md §2, LIVE-FINDINGS NEW 1). A background
/// supervisor thread polls the game process connection table (~every 2 s, every ESTABLISHED connection to port 13328 is a
/// hint), selects the adapters, opens them non-promiscuously (snaplen 65535, 100 ms read timeout) with a BPF filter for
/// <c>tcp port 13328</c> plus the hinted hosts, and every game flow is locked independently (process hint or heartbeat
/// signature), reassembled and delivered to its own sink.
/// <para><b>Ordering:</b> the adapter reader threads only copy packets into one queue; a single dispatch thread feeds
/// the <see cref="GameFlowTracker"/>, so every sink call happens on that one thread, in arrival (capture) order, also
/// with several adapters. With an <see cref="IStreamSinkFactory"/> sink every flow gets its own sink
/// (<see cref="DiscontinuityReason.TcpGap"/>/<see cref="DiscontinuityReason.NewConnection"/> per flow);
/// <see cref="DiscontinuityReason.CaptureRestarted"/> is emitted to the sink itself at every <see cref="Start"/>.
/// A plain <see cref="IStreamSink"/> gets one flow at a time (<see cref="SingleSinkFlowAdapter"/>).</para>
/// </summary>
public sealed class NpcapCaptureService : ICaptureService, ICaptureFlowStatus
{
    private readonly CaptureServiceOptions _options;
    private readonly Func<NpcapCheckResult> _probe;
    private readonly IGameConnectionLocator _locator;
    private readonly object _lifecycle = new();
    private readonly object _sync = new(); // tracker state: dispatch thread (writes, sink calls), supervisor (reads, hints)
    private readonly Dictionary<string, DeviceReader> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _sourceIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, AdapterInfo> _sourceAdapters = new();
    private readonly Dictionary<string, DateTime> _openRetryAfter = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _openErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _openFailures = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Handles replaced by a handle with a new filter: still reading until their deadline (no lost packets).</summary>
    private readonly List<(DeviceReader Reader, DateTime DisposeAt)> _retiring = new();

    private Thread? _thread;
    private ManualResetEventSlim _stopEvent = new(false);
    private CaptureDispatcher? _dispatcher;
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

    /// <summary>Game flows currently open.</summary>
    public int OpenFlowCount
    {
        get { lock (_sync) return _tracker?.ActiveFlowCount ?? 0; }
    }

    /// <summary>Open game flows and recently ended ones.</summary>
    public IReadOnlyList<CaptureFlowInfo> GetFlows()
    {
        lock (_sync) return _tracker?.GetFlows() ?? Array.Empty<CaptureFlowInfo>();
    }

    /// <summary>Packets dropped because the dispatch queue was full.</summary>
    public long DroppedPackets => _dispatcher?.Dropped ?? 0;

    public void Start(IStreamSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null) return; // idempotent
            _stopEvent = new ManualResetEventSlim(false);
            _options.Tracker.FlowIdleTimeout = _options.WatchdogTimeout;
            var tracker = new GameFlowTracker(sink, _options.Tracker);
            tracker.Locked += info => AppLog.Info("Capture", $"Capturing game flow #{info.LockNumber} {info}");
            lock (_sync)
            {
                tracker.Recorder = _recorder;
                _tracker = tracker;
            }

            _dispatcher = new CaptureDispatcher(tracker, _sync, _options.MaxQueuedBytes);
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

        // A status handler can request stop on the supervisor itself; its finally block owns the actual cleanup.
        if (Thread.CurrentThread == thread) return;
        if (!thread.Join(_options.StopTimeout))
            throw new TimeoutException("Capture is still stopping. Its readers and queued packets must finish before capture can restart.");
    }

    public void StartRecording(string pcapngPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(pcapngPath);
        PcapFileWriter writer;
        PcapFileWriter? old;
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null && _stopEvent.IsSet)
                throw new InvalidOperationException("Capture is stopping. Start recording after it has finished.");

            // Creation and attachment share the lifecycle guard, so disposal cannot finish between them and orphan
            // a newly opened writer. Recording before Start remains supported.
            writer = PcapFileWriter.Create(pcapngPath, PcapFileWriter.FormatForPath(pcapngPath));
            lock (_sync)
            {
                old = _recorder;
                _recorder = writer;
                _recordingPath = Path.GetFullPath(pcapngPath);
                if (_tracker is not null) _tracker.Recorder = writer;
            }
        }

        old?.Dispose();
        AppLog.Info("Capture", $"Recording every game flow to {_recordingPath} ({writer.Format})");
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
        // A timed-out run still owns the recorder until its queued packets drain. A recorder started without a run
        // has no supervisor to close it, so dispose it here.
        if (!IsRunning) StopRecording();
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
        /// <summary>Game process id per locked flow (from the connection table at the time it was seen).</summary>
        public readonly Dictionary<TcpFlowKey, int> FlowPids = new();
        public readonly HashSet<TcpFlowKey> UnlockRequested = new();
    }

    private void Run()
    {
        var st = new LoopState();
        var stop = _stopEvent;
        var tracker = _tracker!;
        var dispatcher = _dispatcher!;
        dispatcher.Start();
        Publish(new CaptureStatus { State = CaptureState.WaitingForGame, Message = "Starting capture, looking for AION 2..." }, force: true);
        try
        {
            while (!stop.IsSet)
            {
                try
                {
                    Step(st, tracker, dispatcher);
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
            // Also reject new recorders if this run exits through a fault rather than a public Stop request.
            lock (_lifecycle) stop.Set();
            try
            {
                CloseAllReaders();
                dispatcher.Complete();
                StopRecording();
                Publish(new CaptureStatus { State = CaptureState.Stopped, Message = "Capture is stopped." }, force: true);
            }
            finally
            {
                lock (_lifecycle)
                    if (ReferenceEquals(_thread, Thread.CurrentThread)) _thread = null;
            }
        }
    }

    private void Step(LoopState st, GameFlowTracker tracker, CaptureDispatcher dispatcher)
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

        // 2. Game process and ALL its connections (every ESTABLISHED port-13328 connection is a game flow); adapters.
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

        // 3. Hints + per-flow watchdog (process exit, adapter change). Idle flows end in the tracker (dispatch thread).
        IReadOnlyList<FlowLockInfo> locks;
        lock (_sync)
        {
            tracker.SetHints(hintConnections.Select(c => c.Hint));
            locks = tracker.ActiveLocks;
        }

        var open = new HashSet<TcpFlowKey>(locks.Select(l => l.Key));
        foreach (var k in st.FlowPids.Keys.Where(k => !open.Contains(k)).ToList()) st.FlowPids.Remove(k);
        st.UnlockRequested.RemoveWhere(k => !open.Contains(k));
        TimeSpan IdleFor(TcpFlowKey key)
        {
            DateTime? last;
            lock (_sync) last = tracker.LastPacketOf(key);
            return last is { } l ? now - l : TimeSpan.Zero;
        }

        foreach (var info in locks)
        {
            var conn = located.Candidates.FirstOrDefault(c => c.Hint.Key == info.Key);
            if (conn is not null && !st.FlowPids.ContainsKey(info.Key)) st.FlowPids[info.Key] = conn.ProcessId;

            string? reason = null;
            if (polled && st.FlowPids.TryGetValue(info.Key, out int pid) && !_locator.IsProcessAlive(pid))
                reason = "the game process exited";
            else if (polled && conn is null && st.FlowPids.ContainsKey(info.Key) && located.ProcessFound
                     && IdleFor(info.Key) >= _options.ClosedConnectionIdle)
                reason = "its connection is gone from the game's connection table"; // reconnect via another adapter, resume
            else if (ovr is not null && _sourceAdapters.TryGetValue(info.SourceId, out var la) && !AdapterSelector.MatchesOverride(la, ovr))
                reason = "the capture adapter was changed";
            if (reason is null || !st.UnlockRequested.Add(info.Key)) continue;
            var key = info.Key;
            dispatcher.Enqueue(t => t.Unlock(key, reason, DateTime.UtcNow));
        }

        locks = locks.Where(l => !st.UnlockRequested.Contains(l.Key)).ToList();

        // 4. What should be open? The adapters of every locked flow and of every hinted connection.
        var want = new List<AdapterInfo>();
        void Want(AdapterInfo? a)
        {
            if (a is not null && !want.Any(w => string.Equals(w.Name, a.Name, StringComparison.OrdinalIgnoreCase))) want.Add(a);
        }

        foreach (var l in locks)
            if (_sourceAdapters.TryGetValue(l.SourceId, out var la)) Want(la);
        bool hintAdapterMissing = false;
        foreach (var c in hintConnections)
        {
            var sel = AdapterSelector.Select(st.Adapters, c.Local.IPAddress, ovr);
            if (sel.Adapter is not null) Want(sel.Adapter);
            else hintAdapterMissing = true;
        }

        var servers = locks.Select(l => l.Server).Concat(hintConnections.Select(c => c.Remote)).Distinct().ToList();
        string filter;
        CaptureState state;
        string message;
        AdapterInfo? statusAdapter = null;
        string? local = null, server = null;
        int? pidForStatus = located.Processes.Count > 0 ? located.Processes[0].ProcessId : null;

        if (locks.Count > 0)
        {
            var newest = locks[^1];
            _sourceAdapters.TryGetValue(newest.SourceId, out statusAdapter);
            if (want.Count == 0) want.AddRange(_readers.Values.Select(r => r.Adapter));
            filter = CaptureFilters.ForServers(servers, true, _options.Tracker.GameServerPort);
            state = CaptureState.Capturing;
            server = string.Join(" + ", locks.Select(l => l.Server.ToString()).Distinct());
            local = string.Join(" + ", locks.Select(l => l.Client.ToString()).Distinct());
            if (st.FlowPids.TryGetValue(newest.Key, out int pid)) pidForStatus = pid;
            string where = want.Count <= 1 ? statusAdapter?.Description ?? want.FirstOrDefault()?.Description ?? "adapter" : $"{want.Count} adapters";
            message = locks.Count == 1
                ? $"Capturing game traffic {server} -> {local} on {where}."
                : $"Capturing {locks.Count} game flows ({server}) on {where}.";
        }
        else if (hintConnections.Count > 0 && located.Best is { } best)
        {
            local = string.Join(" + ", hintConnections.Select(c => c.Local.ToString()).Distinct());
            server = string.Join(" + ", hintConnections.Select(c => c.Remote.ToString()).Distinct());
            pidForStatus = best.ProcessId;
            filter = CaptureFilters.ForServers(servers, true, _options.Tracker.GameServerPort);
            if (want.Count > 0)
            {
                statusAdapter = want[0];
                state = CaptureState.Detecting;
                message = $"AION 2 (pid {best.ProcessId}) is connected to {server} from {local}. " +
                          $"Waiting for game traffic on {DescribeSet(want)}...";
            }
            else if (ovr is not null)
            {
                state = CaptureState.Error;
                message = $"The selected capture adapter '{ovr}' was not found. Choose another adapter or switch to automatic.";
            }
            else
            {
                want.AddRange(AdapterSelector.SelectScanSet(st.Adapters, null, includeLoopback: true));
                state = CaptureState.Detecting;
                message = $"AION 2 is connected to {server} from {local}, but no capture adapter has the address " +
                          $"{best.Local.AddressString}. Watching {want.Count} adapters...";
            }
        }
        else if (located.ProcessFound)
        {
            want.AddRange(AdapterSelector.SelectScanSet(st.Adapters, ovr, includeLoopback: true));
            filter = CaptureFilters.Detection();
            state = want.Count > 0 ? CaptureState.Detecting : CaptureState.Error;
            message = want.Count > 0
                ? $"AION 2 is running (pid {located.Processes[0].ProcessId}) but its game connection was not found yet. " +
                  $"Looking for the game heartbeat on {DescribeSet(want)}..."
                : ovr is not null ? $"The selected capture adapter '{ovr}' was not found." : "No usable capture adapter was found.";
        }
        else
        {
            if (_options.ScanWithoutProcess) want.AddRange(AdapterSelector.SelectScanSet(st.Adapters, ovr, includeLoopback: false));
            filter = CaptureFilters.GamePort(_options.Tracker.GameServerPort);
            state = CaptureState.WaitingForGame;
            message = want.Count > 0
                ? $"Waiting for AION 2. Start the game; watching {DescribeSet(want)} for its connection."
                : "Waiting for AION 2. Start the game.";
        }

        if (locks.Count > 0 && hintAdapterMissing && ovr is null)
        {
            // A hinted game connection whose local address no adapter owns (VPN/loopback relay): keep scanning for it too.
            foreach (var a in AdapterSelector.SelectScanSet(st.Adapters, null, includeLoopback: true)) Want(a);
        }

        // 5. Open/close adapters.
        ApplyReaders(want, filter, now, dispatcher);
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

    private void ApplyReaders(IReadOnlyList<AdapterInfo> want, string filter, DateTime now, CaptureDispatcher dispatcher)
    {
        for (int i = _retiring.Count - 1; i >= 0; i--)
        {
            if (now < _retiring[i].DisposeAt) continue;
            _retiring[i].Reader.Dispose();
            _retiring.RemoveAt(i);
        }

        var names = new HashSet<string>(want.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var name in _readers.Keys.ToList())
        {
            var r = _readers[name];
            if (!names.Contains(name) || !r.IsRunning)
            {
                if (!r.IsRunning && r.Error is not null) NoteOpenFailure(r.Adapter, r.Error, now);
                _readers.Remove(name);
                r.Dispose();
            }
        }

        foreach (var adapter in want)
        {
            if (_readers.TryGetValue(adapter.Name, out var existing))
            {
                if (!CaptureFilters.Equivalent(existing.Filter, filter)) SwapFilter(existing, filter, now, dispatcher);
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
                _readers[adapter.Name] = DeviceReader.Open(adapter, id, filter, _options, dispatcher.EnqueuePacket);
                if (_openFailures.Remove(adapter.Name)) AppLog.Info("Capture", $"{adapter.Description} opened after earlier failures");
                _openErrors.Remove(adapter.Name);
                _openRetryAfter.Remove(adapter.Name);
            }
            catch (Exception ex)
            {
                string msg = ex is TypeInitializationException { InnerException: { } inner } ? inner.Message : ex.Message;
                NoteOpenFailure(adapter, msg, now);
            }
        }
    }

    /// <summary>
    /// Changing the filter of a live handle (BIOCSETF) throws away what it has buffered, which is exactly the connect-time
    /// burst of a new game connection. So a handle with the new filter is opened first, and the old one keeps reading for
    /// <see cref="CaptureServiceOptions.FilterSwapOverlap"/> before it is closed.
    /// </summary>
    private void SwapFilter(DeviceReader existing, string filter, DateTime now, CaptureDispatcher dispatcher)
    {
        DeviceReader replacement;
        try
        {
            replacement = DeviceReader.Open(existing.Adapter, existing.SourceId, filter, _options, dispatcher.EnqueuePacket);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Capture", $"{existing.Adapter.Description}: cannot open a second handle ({ex.Message}); changing the filter in place");
            existing.SetFilter(filter);
            return;
        }

        _readers[existing.Adapter.Name] = replacement;
        _retiring.Add((existing, now + _options.FilterSwapOverlap));
    }

    /// <summary>Exponential retry backoff; a warning is logged for the first failure and when the error changes only (an
    /// adapter that cannot be opened must not write a log line every few seconds, around the clock).</summary>
    private void NoteOpenFailure(AdapterInfo adapter, string message, DateTime now)
    {
        bool changed = !_openErrors.TryGetValue(adapter.Name, out var previous) || previous != message;
        int failures = _openFailures.GetValueOrDefault(adapter.Name) + 1;
        _openFailures[adapter.Name] = failures;
        _openErrors[adapter.Name] = message;
        _openRetryAfter[adapter.Name] = now + RetryDelay(failures, _options.OpenRetryInterval, _options.MaxOpenRetryInterval);
        if (changed) AppLog.Warn("Capture", $"Cannot open {adapter.Description}: {message}");
        else AppLog.Debug("Capture", $"Cannot open {adapter.Description} (attempt {failures}): {message}");
    }

    /// <summary>Delay before the next attempt after <paramref name="failures"/> consecutive failures (1 = first).</summary>
    internal static TimeSpan RetryDelay(int failures, TimeSpan first, TimeSpan max)
    {
        if (first <= TimeSpan.Zero) return TimeSpan.Zero;
        double factor = Math.Pow(2, Math.Clamp(failures - 1, 0, 30));
        double ms = Math.Min(first.TotalMilliseconds * factor, Math.Max(first.TotalMilliseconds, max.TotalMilliseconds));
        return TimeSpan.FromMilliseconds(ms);
    }

    private void CloseAllReaders()
    {
        foreach (var r in _readers.Values) r.Dispose();
        _readers.Clear();
        foreach (var (r, _) in _retiring) r.Dispose();
        _retiring.Clear();
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
