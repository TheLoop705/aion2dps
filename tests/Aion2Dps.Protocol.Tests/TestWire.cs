using Aion2Dps.Contracts;
using K4os.Compression.LZ4;

namespace Aion2Dps.Protocol.Tests;

/// <summary>Builds wire bytes for tests: frame = varint(payload length + 4) + payload (§4.1).</summary>
internal static class W
{
    public static readonly DateTime T0 = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

    public static byte[] H(string hex) => Hex.Parse(hex);

    public static byte[] VarInt(uint v)
    {
        var b = new byte[5];
        int n = Protocol.VarInt.Write(v, b);
        return b[..n];
    }

    /// <summary>Wraps a payload (opcode + body) into a frame.</summary>
    public static byte[] Frame(byte[] payload) => [.. VarInt((uint)payload.Length + 4), .. payload];

    /// <summary>Wraps hex of "opcode + body" into a frame.</summary>
    public static byte[] Frame(string payloadHex) => Frame(H(payloadHex));

    public static byte[] Packet(ushort opcode, byte[] body) => Frame([(byte)(opcode >> 8), (byte)opcode, .. body]);

    public static byte[] Heartbeat(ulong ms = 0x01A0F4207185UL) => Packet(Opcodes.Heartbeat, BitConverter.GetBytes(ms));

    public static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>Raw LZ4 block of <paramref name="data"/> (K4os oracle encoder).</summary>
    public static byte[] Lz4(byte[] data, LZ4Level level = LZ4Level.L00_FAST)
    {
        var target = new byte[LZ4Codec.MaximumOutputSize(data.Length)];
        int n = LZ4Codec.Encode(data, 0, data.Length, target, 0, target.Length, level);
        return target[..n];
    }

    /// <summary>A bundle frame: FF FF rawSize:u32 lz4(inner).</summary>
    public static byte[] Bundle(byte[] innerStream) =>
        Frame([0xFF, 0xFF, .. BitConverter.GetBytes((uint)innerStream.Length), .. Lz4(innerStream)]);

    /// <summary>Damage body (layout 4) with a valid effect id, for synthetic streams.</summary>
    public static byte[] DamageBody(uint target, uint actor, uint amount, uint skill = 11020030)
    {
        var list = new List<byte>();
        list.AddRange(VarInt(target));
        list.Add(0x04);
        list.Add(0x00);
        list.AddRange(VarInt(actor));
        list.AddRange(BitConverter.GetBytes(skill));
        list.Add(0x10);
        list.Add(0x02);
        list.AddRange(BitConverter.GetBytes(skill * 100 + 11));
        list.AddRange(BitConverter.GetBytes(1u));
        list.AddRange(VarInt(10170));
        list.AddRange(VarInt(amount));
        list.Add(0x01);
        list.Add(0x00);
        return list.ToArray();
    }

    public static byte[] DamageFrame(uint target, uint actor, uint amount) => Packet(Opcodes.Damage, DamageBody(target, actor, amount));
}

/// <summary>Collects frames (copying bodies, which are only valid during the call).</summary>
internal sealed class FrameCollector : IFrameSink
{
    public readonly List<(ushort Op, byte[] Body, int Depth, DateTime Time)> Frames = [];

    public void OnFrame(in Frame frame) => Frames.Add((frame.Opcode, frame.Body.ToArray(), frame.BundleDepth, frame.TimeUtc));

    public List<string> Keys() => Frames.Select(f => $"{f.Op:X4}:{f.Depth}:{Convert.ToHexString(f.Body)}").ToList();

    public List<ushort> Ops() => Frames.Select(f => f.Op).ToList();
}

internal sealed class EventCollector : IGameEventSink
{
    public readonly List<GameEvent> Events = [];

    public void OnEvent(GameEvent gameEvent) => Events.Add(gameEvent);

    public List<T> Of<T>() where T : GameEvent => Events.OfType<T>().ToList();
}

/// <summary>Helpers to run bytes through the decoders.</summary>
internal static class Run
{
    /// <summary>Decodes one frame body (without opcode) with the packet decoder.</summary>
    public static (List<GameEvent> Events, ProtocolDiagnostics Diag) Body(ushort opcode, string bodyHex, int depth = 0)
    {
        var events = new EventCollector();
        var diag = new ProtocolDiagnostics();
        var dec = new PacketDecoder(events, null, diag);
        var bytes = W.H(bodyHex);
        dec.OnFrame(new Frame(W.T0, opcode, bytes, depth));
        return (events.Events, diag);
    }

    /// <summary>Decodes "opcode + body" hex (the form the stacys fixtures use).</summary>
    public static (List<GameEvent> Events, ProtocolDiagnostics Diag) Payload(string payloadHex)
    {
        var bytes = W.H(payloadHex);
        return Body((ushort)((bytes[0] << 8) | bytes[1]), Convert.ToHexString(bytes[2..]));
    }

    public static T Single<T>(string payloadHex) where T : GameEvent
    {
        var (events, diag) = Payload(payloadHex);
        Assert.True(diag.DecodeErrors == 0, string.Join("\n", diag.GetRecentErrors()));
        return Assert.IsType<T>(Assert.Single(events));
    }

    /// <summary>Full pipeline over a wire stream.</summary>
    public static (EventCollector Events, ProtocolPipeline Pipeline) Wire(byte[] wire, FrameDecoderOptions? options = null)
    {
        var events = new EventCollector();
        var pipeline = new ProtocolPipeline(events, null, options);
        pipeline.Input.OnData(W.T0, wire);
        return (events, pipeline);
    }

    /// <summary>Frame decoder only.</summary>
    public static (FrameCollector Frames, FrameDecoder Decoder) Frames(byte[] wire, FrameDecoderOptions? options = null, int chunk = 0)
    {
        var frames = new FrameCollector();
        var dec = new FrameDecoder(frames, null, null, options);
        Feed(dec, wire, chunk);
        return (frames, dec);
    }

    public static void Feed(IStreamSink sink, byte[] wire, int chunk = 0)
    {
        if (chunk <= 0)
        {
            sink.OnData(W.T0, wire);
            return;
        }

        for (int i = 0; i < wire.Length; i += chunk)
            sink.OnData(W.T0, wire.AsSpan(i, Math.Min(chunk, wire.Length - i)));
    }
}
