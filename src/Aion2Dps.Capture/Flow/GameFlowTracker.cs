using Aion2Dps.Contracts;

namespace Aion2Dps.Capture;

/// <summary>Tuning of <see cref="GameFlowTracker"/>. Defaults follow PROTOCOL.md §2 and §3 and LIVE-FINDINGS NEW 1.</summary>
public sealed class FlowTrackerOptions
{
    /// <summary>Heartbeat hits are counted inside this sliding window (capture time).</summary>
    public TimeSpan SignatureWindow { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>Hits needed when the sending side uses the game port (13328).</summary>
    public int GamePortHits { get; set; } = 3;
    /// <summary>Hits needed on any other port.</summary>
    public int OtherPortHits { get; set; } = 12;
    /// <summary>Hits needed on a flow named by a <see cref="FlowHint"/> (the game process's own connection).</summary>
    public int HintedHits { get; set; } = 1;
    public int GameServerPort { get; set; } = GameSignature.GameServerPort;
    /// <summary>Rolling pre-lock buffer per candidate flow (raw frames, both directions).</summary>
    public int MaxBufferedBytesPerFlow { get; set; } = 2 * 1024 * 1024;
    public int MaxBufferedPacketsPerFlow { get; set; } = 8192;
    /// <summary>Total pre-lock buffer over all candidate flows.</summary>
    public int MaxTotalBufferedBytes { get; set; } = 32 * 1024 * 1024;
    public int MaxCandidateFlows { get; set; } = 1024;
    /// <summary>Candidate flows without packets for this long are forgotten.</summary>
    public TimeSpan CandidateIdleTimeout { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>Most game flows followed at the same time (world + instance + reconnect overlap; real sessions use 1-2).</summary>
    public int MaxConcurrentFlows { get; set; } = 8;
    /// <summary>A game flow without any packet for this long ends (its sink is released).</summary>
    public TimeSpan FlowIdleTimeout { get; set; } = TimeSpan.FromSeconds(90);
    /// <summary>After a FIN in only one direction the flow ends this long later (a FIN in both directions or an RST ends
    /// it at once).</summary>
    public TimeSpan FinGracePeriod { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>Packets of an ended flow (final ACKs, retransmits) are still recorded, but otherwise ignored, for this long.</summary>
    public TimeSpan EndedFlowLinger { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Not used any more: every qualifying game flow is followed concurrently (LIVE-FINDINGS NEW 1). Kept for
    /// settings compatibility.</summary>
    public TimeSpan SwitchAfterSilence { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan GapTimeout { get; set; } = TcpReassembler.DefaultGapTimeout;
    public int MaxPendingBytes { get; set; } = TcpReassembler.DefaultMaxPendingBytes;
    /// <summary>Bounds the memory overhead of many tiny out-of-order TCP segments.</summary>
    public int MaxPendingSegments { get; set; } = TcpReassembler.DefaultMaxPendingSegments;
}

/// <summary>Describes one locked game connection.</summary>
/// <param name="Server">Game server endpoint (sender of the delivered bytes).</param>
/// <param name="Client">Local endpoint.</param>
/// <param name="SourceId">Id of the capture source (adapter) that delivered the qualifying packets.</param>
/// <param name="LockedAtUtc">Capture time of the lock.</param>
/// <param name="ByHint">True when the flow was named by the game process's connection table.</param>
/// <param name="LockNumber">1 for the first flow locked by the tracker, 2 for the next one, ...</param>
public sealed record FlowLockInfo(IPv4Endpoint Server, IPv4Endpoint Client, int SourceId, DateTime LockedAtUtc, bool ByHint, int LockNumber)
{
    public TcpFlowKey Key => TcpFlowKey.Create(Server, Client);
    /// <summary>Key passed to <see cref="IStreamSinkFactory.CreateFlowSink"/>: <c>server&gt;client</c>.</summary>
    public string FlowKey => $"{Server}>{Client}";
    public override string ToString() => $"{Server} -> {Client}";
}

/// <summary>
/// Finds every game connection among TCP flows and reassembles each one's server→client direction into its own sink
/// (LIVE-FINDINGS NEW 1: the game keeps the world and the dungeon-instance connection open at the same time).
/// <list type="bullet">
/// <item>Candidate flows are scored independently by the heartbeat signature <c>0E 00 36</c>+8 (or legacy
/// <c>06 00 36</c>) in payloads of one direction: ≥ 3 hits within 3 s when the sender's port is 13328, ≥ 12 otherwise,
/// ≥ 1 for a flow named by <see cref="SetHints"/>. Flows whose first payload looks like TLS are ignored. Every flow that
/// qualifies is locked (up to <see cref="FlowTrackerOptions.MaxConcurrentFlows"/>); a new flow never replaces another.</item>
/// <item>Each locked flow gets its own <see cref="TcpReassembler"/> and its own sink from the
/// <see cref="IStreamSinkFactory"/>. The first segments of every candidate are buffered and replayed into that sink after
/// the lock, so identity records sent at connect are not lost.</item>
/// <item>A flow ends on RST, on FIN in both directions (or one FIN plus <see cref="FlowTrackerOptions.FinGracePeriod"/>),
/// or after <see cref="FlowTrackerOptions.FlowIdleTimeout"/> without packets: its remaining bytes are flushed and the
/// sink is released. A TCP gap or a restarted connection (new SYN on the same 4-tuple) is reported to that flow's sink only.</item>
/// <item>A plain <see cref="IStreamSink"/> passed to the constructor is wrapped in a <see cref="SingleSinkFlowAdapter"/>
/// (newest flow owns the sink) unless it also implements <see cref="IStreamSinkFactory"/>.</item>
/// </list>
/// Not thread-safe: the caller serialises all calls on one thread (the live service's dispatch thread, the replay thread),
/// so every sink call happens on that thread in capture order.
/// </summary>
public sealed class GameFlowTracker
{
    /// <summary>How many ended flows <see cref="GetFlows"/> reports.</summary>
    public const int EndedFlowHistory = 32;

    private readonly FlowTrackerOptions _options;
    private readonly IStreamSinkFactory _factory;
    private readonly StreamSinkGuard? _session;
    private readonly Dictionary<TcpFlowKey, Candidate> _candidates = new();
    private readonly Dictionary<TcpFlowKey, FlowHint> _hints = new();
    private readonly Dictionary<TcpFlowKey, LockedFlow> _flows = new();
    private readonly Dictionary<TcpFlowKey, DateTime> _ended = new(); // key → linger deadline
    private readonly Queue<CaptureFlowInfo> _endedInfo = new();
    private LockedFlow? _newest;
    private long _totalBuffered;
    private DateTime _lastEviction;
    private long _recorderErrors;
    private long _endedBytes, _endedGaps, _endedSkipped;
    private bool _maxFlowsWarned;

    /// <summary>Single-stream compatibility constructor. When <paramref name="sink"/> also implements
    /// <see cref="IStreamSinkFactory"/> every flow gets its own sink from it and <paramref name="sink"/> receives the
    /// session-wide discontinuities; otherwise a <see cref="SingleSinkFlowAdapter"/> feeds one flow at a time into it.</summary>
    public GameFlowTracker(IStreamSink sink, FlowTrackerOptions? options = null)
        : this(AsFactory(sink), options, sink)
    {
    }

    /// <param name="factory">Creates/releases one sink per game flow.</param>
    /// <param name="options">Tuning; null = defaults.</param>
    /// <param name="sessionSink">Receives session-wide discontinuities (<see cref="OnSessionDiscontinuity"/>); null = none.
    /// (No defaults here, so an object that is both a sink and a factory binds to the compatibility constructor.)</param>
    public GameFlowTracker(IStreamSinkFactory factory, FlowTrackerOptions? options, IStreamSink? sessionSink)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _options = options ?? new FlowTrackerOptions();
        if (sessionSink is not null) _session = new StreamSinkGuard(sessionSink);
    }

    private static IStreamSinkFactory AsFactory(IStreamSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        return sink as IStreamSinkFactory ?? new SingleSinkFlowAdapter(sink);
    }

    public FlowTrackerOptions Options => _options;

    /// <summary>The factory flows are delivered to (a <see cref="SingleSinkFlowAdapter"/> for a plain sink).</summary>
    public IStreamSinkFactory Factory => _factory;

    /// <summary>Receives every raw frame of every locked flow, both directions (including the pre-lock buffers).</summary>
    public IPacketRecorder? Recorder { get; set; }

    /// <summary>The most recently locked flow that is still open, or null while no game flow is open.</summary>
    public FlowLockInfo? CurrentLock => _newest?.Info;

    /// <summary>All open game flows, in lock order.</summary>
    public IReadOnlyList<FlowLockInfo> ActiveLocks => _flows.Values.OrderBy(f => f.Info.LockNumber).Select(f => f.Info).ToList();

    /// <summary>Number of open game flows.</summary>
    public int ActiveFlowCount => _flows.Count;

    /// <summary>Largest number of game flows that were open at the same time.</summary>
    public int MaxConcurrentFlows { get; private set; }

    /// <summary>Capture time of the latest packet of any locked flow.</summary>
    public DateTime? LastLockedPacketUtc { get; private set; }

    /// <summary>Capture time of the latest packet of the open flow <paramref name="key"/>, or null when it is not open.</summary>
    public DateTime? LastPacketOf(TcpFlowKey key) => _flows.TryGetValue(key, out var f) ? f.LastPacketUtc : null;

    /// <summary>The <see cref="CurrentLock"/> flow has seen FIN or RST.</summary>
    public bool LockedFlowClosed => _newest?.Closing ?? false;

    public long PacketsSeen { get; private set; }
    public long TcpPackets { get; private set; }
    /// <summary>Packets of locked flows (all flows, both directions, including replayed pre-lock buffers).</summary>
    public long LockedFlowPackets { get; private set; }
    /// <summary>Server→client bytes delivered over all flows.</summary>
    public long BytesDelivered
    {
        get
        {
            long n = _endedBytes;
            foreach (var f in _flows.Values) n += f.Guard.BytesDelivered;
            return n;
        }
    }

    /// <summary>TCP gaps over all flows.</summary>
    public long GapCount
    {
        get
        {
            long n = _endedGaps;
            foreach (var f in _flows.Values) n += f.Guard.GapCount;
            return n;
        }
    }

    /// <summary>Number of game flows locked so far.</summary>
    public int LockCount { get; private set; }
    public long TlsFlowsIgnored { get; private set; }
    public int CandidateCount => _candidates.Count;
    public long BufferedBytes => _totalBuffered;
    /// <summary>Bytes skipped by gap give-ups over all flows (lost data).</summary>
    public long SkippedBytes
    {
        get
        {
            long n = _endedSkipped;
            foreach (var f in _flows.Values) n += f.Reassembler.SkippedBytes;
            return n;
        }
    }

    /// <summary>Raised after a flow has been locked and its buffer replayed into its sink.</summary>
    public event Action<FlowLockInfo>? Locked;

    /// <summary>Raised when a flow ends (FIN/RST, idle, <see cref="Unlock(string)"/>), after its sink was released.</summary>
    public event Action<FlowLockInfo, string>? Unlocked;

    /// <summary>Game connections known from the process connection table. Such flows lock on the first heartbeat.</summary>
    public void SetHints(IEnumerable<FlowHint>? hints)
    {
        _hints.Clear();
        if (hints is null) return;
        foreach (var h in hints) _hints[h.Key] = h;
    }

    public IReadOnlyCollection<FlowHint> Hints => _hints.Values;

    /// <summary>Open flows first (lock order), then up to <see cref="EndedFlowHistory"/> ended ones (oldest first).</summary>
    public IReadOnlyList<CaptureFlowInfo> GetFlows()
    {
        var list = new List<CaptureFlowInfo>(_flows.Count + _endedInfo.Count);
        foreach (var f in _flows.Values.OrderBy(f => f.Info.LockNumber)) list.Add(f.ToInfo(true, null));
        list.AddRange(_endedInfo);
        return list;
    }

    /// <summary>Sends a session-wide discontinuity (capture restart, replay start) to the session sink.</summary>
    public void OnSessionDiscontinuity(DiscontinuityReason reason) => _session?.OnDiscontinuity(reason);

    /// <summary>Feeds one captured link-layer frame. Returns false when it is not an IPv4/TCP packet.</summary>
    public bool OnPacket(DateTime timeUtc, int linkType, ReadOnlySpan<byte> frame, int sourceId = 0)
    {
        PacketsSeen++;
        if (!PacketParser.TryParse(linkType, frame, out var segment)) return false;
        OnSegment(timeUtc, segment, sourceId, linkType, frame);
        return true;
    }

    /// <summary>Feeds one parsed TCP segment. <paramref name="frame"/> (optional) is the raw frame for recording.</summary>
    public void OnSegment(DateTime timeUtc, in TcpSegment segment, int sourceId = 0, int linkType = -1, ReadOnlySpan<byte> frame = default)
    {
        TcpPackets++;
        var key = TcpFlowKey.Create(segment.Source, segment.Destination);
        if (_flows.TryGetValue(key, out var flow))
        {
            OnLockedSegment(flow, timeUtc, segment, linkType, frame);
        }
        else if (!OnEndedSegment(key, timeUtc, segment, linkType, frame))
        {
            OnCandidateSegment(timeUtc, key, segment, sourceId, linkType, frame);
        }

        if (timeUtc - _lastEviction > TimeSpan.FromSeconds(1)) Evict(timeUtc);
    }

    /// <summary>Drives gap give-up, flow end (FIN grace, idle timeout) and candidate eviction with the capture clock.</summary>
    public void Tick(DateTime nowUtc)
    {
        if (_flows.Count > 0)
        {
            List<(LockedFlow Flow, string Reason)>? ending = null;
            foreach (var f in _flows.Values)
            {
                f.Reassembler.Tick(nowUtc);
                string? reason = null;
                if (f.Closing && f.ClosingSince is { } since && nowUtc - since >= _options.FinGracePeriod) reason = "connection closed (FIN)";
                else if (nowUtc - f.LastPacketUtc > _options.FlowIdleTimeout)
                    reason = $"no packets for {(int)_options.FlowIdleTimeout.TotalSeconds} s";
                if (reason is not null) (ending ??= new()).Add((f, reason));
            }

            if (ending is not null)
                foreach (var (f, reason) in ending) End(f, nowUtc, reason);
        }

        if (nowUtc - _lastEviction > TimeSpan.FromSeconds(1)) Evict(nowUtc);
    }

    /// <summary>Delivers all bytes still held by every flow's reassembler, skipping holes (end of a replay).</summary>
    public void Flush(DateTime nowUtc)
    {
        foreach (var f in _flows.Values) f.Reassembler.Flush(nowUtc);
    }

    /// <summary>Ends every open flow (flushes and releases their sinks) and returns to detection.</summary>
    public void Unlock(string reason) => UnlockAll(LastLockedPacketUtc ?? DateTime.UtcNow, reason);

    /// <summary>Ends every open flow at <paramref name="nowUtc"/>.</summary>
    public void UnlockAll(DateTime nowUtc, string reason)
    {
        foreach (var f in _flows.Values.OrderBy(f => f.Info.LockNumber).ToList()) End(f, nowUtc, reason);
    }

    /// <summary>Ends one open flow. Returns false when it is not open.</summary>
    public bool Unlock(TcpFlowKey key, string reason, DateTime? nowUtc = null)
    {
        if (!_flows.TryGetValue(key, out var f)) return false;
        End(f, nowUtc ?? f.LastPacketUtc, reason);
        return true;
    }

    /// <summary>End-of-file fallback for replays: when nothing was ever locked, lock every remaining candidate whose
    /// server side uses the game port (or, failing that, the one carrying the most heartbeats). Returns true if a flow was
    /// locked.</summary>
    public bool TryFallbackLock(DateTime nowUtc)
    {
        if (_flows.Count > 0 || LockCount > 0) return false;
        var picks = new List<(Candidate C, IPv4Endpoint Server)>();
        Candidate? best = null;
        IPv4Endpoint bestServer = default;
        long bestScore = -1;
        foreach (var c in _candidates.Values.OrderBy(c => c.FirstSeen))
        {
            if (c.Tls) continue;
            IPv4Endpoint server;
            bool gamePort = true;
            if (c.Key.A.Port == _options.GameServerPort && c.Key.B.Port != _options.GameServerPort) server = c.Key.A;
            else if (c.Key.B.Port == _options.GameServerPort && c.Key.A.Port != _options.GameServerPort) server = c.Key.B;
            else if (c.HitsFromA.Total > 0 || c.HitsFromB.Total > 0)
            {
                server = c.HitsFromA.Total >= c.HitsFromB.Total ? c.Key.A : c.Key.B;
                gamePort = false;
            }
            else continue;
            long payload = server == c.Key.A ? c.PayloadFromA : c.PayloadFromB;
            if (payload == 0) continue;
            if (gamePort)
            {
                picks.Add((c, server));
                continue;
            }

            long hits = server == c.Key.A ? c.HitsFromA.Total : c.HitsFromB.Total;
            long score = hits * 1_000_000_000L + payload;
            if (score > bestScore)
            {
                bestScore = score;
                best = c;
                bestServer = server;
            }
        }

        if (picks.Count == 0 && best is not null) picks.Add((best, bestServer));
        foreach (var (c, server) in picks)
        {
            if (_flows.Count >= Math.Max(1, _options.MaxConcurrentFlows)) break;
            Lock(c, server, nowUtc, _hints.ContainsKey(c.Key));
        }

        return picks.Count > 0;
    }

    // ───────────────────────────── locked flows ─────────────────────────────

    private void OnLockedSegment(LockedFlow flow, DateTime timeUtc, in TcpSegment segment, int linkType, ReadOnlySpan<byte> frame)
    {
        LockedFlowPackets++;
        flow.Packets++;
        if (timeUtc > flow.LastPacketUtc) flow.LastPacketUtc = timeUtc;
        if (LastLockedPacketUtc is null || timeUtc > LastLockedPacketUtc) LastLockedPacketUtc = timeUtc;
        if (!frame.IsEmpty && linkType >= 0) Record(timeUtc, linkType, frame);

        bool fromServer = segment.Source == flow.Server;
        if (fromServer && (segment.Flags & TcpFlags.Syn) != 0 && flow.ServerIsn != segment.Sequence)
        {
            // A new connection on the same 4-tuple (SYN-ACK from the server with a new ISN): restart this flow's stream.
            if (flow.Reassembler.NextSequence is not null)
            {
                flow.Reassembler.Reset();
                flow.Guard.OnDiscontinuity(DiscontinuityReason.NewConnection);
            }

            flow.ServerIsn = segment.Sequence;
            flow.Reassembler.Seed(unchecked(segment.Sequence + 1));
            flow.ServerFin = flow.ClientFin = false;
            flow.ClosingSince = null;
        }

        if (fromServer) flow.Reassembler.Push(PayloadSequence(segment), segment.Payload, timeUtc);

        if ((segment.Flags & TcpFlags.Rst) != 0)
        {
            End(flow, timeUtc, "connection reset (RST)");
            return;
        }

        if ((segment.Flags & TcpFlags.Fin) != 0)
        {
            if (fromServer) flow.ServerFin = true; else flow.ClientFin = true;
            flow.ClosingSince ??= timeUtc;
            if (flow.ServerFin && flow.ClientFin) End(flow, timeUtc, "connection closed (FIN)");
        }
    }

    /// <summary>Packets of a recently ended flow: recorded and ignored, unless a new connection starts on the 4-tuple.</summary>
    private bool OnEndedSegment(TcpFlowKey key, DateTime timeUtc, in TcpSegment segment, int linkType, ReadOnlySpan<byte> frame)
    {
        if (!_ended.TryGetValue(key, out var until)) return false;
        if (timeUtc > until || (segment.Flags & TcpFlags.Syn) != 0)
        {
            _ended.Remove(key);
            return false;
        }

        if (!frame.IsEmpty && linkType >= 0) Record(timeUtc, linkType, frame);
        return true;
    }

    private void End(LockedFlow flow, DateTime nowUtc, string reason)
    {
        if (!_flows.Remove(flow.Key)) return;
        try
        {
            flow.Reassembler.Flush(nowUtc);
        }
        catch (Exception ex)
        {
            AppLog.Error("Capture", $"Flushing game flow {flow.Info} failed", ex);
        }

        try
        {
            _factory.ReleaseFlowSink(flow.Info.FlowKey);
        }
        catch (Exception ex)
        {
            AppLog.Error("Capture", $"Releasing the sink of game flow {flow.Info} failed", ex);
        }

        _endedBytes += flow.Guard.BytesDelivered;
        _endedGaps += flow.Guard.GapCount;
        _endedSkipped += flow.Reassembler.SkippedBytes;
        _endedInfo.Enqueue(flow.ToInfo(false, reason));
        while (_endedInfo.Count > EndedFlowHistory) _endedInfo.Dequeue();
        _ended[flow.Key] = nowUtc + _options.EndedFlowLinger;
        if (ReferenceEquals(_newest, flow))
            _newest = _flows.Values.OrderByDescending(f => f.Info.LockNumber).FirstOrDefault();
        AppLog.Info("Capture", $"Game flow {flow.Info} ended: {reason} ({flow.Guard.BytesDelivered} bytes, {_flows.Count} flow(s) still open)");
        Unlocked?.Invoke(flow.Info, reason);
    }

    private void Record(DateTime timeUtc, int linkType, ReadOnlySpan<byte> frame)
    {
        var recorder = Recorder;
        if (recorder is null) return;
        try
        {
            recorder.WritePacket(timeUtc, linkType, frame);
        }
        catch (Exception ex)
        {
            if (++_recorderErrors <= 3) AppLog.Error("Capture", "Recording a packet failed", ex);
        }
    }

    // ───────────────────────────── detection ─────────────────────────────

    private void OnCandidateSegment(DateTime timeUtc, TcpFlowKey key, in TcpSegment segment, int sourceId, int linkType,
        ReadOnlySpan<byte> frame)
    {
        if (!_candidates.TryGetValue(key, out var c))
        {
            if (_candidates.Count >= Math.Max(1, _options.MaxCandidateFlows)) EvictOldest();
            c = new Candidate(key, timeUtc, _options);
            _candidates[key] = c;
        }

        bool fromA = segment.Source == key.A;
        bool syn = (segment.Flags & TcpFlags.Syn) != 0;
        uint? priorSyn = fromA ? c.SynSeqA : c.SynSeqB;
        if (syn && priorSyn != segment.Sequence && (priorSyn is not null || c.PayloadSeen || c.Closed))
        {
            // A tuple can be reused before its idle expiry. Scores, TLS classification and identity bytes belong to
            // the old connection; a repeated SYN with the same ISN still belongs to the original handshake.
            _totalBuffered -= c.ClearBuffer();
            c = new Candidate(key, timeUtc, _options);
            _candidates[key] = c;
        }

        if (timeUtc > c.LastSeen) c.LastSeen = timeUtc;
        c.SourceId = sourceId;
        if (c.Tls) return;

        var payload = segment.Payload;
        if ((segment.Flags & (TcpFlags.Fin | TcpFlags.Rst)) != 0) c.Closed = true;
        var signature = fromA ? c.SignatureFromA : c.SignatureFromB;
        if (syn)
        {
            if (fromA) c.SynSeqA = segment.Sequence; else c.SynSeqB = segment.Sequence;
            signature.Reassembler.Seed(unchecked(segment.Sequence + 1));
            c.Closed = false;
        }

        if (!payload.IsEmpty && !c.PayloadSeen)
        {
            c.PayloadSeen = true;
            if (GameSignature.LooksLikeTls(payload))
            {
                c.Tls = true;
                TlsFlowsIgnored++;
                _totalBuffered -= c.ClearBuffer();
                return;
            }
        }

        BufferPacket(c, timeUtc, segment, linkType, frame);
        if (payload.IsEmpty) return;
        if (fromA) c.PayloadFromA += payload.Length; else c.PayloadFromB += payload.Length;

        int threshold = ThresholdFor(key, segment.Source);
        if (threshold == int.MaxValue) return;
        // Off the game port (relay, VPN, unknown flows in the "process found, no connection" phase) a heartbeat must also
        // carry a plausible server clock: three bytes alone occur in any bulk transfer (~0.125 per MB of random data).
        signature.RequireServerClock = segment.Source.Port != _options.GameServerPort && !_hints.ContainsKey(key);
        signature.Reassembler.Push(PayloadSequence(segment), payload, timeUtc);
        signature.Hits.Expire(timeUtc, _options.SignatureWindow);
        if (signature.Hits.InWindow >= threshold) TryQualify(c, segment.Source, timeUtc);
    }

    private int ThresholdFor(TcpFlowKey key, IPv4Endpoint sender)
    {
        if (_hints.TryGetValue(key, out var hint))
            return sender == hint.Server ? Math.Max(1, _options.HintedHits) : int.MaxValue;
        if (sender.Port == _options.GameServerPort) return _options.GamePortHits;
        // Web and SMB flows are never the game unless the game process names them.
        if (IsBulkPort(key.A.Port) || IsBulkPort(key.B.Port)) return int.MaxValue;
        return _options.OtherPortHits;
    }

    private static bool IsBulkPort(int port) => port is 80 or 443 or 445;

    private void TryQualify(Candidate c, IPv4Endpoint server, DateTime timeUtc)
    {
        if (_flows.Count >= Math.Max(1, _options.MaxConcurrentFlows))
        {
            if (!_maxFlowsWarned)
            {
                _maxFlowsWarned = true;
                AppLog.Warn("Capture", $"Game flow {server} <-> {c.Key.Other(server)} qualifies, but {_flows.Count} flows are already open; ignored");
            }

            return;
        }

        Lock(c, server, timeUtc, _hints.ContainsKey(c.Key));
    }

    private void Lock(Candidate c, IPv4Endpoint server, DateTime timeUtc, bool hinted)
    {
        _candidates.Remove(c.Key);
        _ended.Remove(c.Key);
        _totalBuffered -= c.BufferedBytes;
        LockCount++;
        var info = new FlowLockInfo(server, c.Key.Other(server), c.SourceId, timeUtc, hinted, LockCount);

        IStreamSink sink;
        try
        {
            sink = _factory.CreateFlowSink(info.FlowKey);
        }
        catch (Exception ex)
        {
            AppLog.Error("Capture", $"Creating the sink of game flow {info} failed; its data is dropped", ex);
            sink = NullSink.Instance;
        }

        var guard = new StreamSinkGuard(sink);
        var reassembler = new TcpReassembler(guard, _options.GapTimeout, _options.MaxPendingBytes, _options.MaxPendingSegments);
        var flow = new LockedFlow(info, c.Key, server, guard, reassembler) { LastPacketUtc = c.LastSeen };
        if (c.Closed) flow.ClosingSince = c.LastSeen;
        _flows[c.Key] = flow;
        _newest = flow;
        if (_flows.Count > MaxConcurrentFlows) MaxConcurrentFlows = _flows.Count;
        if (LastLockedPacketUtc is null || c.LastSeen > LastLockedPacketUtc) LastLockedPacketUtc = c.LastSeen;

        AppLog.Info("Capture", $"Locked game flow #{info.LockNumber} {info} (source {c.SourceId}, " +
                               $"{(hinted ? "process hint" : "heartbeat signature")}, {c.Buffer.Count} buffered packets, " +
                               $"{_flows.Count} flow(s) open)");

        // Seed with the server's SYN, else with the smallest buffered server sequence number.
        uint? synSeq = server == c.Key.A ? c.SynSeqA : c.SynSeqB;
        flow.ServerIsn = synSeq;
        if (synSeq is { } s) reassembler.Seed(unchecked(s + 1));
        else
        {
            bool have = false;
            uint min = 0;
            foreach (var b in c.Buffer)
            {
                if (b.Source != server || b.PayloadLength == 0) continue;
                if (!have || unchecked((int)(b.Sequence - min)) < 0) min = b.Sequence;
                have = true;
            }

            if (have) reassembler.Seed(min);
        }

        foreach (var b in c.Buffer)
        {
            LockedFlowPackets++;
            flow.Packets++;
            if (b.HasFrame) Record(b.TimeUtc, b.LinkType, b.Data);
            if (b.Source == server && b.PayloadLength > 0)
                reassembler.Push(b.Sequence, b.Data.AsSpan(b.PayloadOffset, b.PayloadLength), b.TimeUtc);
        }

        c.Buffer.Clear();
        c.BufferedBytes = 0;
        Locked?.Invoke(info);
    }

    private void BufferPacket(Candidate c, DateTime timeUtc, in TcpSegment segment, int linkType, ReadOnlySpan<byte> frame)
    {
        Buffered b;
        if (!frame.IsEmpty && linkType >= 0)
            b = new Buffered(timeUtc, segment.Source, PayloadSequence(segment), frame.ToArray(), segment.PayloadOffset, segment.Payload.Length, linkType, true);
        else
            b = new Buffered(timeUtc, segment.Source, PayloadSequence(segment), segment.Payload.ToArray(), 0, segment.Payload.Length, -1, false);

        int perFlowLimit = Math.Max(0, _options.MaxBufferedBytesPerFlow);
        int totalLimit = Math.Max(0, _options.MaxTotalBufferedBytes);
        if (b.Data.Length == 0 || b.Data.Length > perFlowLimit || b.Data.Length > totalLimit) return;
        while (c.Buffer.Count > 0 && (c.BufferedBytes + b.Data.Length > perFlowLimit ||
                                    c.Buffer.Count >= Math.Max(1, _options.MaxBufferedPacketsPerFlow)))
        {
            var old = c.Buffer.Dequeue();
            c.BufferedBytes -= old.Data.Length;
            _totalBuffered -= old.Data.Length;
        }

        c.Buffer.Enqueue(b);
        c.BufferedBytes += b.Data.Length;
        _totalBuffered += b.Data.Length;

        while (_totalBuffered > totalLimit)
        {
            Candidate? largest = null;
            foreach (var other in _candidates.Values)
                if (other != c && (largest is null || other.BufferedBytes > largest.BufferedBytes)) largest = other;
            if (largest is { BufferedBytes: > 0 }) _totalBuffered -= largest.ClearBuffer();
            else
            {
                var old = c.Buffer.Dequeue();
                c.BufferedBytes -= old.Data.Length;
                _totalBuffered -= old.Data.Length;
            }
        }
    }

    private void Evict(DateTime nowUtc)
    {
        _lastEviction = nowUtc;
        if (_ended.Count > 0)
        {
            List<TcpFlowKey>? gone = null;
            foreach (var (k, until) in _ended)
                if (nowUtc > until) (gone ??= new()).Add(k);
            if (gone is not null)
                foreach (var k in gone) _ended.Remove(k);
        }

        if (_candidates.Count == 0) return;
        List<TcpFlowKey>? stale = null;
        foreach (var (k, c) in _candidates)
            if (nowUtc - c.LastSeen > _options.CandidateIdleTimeout) (stale ??= new()).Add(k);
        if (stale is null) return;
        foreach (var k in stale)
        {
            _totalBuffered -= _candidates[k].BufferedBytes;
            _candidates.Remove(k);
        }
    }

    private void EvictOldest()
    {
        Candidate? oldest = null;
        foreach (var c in _candidates.Values)
            if (oldest is null || c.LastSeen < oldest.LastSeen) oldest = c;
        if (oldest is null) return;
        _totalBuffered -= oldest.BufferedBytes;
        _candidates.Remove(oldest.Key);
    }

    // ───────────────────────────── state types ─────────────────────────────

    private static uint PayloadSequence(in TcpSegment segment) =>
        unchecked(segment.Sequence + ((segment.Flags & TcpFlags.Syn) != 0 ? 1u : 0u));

    private sealed class NullSink : IStreamSink
    {
        public static readonly NullSink Instance = new();
        public void OnData(DateTime timeUtc, ReadOnlySpan<byte> data) { }
        public void OnDiscontinuity(DiscontinuityReason reason) { }
    }

    private sealed class LockedFlow(FlowLockInfo info, TcpFlowKey key, IPv4Endpoint server, StreamSinkGuard guard, TcpReassembler reassembler)
    {
        public FlowLockInfo Info { get; } = info;
        public TcpFlowKey Key { get; } = key;
        public IPv4Endpoint Server { get; } = server;
        public StreamSinkGuard Guard { get; } = guard;
        public TcpReassembler Reassembler { get; } = reassembler;
        public uint? ServerIsn { get; set; }
        public bool ServerFin { get; set; }
        public bool ClientFin { get; set; }
        public DateTime? ClosingSince { get; set; }
        public bool Closing => ClosingSince is not null;
        public DateTime LastPacketUtc { get; set; }
        public long Packets { get; set; }

        public CaptureFlowInfo ToInfo(bool open, string? endReason) => new(Info.FlowKey, Info.Server.ToString(), Info.Client.ToString(),
            Info.ByHint, Info.LockedAtUtc, LastPacketUtc, Packets, Guard.BytesDelivered, Guard.GapCount, open, endReason);
    }

    private readonly record struct Buffered(DateTime TimeUtc, IPv4Endpoint Source, uint Sequence, byte[] Data,
        int PayloadOffset, int PayloadLength, int LinkType, bool HasFrame);

    private sealed class HitWindow
    {
        private readonly Queue<(DateTime Time, int Count)> _hits = new();
        public int InWindow { get; private set; }
        public long Total { get; private set; }

        public void Add(DateTime timeUtc, int count, TimeSpan window)
        {
            _hits.Enqueue((timeUtc, count));
            InWindow += count;
            Total += count;
            Expire(timeUtc, window);
        }

        public void Expire(DateTime timeUtc, TimeSpan window)
        {
            while (_hits.Count > 0 && timeUtc - _hits.Peek().Time > window)
                InWindow -= _hits.Dequeue().Count;
        }
    }

    private sealed class SignatureDirection : IStreamSink
    {
        private readonly HeartbeatSignatureScanner _scanner = new();
        private readonly FlowTrackerOptions _options;
        public HitWindow Hits { get; } = new();
        public bool RequireServerClock { get; set; }
        public TcpReassembler Reassembler { get; }

        public SignatureDirection(FlowTrackerOptions options)
        {
            _options = options;
            // Detection needs only short signatures. Keep its extra buffering small even with many candidate flows.
            Reassembler = new TcpReassembler(this, options.GapTimeout,
                Math.Min(options.MaxPendingBytes > 0 ? options.MaxPendingBytes : 64 * 1024, 64 * 1024),
                Math.Min(options.MaxPendingSegments > 0 ? options.MaxPendingSegments : 256, 256));
        }

        public void OnData(DateTime timeUtc, ReadOnlySpan<byte> data)
        {
            int count = _scanner.Push(data, RequireServerClock);
            if (count > 0) Hits.Add(timeUtc, count, _options.SignatureWindow);
        }

        public void OnDiscontinuity(DiscontinuityReason reason) => _scanner.Reset();
    }

    private sealed class Candidate(TcpFlowKey key, DateTime firstSeen, FlowTrackerOptions options)
    {
        public TcpFlowKey Key { get; } = key;
        public DateTime FirstSeen { get; } = firstSeen;
        public DateTime LastSeen { get; set; } = firstSeen;
        public int SourceId { get; set; }
        public bool Tls { get; set; }
        public bool PayloadSeen { get; set; }
        public bool Closed { get; set; }
        public uint? SynSeqA { get; set; }
        public uint? SynSeqB { get; set; }
        public long PayloadFromA { get; set; }
        public long PayloadFromB { get; set; }
        public SignatureDirection SignatureFromA { get; } = new(options);
        public SignatureDirection SignatureFromB { get; } = new(options);
        public HitWindow HitsFromA => SignatureFromA.Hits;
        public HitWindow HitsFromB => SignatureFromB.Hits;
        public Queue<Buffered> Buffer { get; } = new();
        public long BufferedBytes { get; set; }

        public long ClearBuffer()
        {
            long n = BufferedBytes;
            Buffer.Clear();
            BufferedBytes = 0;
            return n;
        }
    }
}
