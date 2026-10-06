using System.Buffers.Binary;
using System.Text;

namespace Aion2Dps.Capture;

/// <summary>
/// Managed capture file writer (no Npcap dependency). Thread-safe.
/// <list type="bullet">
/// <item><b>pcapng</b>: one section; an Interface Description Block is written on demand for each link type, with
/// <c>if_tsresol = 7</c> (100 ns, exactly <see cref="DateTime"/> ticks); packets are Enhanced Packet Blocks.</item>
/// <item><b>pcap</b>: little endian, µs (or ns) timestamps. The file header is written with the first packet's link type;
/// packets with another link type are skipped (counted in <see cref="PacketsSkipped"/>).</item>
/// </list>
/// </summary>
public sealed class PcapFileWriter : IPacketRecorder, IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly object _sync = new();
    private readonly int _snapLength;
    private readonly bool _nanoseconds;
    private readonly Dictionary<int, int> _ngInterfaces = new(); // link type -> interface id
    private int _pcapLinkType;
    private bool _pcapHeaderWritten;
    private bool _disposed;
    private byte[] _scratch = new byte[2048];

    /// <param name="stream">Destination stream (written sequentially).</param>
    /// <param name="format">pcap or pcapng.</param>
    /// <param name="defaultLinkType">Classic pcap: link type written when the file is closed without packets.</param>
    /// <param name="leaveOpen">Do not dispose <paramref name="stream"/>.</param>
    /// <param name="snapLength">Snap length written to the headers.</param>
    /// <param name="nanosecondTimestamps">Classic pcap: write nanosecond timestamps (magic A1B23C4D).</param>
    /// <param name="application">pcapng shb_userappl option.</param>
    public PcapFileWriter(Stream stream, CaptureFileFormat format, int defaultLinkType = LinkTypes.Ethernet, bool leaveOpen = false,
        int snapLength = 65535, bool nanosecondTimestamps = false, string application = "Aion2Dps")
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _leaveOpen = leaveOpen;
        Format = format;
        _snapLength = snapLength > 0 ? snapLength : 65535;
        _nanoseconds = nanosecondTimestamps;
        _pcapLinkType = defaultLinkType;
        if (format == CaptureFileFormat.PcapNg) WriteSectionHeader(application);
    }

    /// <summary>Creates (overwrites) a capture file. The parent directory is created if needed.</summary>
    public static PcapFileWriter Create(string path, CaptureFileFormat format, int defaultLinkType = LinkTypes.Ethernet,
        int snapLength = 65535, bool nanosecondTimestamps = false)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        return new PcapFileWriter(fs, format, defaultLinkType, leaveOpen: false, snapLength, nanosecondTimestamps);
    }

    /// <summary>Format chosen from the extension: <c>.pcap</c> → classic pcap, anything else → pcapng.</summary>
    public static CaptureFileFormat FormatForPath(string path) =>
        string.Equals(Path.GetExtension(path), ".pcap", StringComparison.OrdinalIgnoreCase) ? CaptureFileFormat.Pcap : CaptureFileFormat.PcapNg;

    public CaptureFileFormat Format { get; }
    public long PacketsWritten { get; private set; }
    public long PacketsSkipped { get; private set; }
    public long BytesWritten { get; private set; }

    /// <summary>Writes one packet (frame bytes are truncated to the snap length).</summary>
    public void WritePacket(DateTime timestampUtc, int linkType, ReadOnlySpan<byte> frame)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Format == CaptureFileFormat.Pcap) WritePcapRecord(timestampUtc, linkType, frame);
            else WriteEnhancedPacket(timestampUtc, linkType, frame);
        }
    }

    public void Flush()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _stream.Flush();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            try
            {
                if (Format == CaptureFileFormat.Pcap && !_pcapHeaderWritten) WritePcapHeader(_pcapLinkType);
                _stream.Flush();
            }
            finally
            {
                _disposed = true;
                if (!_leaveOpen) _stream.Dispose();
            }
        }
    }

    // ───────────────────────────── classic pcap ─────────────────────────────

    private void WritePcapHeader(int linkType)
    {
        Span<byte> h = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(h, _nanoseconds ? 0xA1B23C4Du : 0xA1B2C3D4u);
        BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(6), 4);
        BinaryPrimitives.WriteInt32LittleEndian(h.Slice(8), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(12), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(16), (uint)_snapLength);
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(20), (uint)linkType);
        Put(h);
        _pcapLinkType = linkType;
        _pcapHeaderWritten = true;
    }

    private void WritePcapRecord(DateTime timestampUtc, int linkType, ReadOnlySpan<byte> frame)
    {
        if (!_pcapHeaderWritten) WritePcapHeader(linkType);
        else if (linkType != _pcapLinkType)
        {
            PacketsSkipped++;
            return;
        }
        long ticks = TicksSinceEpoch(timestampUtc);
        long sec = ticks / TimeSpan.TicksPerSecond;
        long rem = ticks % TimeSpan.TicksPerSecond;
        uint frac = _nanoseconds ? (uint)(rem * 100) : (uint)(rem / 10);
        int cap = Math.Min(frame.Length, _snapLength);
        Span<byte> h = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(h, (uint)sec);
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(4), frac);
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(8), (uint)cap);
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(12), (uint)frame.Length);
        Put(h);
        Put(frame.Slice(0, cap));
        PacketsWritten++;
    }

    // ───────────────────────────── pcapng ─────────────────────────────

    private void WriteSectionHeader(string application)
    {
        byte[] app = Encoding.UTF8.GetBytes(application ?? "");
        int optLen = app.Length > 0 ? 4 + Pad4(app.Length) + 4 : 0; // shb_userappl + opt_endofopt
        int total = 28 + optLen;
        var block = Rent(total);
        BinaryPrimitives.WriteUInt32LittleEndian(block, 0x0A0D0D0A);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(8), 0x1A2B3C4D);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(14), 0);
        BinaryPrimitives.WriteInt64LittleEndian(block.AsSpan(16), -1); // section length unknown
        int o = 24;
        if (app.Length > 0)
        {
            o = WriteOption(block, o, 4, app);
            o = WriteOption(block, o, 0, ReadOnlySpan<byte>.Empty);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(o), (uint)total);
        Put(block.AsSpan(0, total));
    }

    private int InterfaceFor(int linkType)
    {
        if (_ngInterfaces.TryGetValue(linkType, out int id)) return id;
        id = _ngInterfaces.Count;
        // IDB: linktype u16, reserved u16, snaplen u32, if_tsresol (7 = 100 ns), opt_endofopt
        int total = 20 + 8 + 4;
        var block = Rent(total);
        Array.Clear(block, 0, total);
        BinaryPrimitives.WriteUInt32LittleEndian(block, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), (uint)total);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8), (ushort)linkType);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(12), (uint)_snapLength);
        int o = WriteOption(block, 16, 9, [7]);
        o = WriteOption(block, o, 0, ReadOnlySpan<byte>.Empty);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(o), (uint)total);
        Put(block.AsSpan(0, total));
        _ngInterfaces[linkType] = id;
        return id;
    }

    private void WriteEnhancedPacket(DateTime timestampUtc, int linkType, ReadOnlySpan<byte> frame)
    {
        int ifId = InterfaceFor(linkType);
        int cap = Math.Min(frame.Length, _snapLength);
        int total = 32 + Pad4(cap);
        var block = Rent(total);
        ulong ts = (ulong)TicksSinceEpoch(timestampUtc);
        BinaryPrimitives.WriteUInt32LittleEndian(block, 6);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(8), (uint)ifId);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(12), (uint)(ts >> 32));
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(16), (uint)ts);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(20), (uint)cap);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(24), (uint)frame.Length);
        frame.Slice(0, cap).CopyTo(block.AsSpan(28));
        Array.Clear(block, 28 + cap, Pad4(cap) - cap);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(total - 4), (uint)total);
        Put(block.AsSpan(0, total));
        PacketsWritten++;
    }

    private static int WriteOption(byte[] block, int offset, ushort code, ReadOnlySpan<byte> value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(offset), code);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(offset + 2), (ushort)value.Length);
        value.CopyTo(block.AsSpan(offset + 4));
        int padded = Pad4(value.Length);
        Array.Clear(block, offset + 4 + value.Length, padded - value.Length);
        return offset + 4 + padded;
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static int Pad4(int n) => (n + 3) & ~3;

    private static long TicksSinceEpoch(DateTime t)
    {
        var utc = t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : t;
        long ticks = utc.Ticks - DateTime.UnixEpoch.Ticks;
        return ticks < 0 ? 0 : ticks;
    }

    private byte[] Rent(int size)
    {
        if (_scratch.Length < size) _scratch = new byte[Math.Max(size, _scratch.Length * 2)];
        return _scratch;
    }

    private void Put(ReadOnlySpan<byte> bytes)
    {
        _stream.Write(bytes);
        BytesWritten += bytes.Length;
    }
}
