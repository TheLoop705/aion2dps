using Aion2Dps.Contracts;

namespace Aion2Dps.Capture;

/// <summary>Tuning of <see cref="GameFlowTracker"/>. Defaults follow PROTOCOL.md §2 and §3.</summary>
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
    /// <summary>Total pre-lock buffer over all candidate flows.</summary>
    public int MaxTotalBufferedBytes { get; set; } = 32 * 1024 * 1024;
    public int MaxCandidateFlows { get; set; } = 1024;
    /// <summary>Candidate flows without packets for this long are forgotten.</summary>
    public TimeSpan CandidateIdleTimeout { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>Another qualifying flow replaces the locked one only when the locked flow is closed (FIN/RST) or has
    /// been silent this long, or when the new flow is a hinted flow and the locked one is not.</summary>
    public TimeSpan SwitchAfterSilence { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan GapTimeout { get; set; } = TcpReassembler.DefaultGapTimeout;
    public int MaxPendingBytes { get; set; } = TcpReassembler.DefaultMaxPendingBytes;
}

/// <summary>Describes the locked game connection.</summary>
/// <param name="Server">Game server endpoint (sender of the delivered bytes).</param>
/// <param name="Client">Local endpoint.</param>
/// <param name="SourceId">Id of the capture source (adapter) that delivered the qualifying packets.</param>
/// <param name="LockedAtUtc">Capture time of the lock.</param>
/// <param name="ByHint">True when the flow was named by the game process's connection table.</param>
/// <param name="LockNumber">1 for the first lock of the tracker, 2 after the first reconnect, ...</param>
public sealed record FlowLockInfo(IPv4Endpoint Server, IPv4Endpoint Client, int SourceId, DateTime LockedAtUtc, bool ByHint, int LockNumber)
{
    public TcpFlowKey Key => TcpFlowKey.Create(Server, Client);
    public override string ToString() => $"{Server} -> {Client}";
}

/// <summary>
/// Finds the game connection among TCP flows and reassembles its server→client direction into the sink.
/// <list type="bullet">
/// <item>Candidate flows are scored by the heartbeat signature <c>0E 00 36</c>+8 (or legacy <c>06 00 36</c>) in
/// payloads of one direction: ≥ 3 hits within 3 s when the sender's port is 13328, ≥ 12 otherwise, ≥ 1 for a flow named by
/// <see cref="SetHints"/>. Flows whose first payload looks like TLS are ignored.</item>
/// <item>The first segments of every candidate are buffered and replayed after the lock, so identity records sent at
/// connect are not lost.</item>
/// <item>Locking a different flow after an earlier lock emits <see cref="DiscontinuityReason.NewConnection"/>.</item>
/// </list>
/// Not thread-safe: the caller serialises all calls (the live service holds a lock, replay is single-threaded).
/// </summary>
public sealed class GameFlowTracker
{
    private readonly FlowTrackerOptions _options;
    private readonly StreamSinkGuard _sink;
    private readonly Dictionary<TcpFlowKey, Candidate> _candidates = new();
    private readonly Dictionary<TcpFlowKey, FlowHint> _hints = new();
    private LockedFlow? _locked;
    private long _totalBuffered;
    private bool _everLocked;
    private DateTime _lastEviction;
    private long _recorderErrors;

    public GameFlowTracker(IStreamSink sink, FlowTrackerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink as StreamSinkGuard ?? new StreamSinkGuard(sink);
        _options = options ?? new FlowTrackerOptions();
    }

    public FlowTrackerOptions Options => _options;

    /// <summary>Receives every raw frame of the locked flow, both directions (including the pre-lock buffer).</summary>
    public IPacketRecorder? Recorder { get; set; }

    /// <summary>The locked flow, or null while detecting.</summary>
    public FlowLockInfo? CurrentLock => _locked?.Info;

    /// <summary>Capture time of the last packet of the locked flow.</summary>
    public DateTime? LastLockedPacketUtc { get; private set; }

    /// <summary>The locked flow has seen FIN or RST.</summary>
    public bool LockedFlowClosed => _locked?.Closed ?? false;

    public long PacketsSeen { get; private set; }
    public long TcpPackets { get; private set; }
    public long LockedFlowPackets { get; private set; }
    public long BytesDelivered => _sink.BytesDelivered;
    public long GapCount => _sink.GapCount;
    public int LockCount { get; private set; }
    public long TlsFlowsIgnored { get; private set; }
    public int CandidateCount => _candidates.Count;
    public long BufferedBytes => _totalBuffered;
    /// <summary>Bytes skipped by gap give-ups of the current lock.</summary>
    public long SkippedBytes => _locked?.Reassembler.SkippedBytes ?? 0;

    /// <summary>Raised (inside the caller's lock) after a flow has been locked and its buffer replayed.</summary>
    public event Action<FlowLockInfo>? Locked;

    /// <summary>Raised when the lock is dropped (watchdog, replaced by a new connection, ...).</summary>
    public event Action<FlowLockInfo, string>? Unlocked;

    /// <summary>Game connections known from the process connection table. Such flows lock on the first heartbeat.</summary>
    public void SetHints(IEnumerable<FlowHint>? hints)
    {
        _hints.Clear();
        if (hints is null) return;
        foreach (var h in hints) _hints[h.Key] = h;
    }

    public IReadOnlyCollection<FlowHint> Hints => _hints.Values;

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
        if (_locked is not null && _locked.Key == key)
        {
            OnLockedSegment(timeUtc, segment, linkType, frame);
            return;
        }
        OnCandidateSegment(timeUtc, key, segment, sourceId, linkType, frame);
        if (timeUtc - _lastEviction > TimeSpan.FromSeconds(1)) Evict(timeUtc);
    }

    /// <summary>Drives gap give-up and candidate eviction with the capture clock.</summary>
    public void Tick(DateTime nowUtc)
    {
        _locked?.Reassembler.Tick(nowUtc);
        if (nowUtc - _lastEviction > TimeSpan.FromSeconds(1)) Evict(nowUtc);
    }

    /// <summary>Delivers all bytes still held by the reassembler, skipping holes (end of a replay).</summary>
    public void Flush(DateTime nowUtc) => _locked?.Reassembler.Flush(nowUtc);

    /// <summary>Drops the current lock and returns to detection. The next lock emits NewConnection.</summary>
    public void Unlock(string reason)
    {
        var old = _locked;
        if (old is null) return;
        _locked = null;
        AppLog.Info("Capture", $"Unlocked game flow {old.Info}: {reason}");
        Unlocked?.Invoke(old.Info, reason);
    }

    /// <summary>End-of-file fallback for replays: when nothing was ever locked, lock the best remaining candidate that
    /// uses the game port or carries at least one heartbeat. Returns true if a flow was locked.</summary>
    public bool TryFallbackLock(DateTime nowUtc)
    {
        if (_locked is not null || LockCount > 0) return false;
        Candidate? best = null;
        IPv4Endpoint bestServer = default;
        long bestScore = -1;
        foreach (var c in _candidates.Values)
        {
            if (c.Tls) continue;
            IPv4Endpoint server;
            if (c.Key.A.Port == _options.GameServerPort && c.Key.B.Port != _options.GameServerPort) server = c.Key.A;
            else if (c.Key.B.Port == _options.GameServerPort && c.Key.A.Port != _options.GameServerPort) server = c.Key.B;
            else if (c.HitsFromA.Total > 0 || c.HitsFromB.Total > 0) server = c.HitsFromA.Total >= c.HitsFromB.Total ? c.Key.A : c.Key.B;
            else continue;
            long payload = server == c.Key.A ? c.PayloadFromA : c.PayloadFromB;
            if (payload == 0) continue;
            long hits = server == c.Key.A ? c.HitsFromA.Total : c.HitsFromB.Total;
            long score = hits * 1_000_000_000L + payload;
            if (score > bestScore)
            {
                bestScore = score;
                best = c;
                bestServer = server;
            }
        }
        if (best is null) return false;
        Lock(best, bestServer, nowUtc, _hints.ContainsKey(best.Key));
        return true;
    }

    // ───────────────────────────── locked flow ─────────────────────────────

    private void OnLockedSegment(DateTime timeUtc, in TcpSegment segment, int linkType, ReadOnlySpan<byte> frame)
    {
        var locked = _locked!;
        LockedFlowPackets++;
        LastLockedPacketUtc = timeUtc;
        if (!frame.IsEmpty && linkType >= 0) Record(timeUtc, linkType, frame);
        if ((segment.Flags & (TcpFlags.Fin | TcpFlags.Rst)) != 0) locked.Closed = true;
        if (segment.Source != locked.Server) return;

        if ((segment.Flags & TcpFlags.Syn) != 0 && locked.ServerIsn != segment.Sequence)
        {
            // A new connection on the same 4-tuple (SYN-ACK from the server with a new ISN): restart the stream.
            if (locked.Reassembler.NextSequence is not null)
            {
                locked.Reassembler.Reset();
                _sink.OnDiscontinuity(DiscontinuityReason.NewConnection);
            }
            locked.ServerIsn = segment.Sequence;
            locked.Reassembler.Seed(unchecked(segment.Sequence + 1));
            locked.Closed = false;
        }
        locked.Reassembler.Push(segment.Sequence, segment.Payload, timeUtc);
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
            c = new Candidate(key, timeUtc);
            _candidates[key] = c;
        }
        c.LastSeen = timeUtc;
        c.SourceId = sourceId;
        if (c.Tls) return;

        var payload = segment.Payload;
        bool fromA = segment.Source == key.A;
        if ((segment.Flags & (TcpFlags.Fin | TcpFlags.Rst)) != 0) c.Closed = true;
        if ((segment.Flags & TcpFlags.Syn) != 0)
        {
            if (fromA) c.SynSeqA = segment.Sequence; else c.SynSeqB = segment.Sequence;
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

        int hits = GameSignature.CountHeartbeats(payload);
        if (hits == 0) return;
        var window = fromA ? c.HitsFromA : c.HitsFromB;
        window.Add(timeUtc, hits, _options.SignatureWindow);

        int threshold = ThresholdFor(key, segment.Source);
        if (window.InWindow >= threshold) TryQualify(c, segment.Source, timeUtc);
    }

    private int ThresholdFor(TcpFlowKey key, IPv4Endpoint sender)
    {
        if (_hints.TryGetValue(key, out var hint))
            return sender == hint.Server ? Math.Max(1, _options.HintedHits) : int.MaxValue;
        return sender.Port == _options.GameServerPort ? _options.GamePortHits : _options.OtherPortHits;
    }

    private void TryQualify(Candidate c, IPv4Endpoint server, DateTime timeUtc)
    {
        bool hinted = _hints.ContainsKey(c.Key);
        if (_locked is not null)
        {
            bool lockedHinted = _hints.ContainsKey(_locked.Key);
            bool silent = LastLockedPacketUtc is { } last && timeUtc - last >= _options.SwitchAfterSilence;
            if (!(_locked.Closed || silent || (hinted && !lockedHinted))) return;
        }
        Lock(c, server, timeUtc, hinted);
    }

    private void Lock(Candidate c, IPv4Endpoint server, DateTime timeUtc, bool hinted)
    {
        var previous = _locked;
        if (previous is not null)
        {
            _locked = null;
            AppLog.Info("Capture", $"Game flow {previous.Info} replaced by {server} <-> {c.Key.Other(server)}");
            Unlocked?.Invoke(previous.Info, "replaced by a new connection");
        }

        _candidates.Remove(c.Key);
        _totalBuffered -= c.BufferedBytes;
        LockCount++;
        var info = new FlowLockInfo(server, c.Key.Other(server), c.SourceId, timeUtc, hinted, LockCount);
        var reassembler = new TcpReassembler(_sink, _options.GapTimeout, _options.MaxPendingBytes);
        _locked = new LockedFlow(info, c.Key, server, reassembler) { Closed = c.Closed };
        LastLockedPacketUtc = c.LastSeen;

        if (_everLocked) _sink.OnDiscontinuity(DiscontinuityReason.NewConnection);
        _everLocked = true;
        AppLog.Info("Capture", $"Locked game flow {info} (source {c.SourceId}, {(hinted ? "process hint" : "heartbeat signature")}, " +
                               $"{c.Buffer.Count} buffered packets)");

        // Seed with the server's SYN, else with the smallest buffered server sequence number.
        uint? synSeq = server == c.Key.A ? c.SynSeqA : c.SynSeqB;
        _locked.ServerIsn = synSeq;
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
            b = new Buffered(timeUtc, segment.Source, segment.Sequence, frame.ToArray(), segment.PayloadOffset, segment.Payload.Length, linkType, true);
        else
            b = new Buffered(timeUtc, segment.Source, segment.Sequence, segment.Payload.ToArray(), 0, segment.Payload.Length, -1, false);
        c.Buffer.Enqueue(b);
        c.BufferedBytes += b.Data.Length;
        _totalBuffered += b.Data.Length;

        while (c.BufferedBytes > _options.MaxBufferedBytesPerFlow && c.Buffer.Count > 1)
        {
            var old = c.Buffer.Dequeue();
            c.BufferedBytes -= old.Data.Length;
            _totalBuffered -= old.Data.Length;
        }

        if (_totalBuffered > _options.MaxTotalBufferedBytes)
        {
            Candidate? largest = null;
            foreach (var other in _candidates.Values)
                if (other != c && (largest is null || other.BufferedBytes > largest.BufferedBytes)) largest = other;
            if (largest is not null) _totalBuffered -= largest.ClearBuffer();
        }
    }

    private void Evict(DateTime nowUtc)
    {
        _lastEviction = nowUtc;
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

    private sealed class LockedFlow(FlowLockInfo info, TcpFlowKey key, IPv4Endpoint server, TcpReassembler reassembler)
    {
        public FlowLockInfo Info { get; } = info;
        public TcpFlowKey Key { get; } = key;
        public IPv4Endpoint Server { get; } = server;
        public TcpReassembler Reassembler { get; } = reassembler;
        public bool Closed { get; set; }
        public uint? ServerIsn { get; set; }
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
            while (_hits.Count > 0 && timeUtc - _hits.Peek().Time > window)
                InWindow -= _hits.Dequeue().Count;
        }
    }

    private sealed class Candidate(TcpFlowKey key, DateTime firstSeen)
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
        public HitWindow HitsFromA { get; } = new();
        public HitWindow HitsFromB { get; } = new();
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
