using System.Buffers.Binary;
using System.Text;
using System.Text.Unicode;

namespace Aion2Dps.Protocol;

/// <summary>
/// Forward-only little-endian reader over a byte span. Every <c>TryRead*</c> method returns false (and leaves
/// <see cref="Position"/> unchanged) when the data is truncated or malformed; nothing throws.
/// </summary>
public ref struct SpanReader
{
    private readonly ReadOnlySpan<byte> _span;
    private int _pos;

    public SpanReader(ReadOnlySpan<byte> span)
    {
        _span = span;
        _pos = 0;
    }

    /// <summary>The whole underlying span.</summary>
    public readonly ReadOnlySpan<byte> Span => _span;

    /// <summary>Total length of the underlying span.</summary>
    public readonly int Length => _span.Length;

    /// <summary>Current offset. Setting it clamps to 0..Length.</summary>
    public int Position
    {
        readonly get => _pos;
        set => _pos = Math.Clamp(value, 0, _span.Length);
    }

    /// <summary>Bytes left after <see cref="Position"/>.</summary>
    public readonly int Remaining => _span.Length - _pos;

    /// <summary>True when every byte has been consumed.</summary>
    public readonly bool IsAtEnd => _pos >= _span.Length;

    /// <summary>The unread bytes.</summary>
    public readonly ReadOnlySpan<byte> RemainingSpan => _span[_pos..];

    public bool TryReadU8(out byte value)
    {
        if (_pos >= _span.Length)
        {
            value = 0;
            return false;
        }

        value = _span[_pos++];
        return true;
    }

    public readonly bool TryPeekU8(out byte value)
    {
        if (_pos >= _span.Length)
        {
            value = 0;
            return false;
        }

        value = _span[_pos];
        return true;
    }

    public bool TryReadU16(out ushort value)
    {
        if (Remaining < 2)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(_span[_pos..]);
        _pos += 2;
        return true;
    }

    public bool TryReadU32(out uint value)
    {
        if (Remaining < 4)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(_span[_pos..]);
        _pos += 4;
        return true;
    }

    public readonly bool TryPeekU32(out uint value)
    {
        if (Remaining < 4)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(_span[_pos..]);
        return true;
    }

    public bool TryReadU64(out ulong value)
    {
        if (Remaining < 8)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(_span[_pos..]);
        _pos += 8;
        return true;
    }

    public bool TryReadI64(out long value)
    {
        if (Remaining < 8)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadInt64LittleEndian(_span[_pos..]);
        _pos += 8;
        return true;
    }

    public bool TryReadF32(out float value)
    {
        if (Remaining < 4)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadSingleLittleEndian(_span[_pos..]);
        _pos += 4;
        return true;
    }

    /// <summary>Unsigned LEB128, at most 5 bytes; a 5th byte with the continuation bit is rejected (§6.1).</summary>
    public bool TryReadVarUInt(out uint value)
    {
        if (VarInt.TryRead(_span[_pos..], out value, out int width) != VarIntStatus.Ok)
        {
            value = 0;
            return false;
        }

        _pos += width;
        return true;
    }

    /// <summary>u8 byte length followed by that many bytes of valid UTF-8 (§6.2). Invalid UTF-8 fails.</summary>
    public bool TryReadString(out string value)
    {
        value = "";
        if (_pos >= _span.Length) return false;
        int len = _span[_pos];
        if (Remaining < 1 + len) return false;
        var bytes = _span.Slice(_pos + 1, len);
        if (!Utf8.IsValid(bytes)) return false;
        value = len == 0 ? "" : Encoding.UTF8.GetString(bytes);
        _pos += 1 + len;
        return true;
    }

    public bool TryReadBytes(int count, out ReadOnlySpan<byte> value)
    {
        if (count < 0 || Remaining < count)
        {
            value = default;
            return false;
        }

        value = _span.Slice(_pos, count);
        _pos += count;
        return true;
    }

    public bool TrySkip(int count)
    {
        if (count < 0 || Remaining < count) return false;
        _pos += count;
        return true;
    }
}
