namespace Aion2Dps.Contracts;

// Data flow (each arrow is one of the interfaces below):
//
//   ICaptureService / IReplaySource  --(IStreamSink)-->  FrameDecoder (Protocol)
//   FrameDecoder                      --(IFrameSink)-->   PacketDecoder (Protocol)
//   PacketDecoder                     --(IGameEventSink)--> ICombatEngine (Combat)
//   ICombatEngine  --> MeterSnapshot (UI polls)  /  EncounterRecord (UI breakdown, IFightStore)
//
// All sinks are called synchronously on the capture thread, in stream order.

/// <summary>Why the server→client byte stream is no longer contiguous.</summary>
public enum DiscontinuityReason
{
    NewConnection,
    TcpGap,
    CaptureRestarted,
    ReplayStarted,
}

/// <summary>
/// Receives the reassembled, in-order <b>server→client</b> TCP payload bytes of the locked game connection.
/// </summary>
public interface IStreamSink
{
    /// <summary>Contiguous bytes that directly follow the previous call's bytes. <paramref name="timeUtc"/> is the
    /// capture (pcap) timestamp of the segment that completed these bytes. The span is only valid during the call.</summary>
    void OnData(DateTime timeUtc, ReadOnlySpan<byte> data);

    /// <summary>The stream is discontinuous: drop partial-frame state and resynchronise on the next bytes.</summary>
    void OnDiscontinuity(DiscontinuityReason reason);
}

/// <summary>One decoded frame (opcode + body). Bundles are unpacked before frames reach the sink.</summary>
/// <param name="TimeUtc">Capture timestamp.</param>
/// <param name="Opcode">Two opcode bytes in wire order (see <see cref="Opcodes"/>).</param>
/// <param name="Body">Bytes after the opcode up to the end of the frame. <b>Only valid during the OnFrame call.</b></param>
/// <param name="BundleDepth">0 = top-level frame, 1+ = from inside an LZ4 bundle (nesting depth).</param>
public readonly record struct Frame(DateTime TimeUtc, ushort Opcode, ReadOnlyMemory<byte> Body, int BundleDepth);

public interface IFrameSink
{
    void OnFrame(in Frame frame);
}

public interface IGameEventSink
{
    void OnEvent(GameEvent gameEvent);
}

/// <summary>Per-opcode traffic statistics (the "opcode census").</summary>
public sealed record OpcodeStat(ushort Opcode, long Count, long Bytes, long Decoded, long Failed)
{
    public string Hex => Opcodes.Format(Opcode);
}

/// <summary>Live diagnostics exposed by the protocol layer (thread-safe reads).</summary>
public interface IProtocolDiagnostics
{
    long BytesIn { get; }
    long Frames { get; }
    long Bundles { get; }
    long BundleErrors { get; }
    long Resyncs { get; }
    long DecodeErrors { get; }
    long EventsEmitted { get; }
    DateTime? LastFrameUtc { get; }
    IReadOnlyList<OpcodeStat> GetCensus();
    IReadOnlyList<string> GetRecentErrors();
}
