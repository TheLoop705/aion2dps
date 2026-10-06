using System.Globalization;
using System.Text;

namespace Aion2Dps.Capture;

/// <summary>One line of a taengu/RATmeter style payload log: one server→client TCP payload.</summary>
public sealed record HexLogEntry(DateTime TimestampUtc, string StreamKey, byte[] Payload);

/// <summary>
/// Reads/writes hex line logs (PROTOCOL.md §16): <c>&lt;ISO-8601 | epoch-ms&gt;|&lt;stream key&gt;|&lt;hex payload&gt;</c>,
/// lines starting with <c>#</c> are comments. Whitespace inside the hex is ignored.
/// </summary>
public static class HexLogReader
{
    public static IEnumerable<HexLogEntry> ReadFile(string path, Action<int, string>? onBadLine = null)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        foreach (var e in Read(reader, onBadLine)) yield return e;
    }

    /// <param name="onBadLine">Called with (line number, line) for malformed lines, which are skipped.</param>
    public static IEnumerable<HexLogEntry> Read(TextReader reader, Action<int, string>? onBadLine = null)
    {
        int lineNo = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            var t = line.AsSpan().Trim();
            if (t.IsEmpty || t[0] == '#') continue;
            if (TryParseLine(line, out var entry)) yield return entry!;
            else onBadLine?.Invoke(lineNo, line);
        }
    }

    public static bool TryParseLine(string line, out HexLogEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(line)) return false;
        int first = line.IndexOf('|');
        if (first <= 0) return false;
        int last = line.LastIndexOf('|');
        if (last <= first) return false;
        if (!TryParseTimestamp(line.AsSpan(0, first).Trim(), out var ts)) return false;
        string key = line.Substring(first + 1, last - first - 1).Trim();
        if (!TryParseHex(line.AsSpan(last + 1), out var payload)) return false;
        entry = new HexLogEntry(ts, key, payload);
        return true;
    }

    /// <summary>Accepts epoch milliseconds (or seconds with a fraction / &lt; 10^11) and ISO-8601 (with or without offset;
    /// no offset = UTC).</summary>
    public static bool TryParseTimestamp(ReadOnlySpan<char> text, out DateTime utc)
    {
        utc = default;
        if (text.IsEmpty) return false;
        if (decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
        {
            try
            {
                bool hasFraction = text.Contains('.');
                decimal ms = hasFraction || number < 100_000_000_000m ? number * 1000m : number;
                utc = DateTime.UnixEpoch.AddTicks((long)(ms * TimeSpan.TicksPerMillisecond));
                return true;
            }
            catch (ArgumentOutOfRangeException) { return false; }
            catch (OverflowException) { return false; }
        }
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var dto))
        {
            utc = dto.UtcDateTime;
            return true;
        }
        return false;
    }

    public static bool TryParseHex(ReadOnlySpan<char> text, out byte[] bytes)
    {
        bytes = [];
        Span<char> clean = text.Length <= 4096 ? stackalloc char[text.Length] : new char[text.Length];
        int n = 0;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) || c == '-' || c == ':') continue;
            clean[n++] = c;
        }
        if (n == 0 || (n & 1) != 0) return false;
        try
        {
            bytes = Convert.FromHexString(clean.Slice(0, n));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Writes one log line (ISO-8601 UTC timestamp).</summary>
    public static void WriteLine(TextWriter writer, DateTime timestampUtc, string streamKey, ReadOnlySpan<byte> payload)
    {
        writer.Write(timestampUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        writer.Write('|');
        writer.Write(streamKey);
        writer.Write('|');
        writer.WriteLine(Convert.ToHexString(payload));
    }
}
