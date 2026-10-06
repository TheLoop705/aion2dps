using System.Diagnostics;

namespace Aion2Dps.Combat;

/// <summary>
/// Ping from <c>03 36</c> echoes (§8.15). Global clients send QPC-ms + 16,777,216,000; the RTT is our QPC-ms clock plus that
/// offset minus the echoed value. Live-only diagnostic: replayed echoes fall outside 1..1000 ms and are ignored.
/// </summary>
internal sealed class PingTracker
{
    public const long GlobalClockOffsetMs = 16_777_216_000;
    private const double Alpha = 0.25;

    private readonly Func<long> _clientMs;

    public PingTracker(Func<long>? clientMs = null) => _clientMs = clientMs ?? QpcMilliseconds;

    public double? Smoothed { get; private set; }
    public long Samples { get; private set; }

    public static long QpcMilliseconds()
    {
        long ts = Stopwatch.GetTimestamp();
        long f = Stopwatch.Frequency;
        return ts / f * 1000 + ts % f * 1000 / f;
    }

    public void OnPing(long clientSentMs)
    {
        long rtt = _clientMs() + GlobalClockOffsetMs - clientSentMs;
        if (rtt < 1 || rtt > 1000) return;
        Samples++;
        Smoothed = Smoothed is double s ? s + Alpha * (rtt - s) : rtt;
    }

    public void Reset()
    {
        Smoothed = null;
        Samples = 0;
    }
}
