namespace Aion2Dps.Protocol;

/// <summary>Hex helpers for diagnostics and tests.</summary>
public static class Hex
{
    /// <summary>Upper-case hex of the first <paramref name="maxBytes"/> bytes, with "…" when truncated.</summary>
    public static string Format(ReadOnlySpan<byte> data, int maxBytes = 32)
    {
        int n = Math.Min(data.Length, Math.Max(0, maxBytes));
        string s = Convert.ToHexString(data[..n]);
        return n < data.Length ? s + "…" : s;
    }

    /// <summary>Parses hex, ignoring spaces, dashes and line breaks (e.g. <c>"04 38 b1ea01"</c>).</summary>
    public static byte[] Parse(string hex)
    {
        Span<char> clean = hex.Length <= 4096 ? stackalloc char[hex.Length] : new char[hex.Length];
        int n = 0;
        foreach (char c in hex)
        {
            if (char.IsWhiteSpace(c) || c == '-') continue;
            clean[n++] = c;
        }

        return Convert.FromHexString(clean[..n]);
    }
}
