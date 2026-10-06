using System.Buffers.Binary;
using Aion2Dps.Contracts;

namespace Aion2Dps.Protocol;

/// <summary>
/// Splits the reassembled server→client byte stream into frames (PROTOCOL.md §4), unpacks LZ4 bundles (§5) and
/// emits <see cref="Frame"/>s to an <see cref="IFrameSink"/>.
/// <list type="bullet">
/// <item>frame := L:varint [F0..FE] opcode(2) body; total bytes = L + width(L) − 4; 0x00 padding between frames.</item>
/// <item>Bytes are buffered across <see cref="OnData"/> calls; emitted bodies are slices of the internal buffer
/// (no per-frame copies or allocations) and are only valid during <see cref="IFrameSink.OnFrame"/>.</item>
/// <item><c>FF FF rawSize:u32 lz4block</c> bundles are decompressed into reusable per-depth buffers and walked with the
/// same framing (depth ≤ <see cref="FrameDecoderOptions.MaxBundleDepth"/>, no resync inside a bundle).</item>
/// <item>After a bad length or a discontinuity the decoder resynchronises (§4.4): an offset where
/// <see cref="FrameDecoderOptions.ResyncCleanFrames"/> consecutive frames parse with known opcodes, then a heartbeat
/// signature, then any parsable frame.</item>
/// </list>
/// Not thread-safe: call from one thread (the capture thread). Never throws out of <see cref="OnData"/>.
/// </summary>
public sealed class FrameDecoder : IStreamSink
{
    private enum Parse : byte { Ok, NeedMore, Invalid }

    private enum Candidate : byte { Yes, No, NeedMore }

    private struct FrameInfo
    {
        public int Total;       // varint + payload
        public int BodyOffset;  // offset of the body (after varint, optional extra byte and opcode)
        public ushort Opcode;
        public bool OpcodeAvailable;
        public bool ExtraByte;
        public bool IsBundle;
    }

    private const int InitialBufferSize = 64 * 1024;

    private readonly IFrameSink _sink;
    private readonly OpcodeTable _ops;
    private readonly ProtocolDiagnostics _diag;
    private readonly FrameDecoderOptions _opt;
    private readonly byte[]?[] _scratch;
    private byte[] _embeddedScratch = Array.Empty<byte>();

    private byte[] _buf = new byte[InitialBufferSize];
    private int _len;
    private bool _resync;
    private bool _resyncFresh;      // resync after a clean start: an aligned first frame may be accepted early
    private long _resyncSkipped;    // bytes skipped in the current resync episode
    private DateTime _time;

    /// <param name="sink">Receives the frames.</param>
    /// <param name="opcodes">Opcode table (resync census, heartbeat signature, identity opcode). Default: <see cref="OpcodeTable.Default"/>.</param>
    /// <param name="diagnostics">Counters to update (shared with the packet decoder). Default: a new instance.</param>
    /// <param name="options">Framing options. Default: <see cref="FrameDecoderOptions"/> defaults.</param>
    public FrameDecoder(IFrameSink sink, OpcodeTable? opcodes = null, ProtocolDiagnostics? diagnostics = null, FrameDecoderOptions? options = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _ops = opcodes ?? OpcodeTable.Default;
        _diag = diagnostics ?? new ProtocolDiagnostics();
        _opt = options ?? new FrameDecoderOptions();
        _scratch = new byte[]?[Math.Max(1, _opt.MaxBundleDepth) + 2];
        _resync = !_opt.StartAligned;
        _resyncFresh = true;
    }

    public ProtocolDiagnostics Diagnostics => _diag;
    public OpcodeTable Opcodes => _ops;
    public FrameDecoderOptions Options => _opt;

    /// <summary>True while the decoder trusts its frame alignment (false while searching for it).</summary>
    public bool IsSynchronized => !_resync;

    /// <summary>Bytes held back waiting for the rest of a frame (or for resync lookahead).</summary>
    public int BufferedBytes => _len;

    /// <inheritdoc />
    public void OnData(DateTime timeUtc, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        _diag.AddBytesIn(data.Length);
        _time = timeUtc;
        try
        {
            Append(data);
            int consumed = Process();
            Compact(consumed);
        }
        catch (Exception ex)
        {
            // Defensive: nothing above should throw. Drop state and resync rather than break the pipeline.
            _diag.RecordError(timeUtc, $"framer exception, buffer dropped: {ex.GetType().Name}: {ex.Message}");
            _diag.AddResyncSkipped(_len);
            _len = 0;
            EnterResync(countIt: true, fresh: false);
        }
    }

    /// <inheritdoc />
    public void OnDiscontinuity(DiscontinuityReason reason)
    {
        bool hadPartial = _len > 0;
        if (hadPartial) _diag.AddResyncSkipped(_len);
        _len = 0;

        bool clean = reason is DiscontinuityReason.NewConnection or DiscontinuityReason.ReplayStarted or DiscontinuityReason.CaptureRestarted;
        if (clean && _opt.StartAligned)
        {
            _resync = false;
            _resyncSkipped = 0;
            return;
        }

        // A gap mid-stream is a real loss of alignment (new bytes start anywhere: full verification); a clean restart
        // only needs verification, and an aligned first frame may be accepted as soon as it is complete.
        EnterResync(countIt: reason == DiscontinuityReason.TcpGap || hadPartial, fresh: clean);
    }

    /// <summary>Drops buffered bytes and searches for alignment again (same as a TCP gap).</summary>
    public void Reset() => OnDiscontinuity(DiscontinuityReason.TcpGap);

    // ───────────────────────────── buffering ─────────────────────────────

    private void Append(ReadOnlySpan<byte> data)
    {
        int need = _len + data.Length;
        if (need > _buf.Length)
        {
            int size = _buf.Length;
            while (size < need) size = size <= int.MaxValue / 2 ? size * 2 : need;
            var bigger = new byte[size];
            _buf.AsSpan(0, _len).CopyTo(bigger);
            _buf = bigger;
        }

        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;
    }

    private void Compact(int consumed)
    {
        if (consumed <= 0) return;
        int left = _len - consumed;
        if (left > 0) _buf.AsSpan(consumed, left).CopyTo(_buf);
        _len = left;

        // Give back a buffer that grew far beyond what is normally needed.
        if (_buf.Length > 4 * 1024 * 1024 && _len < InitialBufferSize)
        {
            var smaller = new byte[InitialBufferSize];
            _buf.AsSpan(0, _len).CopyTo(smaller);
            _buf = smaller;
        }
    }

    private void EnterResync(bool countIt, bool fresh)
    {
        if (countIt) _diag.IncrementResyncs();
        _resync = true;
        _resyncFresh = fresh;
        _resyncSkipped = 0;
    }

    // ───────────────────────────── top-level walk ─────────────────────────────

    /// <summary>Walks the buffer; returns how many bytes were consumed.</summary>
    private int Process()
    {
        int pos = 0;
        while (pos < _len)
        {
            if (_resync)
            {
                pos = ResyncScan(pos, out bool found);
                if (!found) return pos;
                continue;
            }

            byte b = _buf[pos];
            if (b == 0)
            {
                int run = pos;
                while (run < _len && _buf[run] == 0) run++;
                _diag.AddPadding(run - pos);
                pos = run;
                continue;
            }

            if (_opt.SkipTlsRecords && b is >= 0x14 and <= 0x17)
            {
                int tls = TryTlsRecord(pos);
                if (tls < 0) return pos; // need more bytes to decide
                if (tls > 0)
                {
                    _diag.IncrementTlsRecordsSkipped();
                    pos += tls;
                    continue;
                }
            }

            var span = new ReadOnlySpan<byte>(_buf, pos, _len - pos);
            switch (ParseHeader(span, topLevel: true, out var info))
            {
                case Parse.NeedMore:
                    return pos;
                case Parse.Invalid:
                    _diag.IncrementInvalidFrames();
                    _diag.RecordError(_time, $"invalid frame length at stream offset (+{pos}): {Hex.Format(span, 12)}; resyncing");
                    pos++;
                    _diag.AddResyncSkipped(1);
                    EnterResync(countIt: true, fresh: false);
                    continue;
                default:
                    Dispatch(_buf, pos, in info, depth: 0, embedded: false);
                    pos += info.Total;
                    continue;
            }
        }

        return pos;
    }

    /// <summary>
    /// TLS record header at a frame boundary (<c>14..17 03 00..04 len:u16be</c>). Returns the record size to skip,
    /// 0 when it is not a TLS record, or −1 when more bytes are needed to decide.
    /// </summary>
    private int TryTlsRecord(int pos)
    {
        int avail = _len - pos;
        if (avail < 3) return -1;
        if (_buf[pos + 1] != 0x03 || _buf[pos + 2] > 0x04) return 0;
        if (_ops.IsKnown((ushort)(0x0300 | _buf[pos + 2]))) return 0; // would also be a real frame
        if (avail < 5) return -1;
        int recordLen = (_buf[pos + 3] << 8) | _buf[pos + 4];
        if (recordLen > 16384 + 2048) return 0;
        if (avail < 5 + recordLen) return -1;
        return 5 + recordLen;
    }

    // ───────────────────────────── resync ─────────────────────────────

    /// <summary>
    /// Searches for alignment starting at <paramref name="pos"/>. Returns the new position: the accepted offset (found),
    /// or the earliest offset that is still undecided because it needs more data (not found). Bytes before the returned
    /// position are discarded.
    /// <para>A candidate that needs more data (e.g. garbage that looks like the start of a long frame) does not stop the
    /// scan: a later offset that can already be fully verified wins, so a false candidate cannot stall resync.</para>
    /// </summary>
    private int ResyncScan(int pos, out bool found)
    {
        int k = pos;
        int pending = -1;      // first candidate that needs more data
        long rejected = 0;     // non-padding candidates rejected in this call
        bool firstCandidate = true;
        found = false;
        while (k < _len)
        {
            if (_buf[k] == 0)
            {
                k++; // padding is never a frame start, and does not count as lost data
                continue;
            }

            long skipped = _resyncSkipped + rejected;
            bool allowEarly = _resyncFresh && firstCandidate && skipped == 0;
            firstCandidate = false;
            var c = CheckCandidate(k, skipped, allowEarly);
            if (c == Candidate.Yes)
            {
                found = true;
                if (skipped < _opt.ResyncByteFallbackBytes) k = RefineAlignment(k);
                break;
            }

            if (c == Candidate.NeedMore)
            {
                if (pending < 0) pending = k;
            }
            else
            {
                rejected++;
            }

            k++;
        }

        int newPos = found ? k : pending >= 0 ? pending : _len;
        int discarded = newPos - pos;
        if (discarded > 0)
        {
            long lost = discarded - new ReadOnlySpan<byte>(_buf, pos, discarded).Count((byte)0);
            if (lost > 0)
            {
                _diag.AddResyncSkipped(lost);
                _resyncSkipped += lost;
                _resyncFresh = false;
            }
        }

        if (found)
        {
            _resync = false;
            _resyncSkipped = 0;
            _resyncFresh = false;
        }

        return newPos;
    }

    /// <summary>
    /// A false candidate can announce a "frame" that swallows real frames and happens to end on a real boundary. If a
    /// clean chain of known frames starting inside one of the accepted run's frames lands exactly on that frame's end,
    /// the inner chain is the real alignment (more frames explain the same bytes).
    /// </summary>
    private int RefineAlignment(int k)
    {
        int result = k;
        int start = k;
        int needed = Math.Max(1, _opt.ResyncCleanFrames);
        for (int i = 0; i < needed; i++)
        {
            while (start < _len && _buf[start] == 0) start++;
            if (start >= _len) break;
            if (ParseHeader(new ReadOnlySpan<byte>(_buf, start, _len - start), topLevel: true, out var info) != Parse.Ok) break;
            int end = start + info.Total;
            // A junk frame may also end a few bytes inside a real frame whose remaining bytes are zeros (the high bytes
            // of a heartbeat clock, a damage trailer): they then read as padding. Compare against the next non-zero byte.
            int effectiveEnd = end;
            while (effectiveEnd < _len && _buf[effectiveEnd] == 0) effectiveEnd++;
            // Never look inside a bundle: LZ4 literals carry verbatim frame bytes.
            for (int inner = start + 1; !info.IsBundle && inner < end; inner++)
            {
                if (_buf[inner] != 0 && ChainReaches(inner, effectiveEnd))
                {
                    result = inner;
                    break;
                }
            }

            start = end;
        }

        return result;
    }

    /// <summary>Complete known frames from <paramref name="from"/> that end exactly at <paramref name="end"/>.</summary>
    private bool ChainReaches(int from, int end)
    {
        int off = from;
        int frames = 0;
        while (off < end)
        {
            if (_buf[off] == 0)
            {
                off++;
                continue;
            }

            var span = new ReadOnlySpan<byte>(_buf, off, end - off);
            if (ParseHeader(span, topLevel: false, out var info) != Parse.Ok || !_ops.IsKnown(info.Opcode)) return false;
            if (info.IsBundle && !BundleHeaderPlausible(span.Slice(info.BodyOffset, info.Total - info.BodyOffset))) return false;
            off += info.Total;
            frames++;
        }

        return off == end && frames > 0;
    }

    private Candidate CheckCandidate(int k, long skipped, bool allowEarly)
    {
        if (skipped >= _opt.ResyncByteFallbackBytes)
        {
            var span = new ReadOnlySpan<byte>(_buf, k, _len - k);
            return ParseHeader(span, topLevel: true, out _) switch
            {
                Parse.Ok => Candidate.Yes,
                Parse.NeedMore => Candidate.NeedMore,
                _ => Candidate.No,
            };
        }

        if (skipped >= _opt.ResyncHeartbeatFallbackBytes)
        {
            int avail = _len - k;
            if (avail < 3) return Candidate.NeedMore;
            byte hb0 = (byte)(_ops.Heartbeat >> 8), hb1 = (byte)_ops.Heartbeat;
            if ((_buf[k] == 0x0E || _buf[k] == 0x06) && _buf[k + 1] == hb0 && _buf[k + 2] == hb1) return Candidate.Yes;
        }

        return CleanRun(k, allowEarly);
    }

    /// <summary>≥ N consecutive frames from <paramref name="k"/> that parse with known opcodes (§4.4).</summary>
    private Candidate CleanRun(int k, bool allowEarly)
    {
        int needed = Math.Max(1, _opt.ResyncCleanFrames);
        int off = k;
        int count = 0;
        while (count < needed)
        {
            while (off < _len && _buf[off] == 0) off++;
            if (off >= _len)
            {
                // The data ends exactly at a frame boundary. Right after a clean (re)start this is the normal shape of
                // an aligned stream, so accept it; mid-garbage, wait for more frames.
                return allowEarly && count > 0 ? Candidate.Yes : Candidate.NeedMore;
            }

            var span = new ReadOnlySpan<byte>(_buf, off, _len - off);
            var status = ParseHeader(span, topLevel: true, out var info);
            if (info.OpcodeAvailable && !_ops.IsKnown(info.Opcode)) return Candidate.No;
            if (status == Parse.Invalid) return Candidate.No;
            if (status == Parse.NeedMore) return Candidate.NeedMore;
            if (info.IsBundle && !BundleHeaderPlausible(span.Slice(info.BodyOffset, info.Total - info.BodyOffset))) return Candidate.No;
            count++;
            off += info.Total;
        }

        return Candidate.Yes;
    }

    private bool BundleHeaderPlausible(ReadOnlySpan<byte> body)
    {
        if (body.Length < 5) return false;
        uint raw = BinaryPrimitives.ReadUInt32LittleEndian(body);
        return raw >= 1 && raw <= (uint)_opt.MaxBundleRawSize && raw <= (long)(body.Length - 4) * 255 + 16;
    }

    // ───────────────────────────── frame header ─────────────────────────────

    /// <summary>Parses a frame header at the start of <paramref name="s"/> (§4.1/§4.2).</summary>
    private Parse ParseHeader(ReadOnlySpan<byte> s, bool topLevel, out FrameInfo info)
    {
        info = default;
        var vs = VarInt.TryRead(s, out uint length, out int width);
        if (vs == VarIntStatus.Truncated) return topLevel ? Parse.NeedMore : Parse.Invalid;
        if (vs == VarIntStatus.Invalid) return Parse.Invalid;

        long total = (long)length + width - 4;
        if (total < width + 2 || total > _opt.MaxFrame) return Parse.Invalid;

        int p = width;
        if (s.Length <= p) return topLevel ? Parse.NeedMore : Parse.Invalid;
        bool extra = s[p] is >= 0xF0 and <= 0xFE;
        if (extra)
        {
            p++;
            if (total < p + 2) return Parse.Invalid;
        }

        if (s.Length < p + 2) return topLevel ? Parse.NeedMore : Parse.Invalid;

        info.Opcode = (ushort)((s[p] << 8) | s[p + 1]);
        info.OpcodeAvailable = true;
        info.ExtraByte = extra;
        info.IsBundle = info.Opcode == Aion2Dps.Contracts.Opcodes.Bundle;
        info.BodyOffset = p + 2;
        info.Total = (int)total;

        if (total > s.Length)
        {
            if (!topLevel) return Parse.Invalid;
            if (total > _opt.MaxWait && !info.IsBundle && !(_opt.WaitForLargeKnownFrames && _ops.IsKnown(info.Opcode)))
                return Parse.Invalid;
            return Parse.NeedMore;
        }

        return Parse.Ok;
    }

    // ───────────────────────────── dispatch / bundles ─────────────────────────────

    private void Dispatch(byte[] buf, int start, in FrameInfo info, int depth, bool embedded)
    {
        int bodyStart = start + info.BodyOffset;
        int bodyLen = start + info.Total - bodyStart;
        if (info.ExtraByte) _diag.IncrementExtraByteFrames();

        if (info.IsBundle)
        {
            _diag.RecordFrame(info.Opcode, bodyLen, _time, delivered: false);
            HandleBundle(buf, bodyStart, bodyLen, depth);
            return;
        }

        _diag.RecordFrame(info.Opcode, bodyLen, _time, delivered: true);
        var frame = new Frame(_time, info.Opcode, new ReadOnlyMemory<byte>(buf, bodyStart, bodyLen), depth);
        try
        {
            _sink.OnFrame(in frame);
        }
        catch (Exception ex)
        {
            _diag.IncrementSinkErrors();
            _diag.RecordError(_time, $"frame sink threw on {Aion2Dps.Contracts.Opcodes.Format(info.Opcode)}: {ex.GetType().Name}: {ex.Message}");
        }

        if (_opt.ScanEmbeddedBundles && !embedded && bodyLen >= 12) ScanEmbedded(buf, bodyStart, bodyLen, depth);
    }

    private void HandleBundle(byte[] buf, int bodyStart, int bodyLen, int depth)
    {
        if (depth >= _opt.MaxBundleDepth)
        {
            BundleError($"bundle nested deeper than {_opt.MaxBundleDepth}, skipped");
            return;
        }

        if (bodyLen < 5)
        {
            BundleError($"bundle body too short ({bodyLen} bytes)");
            return;
        }

        uint raw = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(bodyStart, 4));
        int blockLen = bodyLen - 4;
        if (raw < 1 || raw > (uint)_opt.MaxBundleRawSize || raw > (long)blockLen * 255 + 16)
        {
            BundleError($"bundle rawSize {raw} out of range (block {blockLen} bytes)");
            return;
        }

        int size = (int)raw;
        byte[] dst = GetScratch(depth + 1, size);
        if (!Lz4Block.TryDecompress(buf.AsSpan(bodyStart + 4, blockLen), dst.AsSpan(0, size), out int written))
        {
            BundleError($"bundle LZ4 block invalid (rawSize {size}, produced {written}, block {blockLen} bytes)");
            return;
        }

        _diag.IncrementBundles();
        WalkInner(dst, size, depth + 1, embedded: false);
    }

    /// <summary>Walks decompressed bundle content: §4.1 framing, padding allowed, stop at the first malformed frame.</summary>
    private void WalkInner(byte[] buf, int len, int depth, bool embedded)
    {
        int pos = 0;
        while (pos < len)
        {
            if (buf[pos] == 0)
            {
                pos++;
                continue;
            }

            var span = new ReadOnlySpan<byte>(buf, pos, len - pos);
            if (ParseHeader(span, topLevel: false, out var info) != Parse.Ok)
            {
                if (!embedded)
                {
                    _diag.IncrementBundleInnerErrors();
                    BundleError($"malformed frame inside bundle at {pos}/{len}: {Hex.Format(span, 12)}");
                }

                return;
            }

            if (!embedded)
            {
                Dispatch(buf, pos, in info, depth, embedded: false);
            }
            else if (!info.IsBundle && info.Opcode == _ops.SelfInfo)
            {
                _diag.IncrementEmbeddedForwarded();
                Dispatch(buf, pos, in info, depth, embedded: true);
            }
            else
            {
                _diag.IncrementEmbeddedIgnored();
            }

            pos += info.Total;
        }
    }

    /// <summary>
    /// §5 embedded bundles: <c>FF FF</c> preceded by a 1-3 byte varint L ≥ 12 whose frame fits in the body, then a
    /// valid LZ4 block. Diagnostic only, except identity frames which are forwarded.
    /// </summary>
    private void ScanEmbedded(byte[] buf, int bodyStart, int bodyLen, int depth)
    {
        if (depth >= _opt.MaxBundleDepth) return;
        var body = new ReadOnlySpan<byte>(buf, bodyStart, bodyLen);
        ReadOnlySpan<byte> marker = [0xFF, 0xFF];
        int from = 1;
        while (from < body.Length - 1)
        {
            int idx = body[from..].IndexOf(marker);
            if (idx < 0) return;
            int j = from + idx;
            int end = TryEmbeddedAt(buf, bodyStart, body, j, depth);
            from = end > j ? end : j + 1;
        }
    }

    private int TryEmbeddedAt(byte[] buf, int bodyStart, ReadOnlySpan<byte> body, int j, int depth)
    {
        for (int w = 1; w <= 3; w++)
        {
            int start = j - w;
            if (start < 0) break;
            if (VarInt.TryRead(body[start..], out uint length, out int width) != VarIntStatus.Ok || width != w) continue;
            if (length < 12) continue;
            long total = (long)length + w - 4;
            if (start + total > body.Length) continue;
            int payloadLen = (int)total - w;           // FF FF + rawSize + block
            if (payloadLen < 2 + 4 + 1) continue;
            uint raw = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(j + 2, 4));
            int blockLen = payloadLen - 6;
            if (raw < 1 || raw > (uint)_opt.MaxBundleRawSize || raw > (long)blockLen * 255 + 16) continue;
            int size = (int)raw;
            if (_embeddedScratch.Length < size) _embeddedScratch = new byte[RoundUp(size)];
            if (!Lz4Block.TryDecompress(body.Slice(j + 6, blockLen), _embeddedScratch.AsSpan(0, size), out _)) continue;

            _diag.IncrementEmbeddedBundles();
            WalkInner(_embeddedScratch, size, depth + 1, embedded: true);
            return start + (int)total;
        }

        return j + 1;
    }

    private void BundleError(string message)
    {
        _diag.IncrementBundleErrors();
        _diag.RecordError(_time, message);
    }

    private byte[] GetScratch(int depth, int size)
    {
        var b = _scratch[depth];
        if (b is null || b.Length < size)
        {
            b = new byte[RoundUp(size)];
            _scratch[depth] = b;
        }

        return b;
    }

    private static int RoundUp(int size)
    {
        int s = 4096;
        while (s < size && s < (1 << 30)) s <<= 1;
        return Math.Max(s, size);
    }
}
