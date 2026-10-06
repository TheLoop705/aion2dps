namespace Aion2Dps.Protocol;

/// <summary>Tuning knobs for <see cref="FrameDecoder"/>. Defaults follow PROTOCOL.md §4/§5.</summary>
public sealed record FrameDecoderOptions
{
    /// <summary>Largest accepted frame (varint + payload), §4.2 MAX_FRAME.</summary>
    public int MaxFrame { get; init; } = 65535;

    /// <summary>An incomplete frame larger than this is treated as a bad length unless it looks like a bundle
    /// (or carries a known opcode, see <see cref="WaitForLargeKnownFrames"/>), §4.2 MAX_WAIT.</summary>
    public int MaxWait { get; init; } = 16384;

    /// <summary>Also wait for incomplete frames above <see cref="MaxWait"/> whose opcode is known (login records can be
    /// large). The spec only exempts bundles; this keeps big real frames without stalling on garbage lengths.</summary>
    public bool WaitForLargeKnownFrames { get; init; } = true;

    /// <summary>Deepest bundle nesting unpacked (inner frames get BundleDepth 1..MaxBundleDepth), §5.</summary>
    public int MaxBundleDepth { get; init; } = 4;

    /// <summary>Largest accepted bundle rawSize (1..8 MiB), §5.</summary>
    public int MaxBundleRawSize { get; init; } = 8 * 1024 * 1024;

    /// <summary>Consecutive clean frames with known opcodes needed to accept a resync offset, §4.4.</summary>
    public int ResyncCleanFrames { get; init; } = 3;

    /// <summary>After this many bytes skipped in one resync episode, also accept a heartbeat signature
    /// (<c>0E 00 36</c>) as an alignment point, §4.4.</summary>
    public int ResyncHeartbeatFallbackBytes { get; init; } = 256 * 1024;

    /// <summary>After this many bytes skipped in one resync episode, accept any offset where a complete frame parses
    /// (byte-by-byte fallback), §4.4.</summary>
    public int ResyncByteFallbackBytes { get; init; } = 512 * 1024;

    /// <summary>Assume the first byte (and the first byte after a NewConnection/ReplayStarted/CaptureRestarted
    /// discontinuity) is a frame boundary. When false (default) the decoder verifies alignment first; a stream that
    /// starts aligned still decodes from its first frame.</summary>
    public bool StartAligned { get; init; }

    /// <summary>Scan frame bodies for embedded bundles (§5): counted as diagnostics, and identity frames
    /// (<c>33 36</c>) found inside are forwarded to the frame sink.</summary>
    public bool ScanEmbeddedBundles { get; init; } = true;

    /// <summary>Skip TLS records found at frame boundaries (§4.6, defensive).</summary>
    public bool SkipTlsRecords { get; init; } = true;
}
