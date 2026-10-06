using Aion2Dps.Contracts;

namespace Aion2Dps.Capture;

/// <summary>Receives raw link-layer frames of the locked game flow (both directions), e.g. a capture file writer.</summary>
public interface IPacketRecorder
{
    void WritePacket(DateTime timestampUtc, int linkType, ReadOnlySpan<byte> frame);
}

/// <summary>Wraps the downstream sink: counts delivered bytes and gaps, and never lets a sink exception escape into the
/// capture loop (ARCHITECTURE.md conventions).</summary>
internal sealed class StreamSinkGuard : IStreamSink
{
    private readonly IStreamSink _inner;
    private long _bytes;
    private long _gaps;
    private long _errors;

    public StreamSinkGuard(IStreamSink inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public long BytesDelivered => Interlocked.Read(ref _bytes);
    public long GapCount => Interlocked.Read(ref _gaps);
    public long SinkErrors => Interlocked.Read(ref _errors);
    public DateTime? LastDataUtc { get; private set; }

    public void OnData(DateTime timeUtc, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        Interlocked.Add(ref _bytes, data.Length);
        LastDataUtc = timeUtc;
        try
        {
            _inner.OnData(timeUtc, data);
        }
        catch (Exception ex)
        {
            OnError(ex);
        }
    }

    public void OnDiscontinuity(DiscontinuityReason reason)
    {
        if (reason == DiscontinuityReason.TcpGap) Interlocked.Increment(ref _gaps);
        try
        {
            _inner.OnDiscontinuity(reason);
        }
        catch (Exception ex)
        {
            OnError(ex);
        }
    }

    private void OnError(Exception ex)
    {
        long n = Interlocked.Increment(ref _errors);
        if (n <= 10 || n % 1000 == 0)
            AppLog.Error("Capture", $"Stream sink threw (#{n})", ex);
    }
}
