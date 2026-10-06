using Aion2Dps.Contracts;

namespace Aion2Dps.Protocol;

/// <summary>
/// Pass-through <see cref="IStreamSink"/> that reports discontinuities to a callback before forwarding them, e.g. to
/// mark the active encounter as having capture gaps (<c>CombatEngine.NotifyCaptureGap</c>) on
/// <see cref="DiscontinuityReason.TcpGap"/>. Never throws out of the sink calls.
/// </summary>
public sealed class DiscontinuityTap : IStreamSink
{
    private readonly IStreamSink _inner;
    private readonly Action<DiscontinuityReason> _onDiscontinuity;

    public DiscontinuityTap(IStreamSink inner, Action<DiscontinuityReason> onDiscontinuity)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _onDiscontinuity = onDiscontinuity ?? throw new ArgumentNullException(nameof(onDiscontinuity));
    }

    /// <summary>Number of <see cref="DiscontinuityReason.TcpGap"/> notifications seen.</summary>
    public long TcpGaps => Interlocked.Read(ref _tcpGaps);
    private long _tcpGaps;

    public void OnData(DateTime timeUtc, ReadOnlySpan<byte> data) => _inner.OnData(timeUtc, data);

    public void OnDiscontinuity(DiscontinuityReason reason)
    {
        if (reason == DiscontinuityReason.TcpGap) Interlocked.Increment(ref _tcpGaps);
        try { _onDiscontinuity(reason); }
        catch (Exception ex) { AppLog.Error("Protocol", "Discontinuity callback failed", ex); }
        _inner.OnDiscontinuity(reason);
    }
}
