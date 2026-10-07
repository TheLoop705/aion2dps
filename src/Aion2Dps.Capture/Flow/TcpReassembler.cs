using Aion2Dps.Contracts;

namespace Aion2Dps.Capture;

/// <summary>
/// One-direction TCP stream reassembler (PROTOCOL.md §3). Emits contiguous in-order bytes to an <see cref="IStreamSink"/>.
/// Sequence numbers are compared with signed 32-bit differences (wraparound safe). Retransmits and overlaps are trimmed,
/// out-of-order segments are held (the longest per sequence number is kept) and drained when the hole fills. A hole that
/// persists longer than the gap timeout (capture time) or more than <c>maxPendingBytes</c> of held data makes the
/// reassembler skip to the smallest held sequence number and call <see cref="IStreamSink.OnDiscontinuity"/> with
/// <see cref="DiscontinuityReason.TcpGap"/> before the bytes after the gap.
/// Not thread-safe.
/// </summary>
public sealed class TcpReassembler
{
    public const int DefaultMaxPendingBytes = 2 * 1024 * 1024;
    public const int DefaultMaxPendingSegments = 4096;
    public static readonly TimeSpan DefaultGapTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly IStreamSink _output;
    private readonly TimeSpan _gapTimeout;
    private readonly int _maxPendingBytes;
    private readonly int _maxPendingSegments;
    private readonly Dictionary<uint, byte[]> _pending = new();
    private bool _hasNext;
    private uint _next;
    private int _pendingBytes;
    private DateTime _holeSince;

    public TcpReassembler(IStreamSink output, TimeSpan? gapTimeout = null, int maxPendingBytes = DefaultMaxPendingBytes,
        int maxPendingSegments = DefaultMaxPendingSegments)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _gapTimeout = gapTimeout ?? DefaultGapTimeout;
        _maxPendingBytes = maxPendingBytes > 0 ? maxPendingBytes : DefaultMaxPendingBytes;
        _maxPendingSegments = maxPendingSegments > 0 ? maxPendingSegments : DefaultMaxPendingSegments;
    }

    /// <summary>The next expected sequence number, or null before the first data segment.</summary>
    public uint? NextSequence => _hasNext ? _next : null;
    public int PendingSegments => _pending.Count;
    public int PendingBytes => _pendingBytes;
    public long BytesDelivered { get; private set; }
    public long GapCount { get; private set; }
    /// <summary>Bytes jumped over by gap give-ups (lost data).</summary>
    public long SkippedBytes { get; private set; }
    /// <summary>Bytes dropped because they had already been delivered or held (retransmits, overlaps).</summary>
    public long DuplicateBytes { get; private set; }
    public long OutOfOrderSegments { get; private set; }

    /// <summary>Sets the next expected sequence number before any data (e.g. SYN seq + 1, or the smallest buffered seq).
    /// Ignored once data has been delivered or seeded.</summary>
    public void Seed(uint nextSequence)
    {
        if (_hasNext) return;
        _next = nextSequence;
        _hasNext = true;
    }

    /// <summary>Feeds one segment. <paramref name="timeUtc"/> is its capture timestamp.</summary>
    public void Push(uint seq, ReadOnlySpan<byte> data, DateTime timeUtc)
    {
        if (data.IsEmpty)
        {
            CheckGap(timeUtc);
            return;
        }
        if (!_hasNext)
        {
            _next = seq; // the first segment seeds; a mid-stream start is normal
            _hasNext = true;
        }

        int d = unchecked((int)(seq - _next));
        if (d < 0)
        {
            long behind = -(long)d;
            if (behind >= data.Length)
            {
                DuplicateBytes += data.Length; // fully old
                CheckGap(timeUtc);
                return;
            }
            DuplicateBytes += behind;
            data = data.Slice((int)behind);
            seq = _next;
            d = 0;
        }

        if (d > 0)
        {
            Hold(seq, data, timeUtc);
            CheckGap(timeUtc);
            return;
        }

        Emit(data, timeUtc);
        if (_pending.Count > 0)
        {
            Drain(timeUtc);
            if (_pending.Count > 0) _holeSince = timeUtc; // progress was made; the remaining hole starts now
        }
        CheckGap(timeUtc);
    }

    /// <summary>Gives up on a hole older than the gap timeout (call periodically with the capture clock).</summary>
    public void Tick(DateTime nowUtc) => CheckGap(nowUtc);

    /// <summary>Delivers everything still held, skipping every hole (end of a replay).</summary>
    public void Flush(DateTime nowUtc)
    {
        while (_pending.Count > 0) GiveUp(nowUtc);
    }

    /// <summary>Forgets all state (the next segment seeds again).</summary>
    public void Reset()
    {
        _pending.Clear();
        _pendingBytes = 0;
        _hasNext = false;
        _next = 0;
    }

    private void Hold(uint seq, ReadOnlySpan<byte> data, DateTime timeUtc)
    {
        OutOfOrderSegments++;
        if (_pending.Count == 0) _holeSince = timeUtc;
        if (_pending.TryGetValue(seq, out var existing))
        {
            if (existing.Length >= data.Length)
            {
                DuplicateBytes += data.Length;
                return;
            }
            DuplicateBytes += existing.Length;
            _pendingBytes -= existing.Length;
        }
        _pending[seq] = data.ToArray();
        _pendingBytes += data.Length;
    }

    private void Emit(ReadOnlySpan<byte> data, DateTime timeUtc)
    {
        _next = unchecked(_next + (uint)data.Length);
        BytesDelivered += data.Length;
        _output.OnData(timeUtc, data);
    }

    private void Drain(DateTime timeUtc)
    {
        while (_pending.Count > 0)
        {
            if (_pending.Remove(_next, out var exact))
            {
                _pendingBytes -= exact.Length;
                Emit(exact, timeUtc);
                continue;
            }

            // Any held segment that starts before _next overlaps already delivered bytes: trim it.
            bool found = false;
            uint key = 0;
            foreach (var k in _pending.Keys)
            {
                if (unchecked((int)(k - _next)) < 0)
                {
                    key = k;
                    found = true;
                    break;
                }
            }
            if (!found) return;

            _pending.Remove(key, out var seg);
            _pendingBytes -= seg!.Length;
            long behind = -(long)unchecked((int)(key - _next));
            if (behind >= seg.Length)
            {
                DuplicateBytes += seg.Length;
                continue;
            }
            DuplicateBytes += behind;
            Emit(seg.AsSpan((int)behind), timeUtc);
        }
    }

    private void CheckGap(DateTime nowUtc)
    {
        while (_pending.Count > 0 && (_pendingBytes > _maxPendingBytes || _pending.Count > _maxPendingSegments ||
                                     nowUtc - _holeSince > _gapTimeout))
            GiveUp(nowUtc);
    }

    private void GiveUp(DateTime nowUtc)
    {
        if (_pending.Count == 0) return;
        // After Drain, every held key is ahead of _next. Jump to the smallest one.
        uint best = 0;
        long bestDistance = long.MaxValue;
        foreach (var k in _pending.Keys)
        {
            long dist = unchecked((int)(k - _next));
            if (dist < bestDistance)
            {
                bestDistance = dist;
                best = k;
            }
        }
        if (bestDistance > 0) SkippedBytes += bestDistance;
        _next = best;
        GapCount++;
        _output.OnDiscontinuity(DiscontinuityReason.TcpGap);
        Drain(nowUtc);
        _holeSince = nowUtc;
    }
}
