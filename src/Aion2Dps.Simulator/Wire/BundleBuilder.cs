using System.Buffers.Binary;

namespace Aion2Dps.Simulator;

/// <summary>
/// Builds LZ4 bundles (PROTOCOL.md §5): <c>FF FF | rawSize u32 LE | LZ4 raw block</c>, whose decompressed content is
/// itself a framed stream (§4.1, with optional <c>00</c> padding).
/// </summary>
public static class BundleBuilder
{
    /// <summary>Bundle payload (without the outer length varint) wrapping already-framed bytes.</summary>
    public static byte[] BuildPayload(ReadOnlySpan<byte> innerFramedStream, bool literalOnly = false)
    {
        byte[] block = Lz4BlockCompressor.Compress(innerFramedStream, literalOnly);
        var payload = new byte[6 + block.Length];
        payload[0] = 0xFF;
        payload[1] = 0xFF;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(2), (uint)innerFramedStream.Length);
        block.CopyTo(payload.AsSpan(6));
        return payload;
    }

    /// <summary>A complete bundle frame (length varint + payload) wrapping already-framed bytes.</summary>
    public static byte[] BuildFrame(ReadOnlySpan<byte> innerFramedStream, bool literalOnly = false) =>
        Framing.Frame(BuildPayload(innerFramedStream, literalOnly));

    /// <summary>A complete bundle frame wrapping the given inner frames (each already framed), concatenated in order.</summary>
    public static byte[] BuildFrame(IEnumerable<byte[]> innerFrames, bool literalOnly = false)
    {
        var inner = new WireWriter(256);
        foreach (var frame in innerFrames) inner.Bytes(frame);
        return BuildFrame(inner.WrittenSpan, literalOnly);
    }
}
