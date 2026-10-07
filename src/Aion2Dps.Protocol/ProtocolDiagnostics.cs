using Aion2Dps.Contracts;

namespace Aion2Dps.Protocol;

/// <summary>
/// Thread-safe counters, a per-opcode census and a ring buffer of recent errors, shared by
/// <see cref="FrameDecoder"/> and <see cref="PacketDecoder"/>. Writers are the capture thread; readers may be any
/// thread (UI, CLI).
/// </summary>
public sealed class ProtocolDiagnostics : IProtocolDiagnostics
{
    private sealed class OpcodeCounter
    {
        public long Count;
        public long Bytes;
        public long Decoded;
        public long Failed;
    }

    private readonly OpcodeCounter?[] _census = new OpcodeCounter?[65536];
    private readonly object _errorLock = new();
    private readonly string[] _errors;
    private int _errorNext;
    private int _errorCount;

    private long _bytesIn, _frames, _bundles, _bundleErrors, _resyncs, _decodeErrors, _eventsEmitted, _lastFrameTicks;
    private long _invalidFrames, _resyncSkippedBytes, _paddingBytes, _extraByteFrames, _tlsRecordsSkipped;
    private long _embeddedBundles, _embeddedFramesForwarded, _embeddedFramesIgnored, _bundleInnerErrors;
    private long _sinkErrors, _damageRecords, _damageTrailingBytes, _unhandledFrames, _damagePlaceholders, _dotTriggerTicks;

    /// <param name="recentErrorCapacity">How many recent error lines to keep (default 64).</param>
    public ProtocolDiagnostics(int recentErrorCapacity = 64)
    {
        _errors = new string[Math.Max(1, recentErrorCapacity)];
    }

    // ───────────── IProtocolDiagnostics ─────────────

    public long BytesIn => Interlocked.Read(ref _bytesIn);
    /// <summary>Frames delivered to the frame sink (top level, inside bundles, and forwarded embedded frames).</summary>
    public long Frames => Interlocked.Read(ref _frames);
    /// <summary>LZ4 bundles successfully unpacked (all depths, excluding embedded ones).</summary>
    public long Bundles => Interlocked.Read(ref _bundles);
    /// <summary>Bundles that could not be unpacked (bad rawSize, LZ4 error, too deep) or whose inner walk stopped early.</summary>
    public long BundleErrors => Interlocked.Read(ref _bundleErrors);
    /// <summary>Times the framer lost alignment (invalid length or discontinuity) and had to resynchronise.</summary>
    public long Resyncs => Interlocked.Read(ref _resyncs);
    /// <summary>Frames of a handled opcode that failed to decode (or threw).</summary>
    public long DecodeErrors => Interlocked.Read(ref _decodeErrors);
    public long EventsEmitted => Interlocked.Read(ref _eventsEmitted);

    public DateTime? LastFrameUtc
    {
        get
        {
            long t = Interlocked.Read(ref _lastFrameTicks);
            return t == 0 ? null : new DateTime(t, DateTimeKind.Utc);
        }
    }

    // ───────────── extra counters ─────────────

    /// <summary>Invalid length prefixes met on the top-level stream.</summary>
    public long InvalidFrames => Interlocked.Read(ref _invalidFrames);
    /// <summary>Bytes discarded while searching for alignment.</summary>
    public long ResyncSkippedBytes => Interlocked.Read(ref _resyncSkippedBytes);
    /// <summary>0x00 inter-frame padding bytes skipped.</summary>
    public long PaddingBytes => Interlocked.Read(ref _paddingBytes);
    /// <summary>Frames carrying the legacy F0..FE byte before the opcode (§4.5).</summary>
    public long ExtraByteFrames => Interlocked.Read(ref _extraByteFrames);
    /// <summary>TLS records skipped at frame boundaries (§4.6, defensive).</summary>
    public long TlsRecordsSkipped => Interlocked.Read(ref _tlsRecordsSkipped);
    /// <summary>Bundles found embedded inside other frames' bodies (§5, diagnostic).</summary>
    public long EmbeddedBundles => Interlocked.Read(ref _embeddedBundles);
    /// <summary>Identity frames (<c>33 36</c>) from embedded bundles forwarded to the frame sink.</summary>
    public long EmbeddedFramesForwarded => Interlocked.Read(ref _embeddedFramesForwarded);
    /// <summary>Other frames seen inside embedded bundles (not forwarded; e.g. embedded damage records).</summary>
    public long EmbeddedFramesIgnored => Interlocked.Read(ref _embeddedFramesIgnored);
    /// <summary>Malformed inner frames that stopped a bundle walk (subset of <see cref="BundleErrors"/>).</summary>
    public long BundleInnerErrors => Interlocked.Read(ref _bundleInnerErrors);
    /// <summary>Exceptions thrown by downstream sinks (caught).</summary>
    public long SinkErrors => Interlocked.Read(ref _sinkErrors);
    /// <summary>Damage records decoded (a frame may chain several).</summary>
    public long DamageRecords => Interlocked.Read(ref _damageRecords);
    /// <summary>Damage frames that left bytes after the last record that could be validated.</summary>
    public long DamageTrailingBytes => Interlocked.Read(ref _damageTrailingBytes);
    /// <summary>Frames whose opcode has no decoder.</summary>
    public long UnhandledFrames => Interlocked.Read(ref _unhandledFrames);
    /// <summary>Well-formed <c>04 38</c> records not emitted: the self-targeted 500,000,000 "restore to full HP"
    /// placeholder of instanced NPCs whose max HP was scaled (PROTOCOL.md §8.2.2).</summary>
    public long DamagePlaceholders => Interlocked.Read(ref _damagePlaceholders);
    /// <summary><c>05 38</c> trigger notices (flags 0x30: a buff of the target negated or reacted to a source entity's
    /// skill, e.g. a dodge window; no amount, PROTOCOL.md §8.3) decoded but not emitted.</summary>
    public long DotTriggerTicks => Interlocked.Read(ref _dotTriggerTicks);

    // ───────────── census / errors ─────────────

    public IReadOnlyList<OpcodeStat> GetCensus()
    {
        var list = new List<OpcodeStat>();
        for (int op = 0; op < _census.Length; op++)
        {
            var c = Volatile.Read(ref _census[op]);
            if (c is null) continue;
            list.Add(new OpcodeStat((ushort)op, Interlocked.Read(ref c.Count), Interlocked.Read(ref c.Bytes),
                Interlocked.Read(ref c.Decoded), Interlocked.Read(ref c.Failed)));
        }

        list.Sort((a, b) => b.Count.CompareTo(a.Count));
        return list;
    }

    /// <summary>Census entry of one opcode (zeros if never seen).</summary>
    public OpcodeStat GetStat(ushort opcode)
    {
        var c = Volatile.Read(ref _census[opcode]);
        return c is null
            ? new OpcodeStat(opcode, 0, 0, 0, 0)
            : new OpcodeStat(opcode, Interlocked.Read(ref c.Count), Interlocked.Read(ref c.Bytes),
                Interlocked.Read(ref c.Decoded), Interlocked.Read(ref c.Failed));
    }

    /// <summary>Recent errors, oldest first.</summary>
    public IReadOnlyList<string> GetRecentErrors()
    {
        lock (_errorLock)
        {
            var result = new string[_errorCount];
            int start = (_errorNext - _errorCount + _errors.Length) % _errors.Length;
            for (int i = 0; i < _errorCount; i++) result[i] = _errors[(start + i) % _errors.Length];
            return result;
        }
    }

    /// <summary>Clears every counter, the census and the error ring.</summary>
    public void Reset()
    {
        for (int i = 0; i < _census.Length; i++) Volatile.Write(ref _census[i], null);
        lock (_errorLock)
        {
            Array.Clear(_errors);
            _errorNext = 0;
            _errorCount = 0;
        }

        Interlocked.Exchange(ref _bytesIn, 0); Interlocked.Exchange(ref _frames, 0); Interlocked.Exchange(ref _bundles, 0);
        Interlocked.Exchange(ref _bundleErrors, 0); Interlocked.Exchange(ref _resyncs, 0); Interlocked.Exchange(ref _decodeErrors, 0);
        Interlocked.Exchange(ref _eventsEmitted, 0); Interlocked.Exchange(ref _lastFrameTicks, 0);
        Interlocked.Exchange(ref _invalidFrames, 0); Interlocked.Exchange(ref _resyncSkippedBytes, 0);
        Interlocked.Exchange(ref _paddingBytes, 0); Interlocked.Exchange(ref _extraByteFrames, 0);
        Interlocked.Exchange(ref _tlsRecordsSkipped, 0); Interlocked.Exchange(ref _embeddedBundles, 0);
        Interlocked.Exchange(ref _embeddedFramesForwarded, 0); Interlocked.Exchange(ref _embeddedFramesIgnored, 0);
        Interlocked.Exchange(ref _bundleInnerErrors, 0); Interlocked.Exchange(ref _sinkErrors, 0);
        Interlocked.Exchange(ref _damageRecords, 0); Interlocked.Exchange(ref _damageTrailingBytes, 0);
        Interlocked.Exchange(ref _unhandledFrames, 0); Interlocked.Exchange(ref _damagePlaceholders, 0);
        Interlocked.Exchange(ref _dotTriggerTicks, 0);
    }

    // ───────────── writers (used by the decoders) ─────────────

    public void AddBytesIn(long n) => Interlocked.Add(ref _bytesIn, n);
    public void AddPadding(long n) => Interlocked.Add(ref _paddingBytes, n);
    public void AddResyncSkipped(long n) => Interlocked.Add(ref _resyncSkippedBytes, n);
    public void IncrementResyncs() => Interlocked.Increment(ref _resyncs);
    public void IncrementInvalidFrames() => Interlocked.Increment(ref _invalidFrames);
    public void IncrementBundles() => Interlocked.Increment(ref _bundles);
    public void IncrementBundleErrors() => Interlocked.Increment(ref _bundleErrors);
    public void IncrementBundleInnerErrors() => Interlocked.Increment(ref _bundleInnerErrors);
    public void IncrementExtraByteFrames() => Interlocked.Increment(ref _extraByteFrames);
    public void IncrementTlsRecordsSkipped() => Interlocked.Increment(ref _tlsRecordsSkipped);
    public void IncrementEmbeddedBundles() => Interlocked.Increment(ref _embeddedBundles);
    public void IncrementEmbeddedForwarded() => Interlocked.Increment(ref _embeddedFramesForwarded);
    public void IncrementEmbeddedIgnored() => Interlocked.Increment(ref _embeddedFramesIgnored);
    public void IncrementSinkErrors() => Interlocked.Increment(ref _sinkErrors);
    public void IncrementEvents() => Interlocked.Increment(ref _eventsEmitted);
    public void IncrementDamageRecords() => Interlocked.Increment(ref _damageRecords);
    public void IncrementDamageTrailingBytes() => Interlocked.Increment(ref _damageTrailingBytes);
    public void IncrementUnhandled() => Interlocked.Increment(ref _unhandledFrames);
    public void IncrementDamagePlaceholders() => Interlocked.Increment(ref _damagePlaceholders);
    public void IncrementDotTriggerTicks() => Interlocked.Increment(ref _dotTriggerTicks);

    /// <summary>A frame (or a bundle frame) was seen: census count/bytes, and the last-frame time.</summary>
    public void RecordFrame(ushort opcode, int bodyBytes, DateTime timeUtc, bool delivered)
    {
        var c = GetCounter(opcode);
        Interlocked.Increment(ref c.Count);
        Interlocked.Add(ref c.Bytes, bodyBytes);
        if (delivered) Interlocked.Increment(ref _frames);
        long ticks = timeUtc.Kind == DateTimeKind.Local ? timeUtc.ToUniversalTime().Ticks : timeUtc.Ticks;
        Interlocked.Exchange(ref _lastFrameTicks, ticks);
    }

    public void RecordDecoded(ushort opcode) => Interlocked.Increment(ref GetCounter(opcode).Decoded);

    public void RecordFailed(ushort opcode)
    {
        Interlocked.Increment(ref GetCounter(opcode).Failed);
        Interlocked.Increment(ref _decodeErrors);
    }

    /// <summary>Appends a line to the recent-error ring (and logs it at debug level).</summary>
    public void RecordError(DateTime timeUtc, string message)
    {
        string line = $"{timeUtc:HH:mm:ss.fff} {message}";
        lock (_errorLock)
        {
            _errors[_errorNext] = line;
            _errorNext = (_errorNext + 1) % _errors.Length;
            if (_errorCount < _errors.Length) _errorCount++;
        }

        AppLog.Debug("Protocol", line);
    }

    private OpcodeCounter GetCounter(ushort opcode)
    {
        var c = Volatile.Read(ref _census[opcode]);
        if (c is not null) return c;
        var fresh = new OpcodeCounter();
        return Interlocked.CompareExchange(ref _census[opcode], fresh, null) ?? fresh;
    }
}
