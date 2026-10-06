using System.Buffers.Binary;

namespace Aion2Dps.Capture;

public enum CaptureFileFormat
{
    /// <summary>Classic libpcap format.</summary>
    Pcap,
    /// <summary>pcap Next Generation.</summary>
    PcapNg,
}

/// <summary>One packet read from a capture file.</summary>
/// <param name="TimestampUtc">Capture timestamp (UTC).</param>
/// <param name="LinkType">LINKTYPE_* of the packet's interface (see <see cref="LinkTypes"/>).</param>
/// <param name="InterfaceId">pcapng interface id (0 for classic pcap).</param>
/// <param name="OriginalLength">Length on the wire (may exceed <see cref="Data"/> when truncated by the snaplen).</param>
/// <param name="Data">Captured bytes (owned by this record).</param>
public readonly record struct CapturedPacket(DateTime TimestampUtc, int LinkType, int InterfaceId, int OriginalLength, ReadOnlyMemory<byte> Data);

/// <summary>
/// Managed reader for classic pcap (little/big endian, µs/ns timestamps) and pcapng (SHB/IDB/EPB/SPB/obsolete PB,
/// multiple sections and interfaces, if_tsresol/if_tsoffset). No Npcap dependency.
/// </summary>
public sealed class PcapFileReader : IDisposable
{
    private const uint PcapMagicMicro = 0xA1B2C3D4;
    private const uint PcapMagicNano = 0xA1B23C4D;
    private const uint NgSectionHeader = 0x0A0D0D0A;
    private const uint NgByteOrderMagic = 0x1A2B3C4D;
    private const int MaxBlockLength = 256 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly byte[] _header = new byte[28];

    // classic pcap state
    private bool _pcapNanos;
    private int _pcapLinkType;

    // pcapng state
    private readonly List<NgInterface> _interfaces = new();

    public PcapFileReader(string path)
        : this(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16), leaveOpen: false)
    {
    }

    public PcapFileReader(Stream stream, bool leaveOpen = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _leaveOpen = leaveOpen;
        Span<byte> magic = stackalloc byte[4];
        if (!ReadExactly(magic)) throw new InvalidDataException("The capture file is empty.");
        uint le = BinaryPrimitives.ReadUInt32LittleEndian(magic);
        uint be = BinaryPrimitives.ReadUInt32BigEndian(magic);
        if (le == NgSectionHeader)
        {
            Format = CaptureFileFormat.PcapNg;
            ReadSectionHeaderAfterType();
        }
        else if (le is PcapMagicMicro or PcapMagicNano || be is PcapMagicMicro or PcapMagicNano)
        {
            Format = CaptureFileFormat.Pcap;
            IsBigEndian = le is not (PcapMagicMicro or PcapMagicNano);
            _pcapNanos = (IsBigEndian ? be : le) == PcapMagicNano;
            Span<byte> rest = stackalloc byte[20];
            if (!ReadExactly(rest)) throw new InvalidDataException("Truncated pcap file header.");
            VersionMajor = U16(rest);
            VersionMinor = U16(rest.Slice(2));
            SnapLength = (int)Math.Min(U32(rest.Slice(12)), int.MaxValue);
            _pcapLinkType = (int)(U32(rest.Slice(16)) & 0xFFFF); // upper bits: FCS info
        }
        else
        {
            throw new InvalidDataException($"Not a pcap/pcapng file (magic 0x{be:X8}).");
        }
    }

    public CaptureFileFormat Format { get; }
    /// <summary>Byte order of the file (pcapng: of the current section).</summary>
    public bool IsBigEndian { get; private set; }
    public int VersionMajor { get; private set; }
    public int VersionMinor { get; private set; }
    /// <summary>Classic pcap snaplen (pcapng: of the most recent interface).</summary>
    public int SnapLength { get; private set; }
    /// <summary>Classic pcap: true for nanosecond timestamps.</summary>
    public bool NanosecondTimestamps => _pcapNanos;

    /// <summary>Link types of the interfaces of the current pcapng section (classic pcap: the single link type).</summary>
    public IReadOnlyList<int> InterfaceLinkTypes =>
        Format == CaptureFileFormat.Pcap ? new[] { _pcapLinkType } : _interfaces.Select(i => i.LinkType).ToArray();

    /// <summary>Returns true when <paramref name="path"/> starts with a pcap or pcapng magic number.</summary>
    public static bool IsCaptureFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> m = stackalloc byte[4];
            if (fs.Read(m) != 4) return false;
            uint le = BinaryPrimitives.ReadUInt32LittleEndian(m);
            uint be = BinaryPrimitives.ReadUInt32BigEndian(m);
            return le == NgSectionHeader || le is PcapMagicMicro or PcapMagicNano || be is PcapMagicMicro or PcapMagicNano;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Reads the next packet. Returns false at the end of the file (a truncated final record also ends it).</summary>
    public bool TryReadNext(out CapturedPacket packet)
    {
        return Format == CaptureFileFormat.Pcap ? TryReadPcap(out packet) : TryReadPcapNg(out packet);
    }

    public IEnumerable<CapturedPacket> ReadAll()
    {
        while (TryReadNext(out var p)) yield return p;
    }

    public void Dispose()
    {
        if (!_leaveOpen) _stream.Dispose();
    }

    // ───────────────────────────── classic pcap ─────────────────────────────

    private bool TryReadPcap(out CapturedPacket packet)
    {
        packet = default;
        var hdr = _header.AsSpan(0, 16);
        if (!ReadExactly(hdr)) return false;
        uint sec = U32(hdr);
        uint frac = U32(hdr.Slice(4));
        uint incl = U32(hdr.Slice(8));
        uint orig = U32(hdr.Slice(12));
        if (incl > MaxBlockLength) throw new InvalidDataException($"pcap record length {incl} is not plausible.");
        var data = new byte[incl];
        if (!ReadExactly(data)) return false;
        long ticks = sec * TimeSpan.TicksPerSecond + (_pcapNanos ? frac / 100 : (long)frac * 10);
        packet = new CapturedPacket(ToUtc(ticks), _pcapLinkType, 0, (int)Math.Min(orig, int.MaxValue), data);
        return true;
    }

    // ───────────────────────────── pcapng ─────────────────────────────

    private void ReadSectionHeaderAfterType()
    {
        Span<byte> head = stackalloc byte[8];
        if (!ReadExactly(head)) throw new InvalidDataException("Truncated pcapng section header.");
        uint bomLe = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(4));
        if (bomLe == NgByteOrderMagic) IsBigEndian = false;
        else if (BinaryPrimitives.ReadUInt32BigEndian(head.Slice(4)) == NgByteOrderMagic) IsBigEndian = true;
        else throw new InvalidDataException("Bad pcapng byte-order magic.");
        uint total = U32(head);
        if (total < 28 || total > MaxBlockLength) throw new InvalidDataException($"Bad pcapng section header length {total}.");
        var rest = new byte[total - 12];
        if (!ReadExactly(rest)) throw new InvalidDataException("Truncated pcapng section header.");
        VersionMajor = U16(rest);
        VersionMinor = U16(rest.AsSpan(2));
        _interfaces.Clear();
    }

    private bool TryReadPcapNg(out CapturedPacket packet)
    {
        packet = default;
        Span<byte> head = stackalloc byte[8];
        Span<byte> bom = stackalloc byte[4];
        while (true)
        {
            if (!ReadExactly(head)) return false;
            uint typeLe = BinaryPrimitives.ReadUInt32LittleEndian(head);
            if (typeLe == NgSectionHeader)
            {
                // A new section: re-read the byte order and forget the interfaces.
                if (!ReadExactly(bom)) return false;
                bool big;
                if (BinaryPrimitives.ReadUInt32LittleEndian(bom) == NgByteOrderMagic) big = false;
                else if (BinaryPrimitives.ReadUInt32BigEndian(bom) == NgByteOrderMagic) big = true;
                else throw new InvalidDataException("Bad pcapng byte-order magic.");
                IsBigEndian = big;
                uint total = U32(head.Slice(4));
                if (total < 28 || total > MaxBlockLength) throw new InvalidDataException("Bad pcapng section header length.");
                var rest = new byte[total - 12];
                if (!ReadExactly(rest)) return false;
                VersionMajor = U16(rest);
                VersionMinor = U16(rest.AsSpan(2));
                _interfaces.Clear();
                continue;
            }

            uint type = U32(head);
            uint length = U32(head.Slice(4));
            if (length < 12 || length > MaxBlockLength) throw new InvalidDataException($"Bad pcapng block length {length}.");
            var body = new byte[length - 8]; // body + trailing length
            if (!ReadExactly(body)) return false;
            var b = body.AsSpan(0, body.Length - 4);

            switch (type)
            {
                case 1: // Interface Description Block
                    ParseInterface(b);
                    break;
                case 6: // Enhanced Packet Block
                {
                    if (b.Length < 20) break;
                    int ifId = (int)U32(b);
                    ulong ts = ((ulong)U32(b.Slice(4)) << 32) | U32(b.Slice(8));
                    int cap = (int)Math.Min(U32(b.Slice(12)), (uint)(b.Length - 20));
                    uint orig = U32(b.Slice(16));
                    var iface = GetInterface(ifId);
                    packet = new CapturedPacket(iface.ToUtc(ts), iface.LinkType, ifId, (int)Math.Min(orig, int.MaxValue),
                        b.Slice(20, cap).ToArray());
                    return true;
                }
                case 3: // Simple Packet Block (interface 0, no timestamp)
                {
                    if (b.Length < 4) break;
                    uint orig = U32(b);
                    var iface = GetInterface(0);
                    int cap = (int)Math.Min(Math.Min(orig, (uint)(b.Length - 4)), iface.SnapLength > 0 ? (uint)iface.SnapLength : uint.MaxValue);
                    packet = new CapturedPacket(DateTime.UnixEpoch, iface.LinkType, 0, (int)Math.Min(orig, int.MaxValue), b.Slice(4, cap).ToArray());
                    return true;
                }
                case 2: // obsolete Packet Block
                {
                    if (b.Length < 20) break;
                    int ifId = U16(b);
                    ulong ts = ((ulong)U32(b.Slice(4)) << 32) | U32(b.Slice(8));
                    int cap = (int)Math.Min(U32(b.Slice(12)), (uint)(b.Length - 20));
                    uint orig = U32(b.Slice(16));
                    var iface = GetInterface(ifId);
                    packet = new CapturedPacket(iface.ToUtc(ts), iface.LinkType, ifId, (int)Math.Min(orig, int.MaxValue), b.Slice(20, cap).ToArray());
                    return true;
                }
                default:
                    break; // name resolution, statistics, custom blocks...: skipped
            }
        }
    }

    private void ParseInterface(ReadOnlySpan<byte> b)
    {
        if (b.Length < 8) throw new InvalidDataException("Truncated pcapng interface description block.");
        var iface = new NgInterface { LinkType = U16(b), SnapLength = (int)Math.Min(U32(b.Slice(4)), int.MaxValue) };
        var opts = b.Slice(8);
        while (opts.Length >= 4)
        {
            int code = U16(opts);
            int len = U16(opts.Slice(2));
            if (code == 0) break;
            if (4 + len > opts.Length) break;
            var value = opts.Slice(4, len);
            if (code == 9 && len >= 1) iface.TsResol = value[0]; // if_tsresol
            else if (code == 14 && len >= 8) iface.TsOffsetSeconds = (long)U64(value); // if_tsoffset
            opts = opts.Slice(Math.Min(opts.Length, 4 + ((len + 3) & ~3)));
        }
        SnapLength = iface.SnapLength;
        _interfaces.Add(iface);
    }

    private NgInterface GetInterface(int id)
    {
        if (id < 0 || id >= _interfaces.Count)
            throw new InvalidDataException($"pcapng packet refers to undefined interface {id}.");
        return _interfaces[id];
    }

    private sealed class NgInterface
    {
        public int LinkType;
        public int SnapLength;
        public byte TsResol = 6;
        public long TsOffsetSeconds;

        public DateTime ToUtc(ulong units)
        {
            long ticks;
            if ((TsResol & 0x80) == 0)
            {
                int exp = TsResol;
                if (exp == 7) ticks = (long)units;
                else if (exp < 7) ticks = (long)(units * Pow10(7 - exp));
                else ticks = (long)(units / Pow10(exp - 7));
            }
            else
            {
                int shift = TsResol & 0x7F;
                ticks = (long)(((UInt128)units * TimeSpan.TicksPerSecond) >> shift);
            }
            return ToUtcTicks(ticks + TsOffsetSeconds * TimeSpan.TicksPerSecond);
        }

        private static ulong Pow10(int n)
        {
            ulong r = 1;
            for (int i = 0; i < n && i < 19; i++) r *= 10;
            return r;
        }
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static DateTime ToUtc(long ticksSinceEpoch) => ToUtcTicks(ticksSinceEpoch);

    private static DateTime ToUtcTicks(long ticksSinceEpoch)
    {
        long max = DateTime.MaxValue.Ticks - DateTime.UnixEpoch.Ticks;
        if (ticksSinceEpoch < 0) ticksSinceEpoch = 0;
        if (ticksSinceEpoch > max) ticksSinceEpoch = max;
        return new DateTime(DateTime.UnixEpoch.Ticks + ticksSinceEpoch, DateTimeKind.Utc);
    }

    private ushort U16(ReadOnlySpan<byte> s) => IsBigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);
    private uint U32(ReadOnlySpan<byte> s) => IsBigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
    private ulong U64(ReadOnlySpan<byte> s) => IsBigEndian ? BinaryPrimitives.ReadUInt64BigEndian(s) : BinaryPrimitives.ReadUInt64LittleEndian(s);

    private bool ReadExactly(Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = _stream.Read(buffer.Slice(total));
            if (n <= 0) return false;
            total += n;
        }
        return true;
    }
}
