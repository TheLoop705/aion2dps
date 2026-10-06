namespace Aion2Dps.Protocol;

/// <summary>Result of decoding a varint from a possibly incomplete buffer.</summary>
public enum VarIntStatus
{
    /// <summary>A complete, valid varint was read.</summary>
    Ok,
    /// <summary>The buffer ends before the varint does (fewer than 5 bytes available). Wait for more bytes.</summary>
    Truncated,
    /// <summary>5 bytes were available and the 5th still has the continuation bit set, or the value overflows u32.</summary>
    Invalid,
}

/// <summary>
/// Unsigned LEB128 varints as used by the AION 2 wire format (PROTOCOL.md §6.1): 7 data bits per byte, least
/// significant group first, 0x80 = continue, at most 5 bytes, no zigzag, values fit in u32.
/// </summary>
public static class VarInt
{
    /// <summary>Maximum encoded width in bytes.</summary>
    public const int MaxWidth = 5;

    /// <summary>Decodes a varint at the start of <paramref name="span"/>.</summary>
    /// <param name="span">Bytes starting at the varint.</param>
    /// <param name="value">Decoded value (0 unless <see cref="VarIntStatus.Ok"/>).</param>
    /// <param name="width">Encoded width in bytes (0 unless <see cref="VarIntStatus.Ok"/>).</param>
    public static VarIntStatus TryRead(ReadOnlySpan<byte> span, out uint value, out int width)
    {
        uint v = 0;
        int shift = 0;
        int n = Math.Min(span.Length, MaxWidth);
        for (int k = 0; k < n; k++)
        {
            byte b = span[k];
            if (k == MaxWidth - 1)
            {
                // 5th byte: no continuation allowed, and only 4 data bits fit in a u32.
                if ((b & 0x80) != 0 || b > 0x0F)
                {
                    value = 0;
                    width = 0;
                    return VarIntStatus.Invalid;
                }
            }

            v |= (uint)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                value = v;
                width = k + 1;
                return VarIntStatus.Ok;
            }

            shift += 7;
        }

        value = 0;
        width = 0;
        return span.Length >= MaxWidth ? VarIntStatus.Invalid : VarIntStatus.Truncated;
    }

    /// <summary>Number of bytes <paramref name="value"/> takes when encoded.</summary>
    public static int GetWidth(uint value)
    {
        int w = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            w++;
        }

        return w;
    }

    /// <summary>Encodes <paramref name="value"/> into <paramref name="destination"/>; returns the bytes written, or 0 if it does not fit.</summary>
    public static int Write(uint value, Span<byte> destination)
    {
        int w = GetWidth(value);
        if (destination.Length < w) return 0;
        for (int k = 0; k < w - 1; k++)
        {
            destination[k] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[w - 1] = (byte)value;
        return w;
    }
}
