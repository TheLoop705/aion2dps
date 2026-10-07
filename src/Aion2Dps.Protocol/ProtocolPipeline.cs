using Aion2Dps.Contracts;

namespace Aion2Dps.Protocol;

/// <summary>
/// Convenience composition of the protocol layer: <see cref="Input"/> (an <see cref="IStreamSink"/> fed by capture or
/// replay) → <see cref="FrameDecoder"/> → <see cref="PacketDecoder"/> → the given <see cref="IGameEventSink"/>, all
/// sharing one <see cref="ProtocolDiagnostics"/>.
/// <para>This decodes ONE byte stream. Live capture and replays follow several game connections at once (world +
/// dungeon instance, LIVE-FINDINGS NEW 1); hosts use <see cref="MultiFlowProtocolPipeline"/> for them (a frame decoder per
/// flow, one packet decoder). A plain <see cref="Input"/> passed to capture only receives one flow at a time.</para>
/// </summary>
public sealed class ProtocolPipeline
{
    /// <param name="sink">Receives decoded game events (e.g. the combat engine).</param>
    /// <param name="opcodes">Opcode table; null = <see cref="OpcodeTable.Default"/>. Hosts normally pass
    /// <see cref="OpcodeTable.LoadOrDefault()"/> so data/protocol/opcodes.json applies.</param>
    /// <param name="options">Framing options; null = defaults.</param>
    public ProtocolPipeline(IGameEventSink sink, OpcodeTable? opcodes = null, FrameDecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        Opcodes = opcodes ?? OpcodeTable.Default;
        ProtocolDiagnostics = new ProtocolDiagnostics();
        PacketDecoder = new PacketDecoder(sink, Opcodes, ProtocolDiagnostics);
        FrameDecoder = new FrameDecoder(PacketDecoder, Opcodes, ProtocolDiagnostics, options);
    }

    /// <summary>Feed server→client stream bytes here.</summary>
    public IStreamSink Input => FrameDecoder;

    /// <summary>Live counters (thread-safe reads).</summary>
    public IProtocolDiagnostics Diagnostics => ProtocolDiagnostics;

    /// <summary>The concrete diagnostics object (extra counters beyond <see cref="IProtocolDiagnostics"/>).</summary>
    public ProtocolDiagnostics ProtocolDiagnostics { get; }

    public FrameDecoder FrameDecoder { get; }
    public PacketDecoder PacketDecoder { get; }
    public OpcodeTable Opcodes { get; }
}
