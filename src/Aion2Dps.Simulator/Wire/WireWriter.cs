using System.Buffers.Binary;
using System.Text;

namespace Aion2Dps.Simulator;

/// <summary>
/// Growable little-endian byte writer for the AION 2 wire primitives (PROTOCOL.md §6): unsigned LEB128 varints,
/// fixed-width LE integers, f32, and u8-length-prefixed UTF-8 strings.
/// </summary>
public sealed class WireWriter
{
    private byte[] _buffer;
    private int _length;

    public WireWriter(int capacity = 64) => _buffer = new byte[Math.Max(16, capacity)];

    /// <summary>Number of bytes written so far.</summary>
    public int Length => _length;

    /// <summary>The written bytes (valid until the next write).</summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

    public void Clear() => _length = 0;

    private Span<byte> Grow(int count)
    {
        if (_length + count > _buffer.Length)
        {
            int size = Math.Max(_buffer.Length * 2, _length + count);
            Array.Resize(ref _buffer, size);
        }

        var span = _buffer.AsSpan(_length, count);
        _length += count;
        return span;
    }

    public WireWriter U8(byte value)
    {
        Grow(1)[0] = value;
        return this;
    }

    public WireWriter U16(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(Grow(2), value);
        return this;
    }

    public WireWriter U32(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(Grow(4), value);
        return this;
    }

    public WireWriter U64(ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(Grow(8), value);
        return this;
    }

    public WireWriter I64(long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(Grow(8), value);
        return this;
    }

    public WireWriter F32(float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(Grow(4), value);
        return this;
    }

    /// <summary>Opcode in wire byte order: <c>0x0438</c> is written as <c>04 38</c>.</summary>
    public WireWriter Opcode(ushort opcode)
    {
        var span = Grow(2);
        span[0] = (byte)(opcode >> 8);
        span[1] = (byte)opcode;
        return this;
    }

    /// <summary>Unsigned LEB128 (§6.1). Values above <see cref="uint.MaxValue"/> are rejected by the protocol.</summary>
    public WireWriter Varint(ulong value)
    {
        if (value > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(value), "AION 2 varints fit in u32.");
        var span = Grow(Simulator.Varint.Size(value));
        Simulator.Varint.Write(span, value);
        return this;
    }

    /// <summary>Varint of a non-negative signed value (damage, HP).</summary>
    public WireWriter Varint(long value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "Varints are unsigned.");
        return Varint((ulong)value);
    }

    /// <summary>u8 byte length + UTF-8 bytes, no terminator (§6.2).</summary>
    public WireWriter String(string? value)
    {
        value ??= "";
        int count = Encoding.UTF8.GetByteCount(value);
        if (count > 255) throw new ArgumentException("Wire strings are at most 255 UTF-8 bytes.", nameof(value));
        U8((byte)count);
        Encoding.UTF8.GetBytes(value, Grow(count));
        return this;
    }

    public WireWriter Bytes(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(Grow(bytes.Length));
        return this;
    }

    public WireWriter Zeros(int count)
    {
        Grow(count).Clear();
        return this;
    }
}

/// <summary>Unsigned LEB128 helpers (PROTOCOL.md §6.1).</summary>
public static class Varint
{
    /// <summary>Encoded width of <paramref name="value"/> in bytes (1..5 for u32).</summary>
    public static int Size(ulong value)
    {
        int n = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            n++;
        }

        return n;
    }

    /// <summary>Writes the varint and returns the number of bytes written.</summary>
    public static int Write(Span<byte> destination, ulong value)
    {
        int i = 0;
        while (value >= 0x80)
        {
            destination[i++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[i++] = (byte)value;
        return i;
    }

    public static byte[] Encode(ulong value)
    {
        var bytes = new byte[Size(value)];
        Write(bytes, value);
        return bytes;
    }
}
