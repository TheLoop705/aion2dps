using Aion2Dps.Contracts;

namespace Aion2Dps.Protocol.Tests;

/// <summary>§4 framing, §5 bundles, §4.4 resync.</summary>
public class FrameDecoderTests
{
    /// <summary>The real 520-byte Global bundle (stacys SelfCheckAion2.cs:157), payload form (FF FF + rawSize + block).</summary>
    internal const string RealBundle = "ffff08020000ff13200438b81c0400ec1b5e7d14011a02c3f8006c01000000c24e87080100200438f81b1d000150c40701001e1d0015003a0015c43a000c1b002700c51b004f200438ea53000212cd53001fea530007071b000c53001fe153000212b353001fe1530007071b00095300f100332a38f81b011335ade5cc0ae02e0001006065a020f4a0019e00f0090c5e7d1401004f576fc60aa7724600c021440d0e92f81b012e0090160538f81b09ec1b354a0120c00b2a0080332a38ea1b0113391f000f4d001415ea4d0010ea4d0010394d00118d77004f332a38e19a001c15e14d0010e14d00019a0011854d00b00e0036857120f4a0010000";

    private static readonly FrameDecoderOptions Aligned = new() { StartAligned = true };

    [Fact]
    public void Heartbeat_frame_is_11_bytes_with_signature()
    {
        var hb = W.Heartbeat();
        Assert.Equal(11, hb.Length);
        Assert.Equal(new byte[] { 0x0E, 0x00, 0x36 }, hb[..3]);
        var (frames, _) = Run.Frames(hb, Aligned);
        var f = Assert.Single(frames.Frames);
        Assert.Equal(Opcodes.Heartbeat, f.Op);
        Assert.Equal(8, f.Body.Length);
        Assert.Equal(0, f.Depth);
    }

    [Fact]
    public void One_and_two_byte_length_varints()
    {
        var small = W.Packet(0x0438, new byte[100]);   // L = 106: 1-byte varint
        var big = W.Packet(0x4136, new byte[300]);     // L = 306: 2-byte varint (B2 02)
        Assert.Equal(0x6A, small[0]);
        Assert.Equal(new byte[] { 0xB2, 0x02 }, big[..2]);
        Assert.Equal(304, big.Length); // total = L + width - 4 = 306 + 2 - 4

        var huge = W.Packet(0x0036, new byte[20_000]); // 3-byte varint
        var (frames, dec) = Run.Frames(W.Concat(small, big, huge), Aligned);
        Assert.Equal(new ushort[] { 0x0438, 0x4136, 0x0036 }, frames.Ops());
        Assert.Equal(new[] { 100, 300, 20_000 }, frames.Frames.Select(f => f.Body.Length));
        Assert.Equal(0, dec.BufferedBytes);
    }

    [Fact]
    public void Padding_between_frames_is_skipped()
    {
        var wire = W.Concat(new byte[] { 0, 0 }, W.Heartbeat(), new byte[] { 0, 0, 0 }, W.DamageFrame(1, 2, 3), new byte[] { 0 }, W.Heartbeat());
        var (frames, dec) = Run.Frames(wire);
        Assert.Equal(new ushort[] { 0x0036, 0x0438, 0x0036 }, frames.Ops());
        Assert.Equal(4, dec.Diagnostics.PaddingBytes); // the 2 leading bytes are passed over by the initial alignment check
        Assert.Equal(0, dec.Diagnostics.ResyncSkippedBytes);
    }

    [Fact]
    public void Legacy_F0_FE_byte_before_the_opcode_is_tolerated()
    {
        var payload = W.Concat(new byte[] { 0xF3 }, W.H("0438"), W.DamageBody(5, 6, 7));
        var wire = W.Concat(W.Frame(payload), W.Heartbeat());
        var (frames, dec) = Run.Frames(wire, Aligned);
        Assert.Equal(new ushort[] { 0x0438, 0x0036 }, frames.Ops());
        Assert.Equal(W.DamageBody(5, 6, 7), frames.Frames[0].Body);
        Assert.Equal(1, dec.Diagnostics.ExtraByteFrames);
    }

    [Fact]
    public void Frames_are_reported_with_the_capture_time_of_the_completing_segment()
    {
        var frames = new FrameCollector();
        var dec = new FrameDecoder(frames, null, null, Aligned);
        var hb = W.Heartbeat();
        dec.OnData(W.T0, hb.AsSpan(0, 5));
        dec.OnData(W.T0.AddMilliseconds(40), hb.AsSpan(5));
        Assert.Equal(W.T0.AddMilliseconds(40), Assert.Single(frames.Frames).Time);
        Assert.Equal(W.T0.AddMilliseconds(40), dec.Diagnostics.LastFrameUtc);
    }

    /// <summary>A stream mixing real frames, large frames, padding and bundles, for fragmentation tests.</summary>
    private static byte[] MixedStream()
    {
        var parts = new List<byte[]>
        {
            W.Heartbeat(),
            W.Frame("04388484030600be08fe26a8004f02000002433baf4101000000ba4ffd020100"),
            W.Frame("0538D490020AC522108BAF3360A804E046F600"),
            new byte[] { 0, 0 },
            W.Frame("008DCE8D0102010008DD010000000000"),
            W.Packet(0x4136, Enumerable.Range(0, 700).Select(i => (byte)(i * 7)).ToArray()),
            W.Frame(RealBundle),
            W.Bundle(W.Concat(W.DamageFrame(10, 20, 30), W.Heartbeat(), W.Bundle(W.Concat(W.DamageFrame(11, 21, 31), W.Heartbeat())))),
            W.Frame("3336ed745e91c12837064e616963686118051e000000011c0000007f0100007f0100001c000000d002040000000000"),
        };
        for (int i = 0; i < 40; i++)
        {
            parts.Add(W.DamageFrame((uint)(1000 + i), 77, (uint)(i * 13 + 1)));
            if (i % 5 == 0) parts.Add(W.Heartbeat((ulong)i));
            if (i % 9 == 0) parts.Add(W.Packet(0x0036, new byte[200 + i])); // 2-byte varint container
        }

        return W.Concat(parts.ToArray());
    }

    [Fact]
    public void Byte_by_byte_and_random_chunks_give_identical_frames()
    {
        var wire = MixedStream();
        var whole = Run.Frames(wire).Frames.Keys();
        Assert.True(whole.Count > 60);

        Assert.Equal(whole, Run.Frames(wire, chunk: 1).Frames.Keys());

        var rng = new Random(42);
        for (int round = 0; round < 25; round++)
        {
            var frames = new FrameCollector();
            var dec = new FrameDecoder(frames);
            int i = 0;
            while (i < wire.Length)
            {
                int n = Math.Min(wire.Length - i, rng.Next(1, round % 2 == 0 ? 40 : 1600));
                dec.OnData(W.T0, wire.AsSpan(i, n));
                i += n;
            }

            Assert.Equal(whole, frames.Keys());
            Assert.Equal(0, dec.BufferedBytes);
            Assert.Equal(0, dec.Diagnostics.Resyncs);
            Assert.Equal(0, dec.Diagnostics.ResyncSkippedBytes);
        }
    }

    /// <summary>The real bundle splits into exactly 20 frames consuming 520/520 bytes (§4.3, §16).</summary>
    [Fact]
    public void Real_520_byte_bundle_splits_into_20_frames()
    {
        var (frames, dec) = Run.Frames(W.Frame(RealBundle));
        Assert.Equal(20, frames.Frames.Count);
        Assert.Equal(1, dec.Diagnostics.Bundles);
        Assert.Equal(0, dec.Diagnostics.BundleErrors);
        Assert.All(frames.Frames, f => Assert.Equal(1, f.Depth));
        var ops = frames.Ops();
        Assert.Equal(10, ops.Count(o => o == 0x0438));
        Assert.Equal(3, ops.Count(o => o == 0x2A38));
        Assert.Equal(3, ops.Count(o => o == 0x0E92));
        Assert.Equal(3, ops.Count(o => o == 0x0538));
        Assert.Equal(1, ops.Count(o => o == 0x0036));
        Assert.Equal(0x0036, ops[^1]);
        // 520 bytes = Σ (varint + payload) of the 20 inner frames.
        Assert.Equal(520, frames.Frames.Sum(f => f.Body.Length + 2 + W.VarInt((uint)f.Body.Length + 2 + 4).Length));
    }

    [Fact]
    public void Real_bundle_inner_damage_amounts_are_1031_964_973_947()
    {
        var (events, pipe) = Run.Wire(W.Frame(RealBundle));
        var hits = events.Of<DamageEvent>().Where(d => d.Amount is not null).ToList();
        Assert.Equal(new long?[] { 1031, 964, 973, 947 }, hits.Select(h => h.Amount));
        Assert.All(hits, h => Assert.Equal(3564u, h.Actor));
        Assert.All(hits, h => Assert.Equal(18120030u, h.SkillId));
        Assert.All(hits, h => Assert.Equal(1, h.BundleDepth));
        Assert.Equal(6, events.Of<DamageEvent>().Count(d => d.IsCastNotice));
        Assert.Equal(3, events.Of<BuffAppliedEvent>().Count);
        Assert.Equal(3, events.Of<BuffRemovedEvent>().Count);
        var dots = events.Of<DotEvent>();
        Assert.Equal(3, dots.Count);
        Assert.All(dots, d => Assert.Equal(0x09, d.Flags));
        Assert.Equal(new long?[] { 1472, 1549, 1541 }, dots.Select(d => d.Heal));
        var hb = Assert.Single(events.Of<HeartbeatEvent>());
        Assert.All(events.Of<BuffAppliedEvent>(), b => Assert.Equal(hb.ServerUnixMs + 12_000, b.ExpiryUnixMs));
        Assert.Equal(0, pipe.Diagnostics.DecodeErrors);
        Assert.Equal(20, pipe.Diagnostics.EventsEmitted);
        Assert.Equal(20, pipe.Diagnostics.Frames);
    }

    [Fact]
    public void Nested_bundles_are_unpacked_with_depth()
    {
        var level3 = W.Bundle(W.Concat(W.DamageFrame(3, 3, 3)));
        var level2 = W.Bundle(W.Concat(W.DamageFrame(2, 2, 2), level3));
        var level1 = W.Bundle(W.Concat(W.DamageFrame(1, 1, 1), level2, W.Heartbeat()));
        var (frames, dec) = Run.Frames(W.Concat(W.Heartbeat(), level1, W.Heartbeat()));
        Assert.Equal(new[] { 0, 1, 2, 3, 1, 0 }, frames.Frames.Select(f => f.Depth));
        Assert.Equal(new ushort[] { 0x0036, 0x0438, 0x0438, 0x0438, 0x0036, 0x0036 }, frames.Ops());
        Assert.Equal(3, dec.Diagnostics.Bundles);
    }

    [Fact]
    public void Bundles_nested_deeper_than_4_are_not_unpacked()
    {
        byte[] inner = W.DamageFrame(9, 9, 9);
        for (int i = 0; i < 5; i++) inner = W.Bundle(inner);
        var (frames, dec) = Run.Frames(W.Concat(inner, W.Heartbeat(), W.Heartbeat()));
        Assert.DoesNotContain(frames.Frames, f => f.Op == 0x0438);
        Assert.Equal(4, dec.Diagnostics.Bundles);
        Assert.Equal(1, dec.Diagnostics.BundleErrors);
        Assert.Equal(2, frames.Frames.Count(f => f.Op == 0x0036 && f.Depth == 0));
    }

    [Fact]
    public void Corrupt_bundle_is_skipped_without_losing_alignment()
    {
        var innerStream = W.Concat(W.DamageFrame(1, 2, 3), W.Heartbeat());
        var good = W.Bundle(innerStream);
        // rawSize one larger than the block produces: the exact-size check rejects it.
        var bad = W.Frame(W.Concat(W.H("FFFF"), BitConverter.GetBytes((uint)innerStream.Length + 1), W.Lz4(innerStream)));
        var badSize = W.Frame(W.Concat(W.H("FFFF"), BitConverter.GetBytes(20_000_000u), W.H("00112233")));
        var (frames, dec) = Run.Frames(W.Concat(W.Heartbeat(), bad, badSize, good, W.Heartbeat()), Aligned);
        Assert.Equal(new ushort[] { 0x0036, 0x0438, 0x0036, 0x0036 }, frames.Ops());
        Assert.Equal(2, dec.Diagnostics.BundleErrors);
        Assert.Equal(1, dec.Diagnostics.Bundles);
        Assert.Equal(0, dec.Diagnostics.Resyncs);
        Assert.NotEmpty(dec.Diagnostics.GetRecentErrors());
    }

    [Fact]
    public void A_malformed_inner_frame_stops_the_bundle_walk_without_resync()
    {
        // Inner stream: a damage frame, then a bogus length (L=2 -> total < header), then a heartbeat that must not appear.
        var inner = W.Concat(W.DamageFrame(1, 2, 3), new byte[] { 0x02, 0x99 }, W.Heartbeat());
        var (frames, dec) = Run.Frames(W.Concat(W.Bundle(inner), W.Heartbeat()), Aligned);
        Assert.Equal(new ushort[] { 0x0438, 0x0036 }, frames.Ops());
        Assert.Equal(new[] { 1, 0 }, frames.Frames.Select(f => f.Depth));
        Assert.Equal(1, dec.Diagnostics.BundleInnerErrors);
        Assert.Equal(0, dec.Diagnostics.Resyncs);
    }

    [Fact]
    public void Garbage_then_resync_on_clean_frames()
    {
        var before = W.Concat(W.Heartbeat(1), W.DamageFrame(1, 1, 1), W.Heartbeat(2));
        var after = new List<byte[]>();
        for (int i = 0; i < 12; i++) after.Add(i % 3 == 0 ? W.Heartbeat((ulong)(100 + i)) : W.DamageFrame((uint)(500 + i), 7, (uint)i + 1));
        var garbage = W.H("FFFFFFFFFF80808080800102030405");
        var wire = W.Concat(before, garbage, W.Concat(after.ToArray()));

        var expected = W.Concat(before, W.Concat(after.ToArray()));
        var reference = Run.Frames(expected).Frames.Keys();

        var (frames, dec) = Run.Frames(wire);
        Assert.Equal(reference, frames.Keys());
        Assert.True(dec.Diagnostics.Resyncs >= 1);
        Assert.True(dec.Diagnostics.InvalidFrames >= 1);
        Assert.Equal(garbage.Length, dec.Diagnostics.ResyncSkippedBytes);
        Assert.True(dec.IsSynchronized);
    }

    [Fact]
    public void Random_garbage_then_resync_recovers_every_following_frame()
    {
        var rng = new Random(2026);
        for (int round = 0; round < 40; round++)
        {
            var garbage = new byte[rng.Next(50, 3000)];
            rng.NextBytes(garbage);
            var after = new List<byte[]>();
            // Realistic server clocks: an all-zero heartbeat body would read as 0x00 padding after a junk frame.
            for (int i = 0; i < 30; i++) after.Add(i % 4 == 0 ? W.Heartbeat(0x01A0F4207185UL + (ulong)i * 53) : W.DamageFrame((uint)(900 + i), 8, (uint)(i * 3 + 1)));
            var good = W.Concat(after.ToArray());
            var reference = Run.Frames(good).Frames.Keys();

            var frames = new FrameCollector();
            var dec = new FrameDecoder(frames);
            Run.Feed(dec, W.Concat(W.Heartbeat(), W.Heartbeat(), W.Heartbeat()));
            dec.OnDiscontinuity(DiscontinuityReason.TcpGap);
            Run.Feed(dec, W.Concat(garbage, good), chunk: rng.Next(1, 500));

            var keys = frames.Keys().Skip(3).ToList();
            // Every good frame is recovered, at the end, in order (garbage never produces known-opcode frames here).
            Assert.True(keys.Count >= reference.Count, $"round {round}: {keys.Count} < {reference.Count}");
            Assert.True(reference.SequenceEqual(keys.Skip(keys.Count - reference.Count)), $"round {round}");
            Assert.True(dec.IsSynchronized);
        }
    }

    [Fact]
    public void Discontinuity_drops_the_partial_frame_and_resyncs()
    {
        var frames = new FrameCollector();
        var dec = new FrameDecoder(frames, null, null, Aligned);
        var dmg = W.DamageFrame(1, 2, 3);
        dec.OnData(W.T0, dmg.AsSpan(0, 10)); // half a frame, then the rest is lost
        dec.OnDiscontinuity(DiscontinuityReason.TcpGap);
        Assert.False(dec.IsSynchronized);
        dec.OnData(W.T0, W.Concat(dmg[15..], W.Heartbeat(), W.Packet(0x4136, new byte[5]), W.Heartbeat(), W.Heartbeat()));
        Assert.Equal(new ushort[] { 0x0036, 0x4136, 0x0036, 0x0036 }, frames.Ops());
        Assert.Equal(1, dec.Diagnostics.Resyncs);
        Assert.True(dec.IsSynchronized);
    }

    [Fact]
    public void Mid_stream_start_finds_alignment()
    {
        var dmg = W.DamageFrame(1, 2, 3);
        var wire = W.Concat(dmg[5..], W.Heartbeat(), W.Packet(0x4136, new byte[5]), W.Heartbeat(), W.DamageFrame(4, 5, 6));
        var (frames, _) = Run.Frames(wire);
        Assert.Equal(new ushort[] { 0x0036, 0x4136, 0x0036, 0x0438 }, frames.Ops());
    }

    [Fact]
    public void Heartbeat_signature_fallback_when_opcodes_are_unknown()
    {
        // A stream of frames with unknown opcodes (as after a patch) behind garbage: the clean-run check cannot pass,
        // so after the fallback threshold the heartbeat signature is used.
        var opts = new FrameDecoderOptions { ResyncHeartbeatFallbackBytes = 64, ResyncByteFallbackBytes = 1 << 20 };
        var parts = new List<byte[]> { Enumerable.Repeat((byte)0x01, 200).ToArray() };
        for (int i = 0; i < 10; i++)
        {
            parts.Add(W.Packet(0x7777, new byte[] { (byte)i }));
            if (i == 5) parts.Add(W.Heartbeat());
        }

        var (frames, dec) = Run.Frames(W.Concat(parts.ToArray()), opts);
        Assert.Equal(0x0036, frames.Frames[0].Op);
        Assert.Equal(4, frames.Frames.Count(f => f.Op == 0x7777));
        Assert.True(dec.IsSynchronized);
    }

    [Fact]
    public void Byte_by_byte_fallback_accepts_any_parsable_frame()
    {
        var opts = new FrameDecoderOptions { ResyncHeartbeatFallbackBytes = 16, ResyncByteFallbackBytes = 32 };
        var parts = new List<byte[]> { Enumerable.Repeat((byte)0x01, 100).ToArray() };
        for (int i = 0; i < 5; i++) parts.Add(W.Packet(0x7777, new byte[] { (byte)i, 1, 2 }));
        var (frames, _) = Run.Frames(W.Concat(parts.ToArray()), opts);
        Assert.Equal(5, frames.Frames.Count(f => f.Op == 0x7777));
    }

    [Fact]
    public void Oversize_length_is_rejected_and_followed_by_resync()
    {
        // L = 70,000 -> total > MAX_FRAME (65,535).
        var oversize = W.Concat(W.VarInt(70_000), W.H("7777"), new byte[10]);
        var after = W.Concat(W.Heartbeat(), W.DamageFrame(1, 2, 3), W.Heartbeat(), W.Heartbeat());
        var (frames, dec) = Run.Frames(W.Concat(W.Heartbeat(), oversize, after), Aligned);
        Assert.Equal(new ushort[] { 0x0036, 0x0036, 0x0438, 0x0036, 0x0036 }, frames.Ops());
        Assert.True(dec.Diagnostics.InvalidFrames >= 1);
        Assert.True(dec.Diagnostics.Resyncs >= 1);
    }

    [Fact]
    public void Resync_is_not_stalled_by_a_false_candidate_announcing_a_long_known_frame()
    {
        // After the bad length, the bytes "A2 04 04 38" read as a 544-byte damage frame that never completes; the
        // heartbeat run after it is fully verifiable and must win without waiting for 544 bytes.
        var oversize = W.Concat(W.VarInt(70_000), W.H("0438"), new byte[10]);
        var after = W.Concat(W.Heartbeat(), W.DamageFrame(1, 2, 3), W.Heartbeat(), W.Heartbeat());
        var (frames, dec) = Run.Frames(W.Concat(W.Heartbeat(), oversize, after), Aligned);
        Assert.Equal(new ushort[] { 0x0036, 0x0036, 0x0438, 0x0036, 0x0036 }, frames.Ops());
        Assert.True(dec.IsSynchronized);
        Assert.Equal(0, dec.BufferedBytes);
    }

    [Fact]
    public void Incomplete_frame_above_max_wait_is_rejected_unless_bundle_or_known()
    {
        // Unknown opcode, 20 KB announced, only a few bytes present: treated as a bad length (§4.2 MAX_WAIT).
        var bogus = W.Concat(W.VarInt(20_004), W.H("7777"), new byte[3]);
        var dec = new FrameDecoder(new FrameCollector(), null, null, Aligned);
        dec.OnData(W.T0, W.Concat(W.Heartbeat(), bogus));
        Assert.True(dec.Diagnostics.InvalidFrames >= 1);

        // The same size with a bundle opcode, or a known opcode, waits for the rest.
        foreach (var op in new[] { "FFFF", "4136" })
        {
            var d2 = new FrameDecoder(new FrameCollector(), null, null, Aligned);
            d2.OnData(W.T0, W.Concat(W.VarInt(20_004), W.H(op), new byte[3]));
            Assert.Equal(0, d2.Diagnostics.InvalidFrames);
            Assert.True(d2.BufferedBytes > 0);
        }
    }

    [Fact]
    public void Large_bundle_split_over_many_segments()
    {
        var rng = new Random(5);
        var inner = new List<byte[]>();
        for (int i = 0; i < 2000; i++) inner.Add(W.DamageFrame((uint)rng.Next(1, 60000), (uint)rng.Next(1, 60000), (uint)rng.Next(1, 100000)));
        var bundle = W.Bundle(W.Concat(inner.ToArray()));
        Assert.True(bundle.Length > 16384);
        var (frames, dec) = Run.Frames(W.Concat(bundle, W.Heartbeat()), chunk: 1460);
        Assert.Equal(2001, frames.Frames.Count);
        Assert.Equal(1, dec.Diagnostics.Bundles);
    }

    [Fact]
    public void Tls_records_at_frame_boundaries_are_skipped()
    {
        var tls = W.Concat(W.H("1703030010"), new byte[16]);
        var (frames, dec) = Run.Frames(W.Concat(W.Heartbeat(), tls, W.DamageFrame(1, 2, 3)), Aligned);
        Assert.Equal(new ushort[] { 0x0036, 0x0438 }, frames.Ops());
        Assert.Equal(1, dec.Diagnostics.TlsRecordsSkipped);
    }

    [Fact]
    public void Embedded_bundle_identity_frames_are_forwarded_and_others_counted()
    {
        var self = W.Frame("3336ed745e91c12837064e616963686118051e000000011c0000007f0100007f0100001c000000d002040000000000");
        var embeddedInner = W.Concat(self, W.DamageFrame(1, 2, 3));
        var embedded = W.Bundle(embeddedInner);
        var carrier = W.Packet(0x1234, W.Concat(W.H("0102030405"), embedded, W.H("0607")));

        var events = new EventCollector();
        var pipeline = new ProtocolPipeline(events, null, Aligned);
        pipeline.Input.OnData(W.T0, W.Concat(carrier, W.Heartbeat()));

        var d = pipeline.ProtocolDiagnostics;
        Assert.Equal(1, d.EmbeddedBundles);
        Assert.Equal(1, d.EmbeddedFramesForwarded);
        Assert.Equal(1, d.EmbeddedFramesIgnored);
        Assert.Equal(0, d.Bundles);
        var s = Assert.Single(events.Of<SelfInfoEvent>());
        Assert.Equal("Naicha", s.Name);
        Assert.Equal(1, s.BundleDepth);
        Assert.Empty(events.Of<DamageEvent>()); // embedded damage is diagnostic only (§5)
    }

    [Fact]
    public void Embedded_scan_can_be_disabled_and_ignores_ff_runs()
    {
        var embedded = W.Bundle(W.Frame("3336ed745e91c12837064e616963686118051e000000011c000000"));
        var carrier = W.Packet(0x1234, W.Concat(W.H("01"), embedded));
        var (events, pipe) = Run.Wire(W.Concat(carrier, W.Heartbeat()), new FrameDecoderOptions { StartAligned = true, ScanEmbeddedBundles = false });
        Assert.Empty(events.Of<SelfInfoEvent>());
        Assert.Equal(0, pipe.ProtocolDiagnostics.EmbeddedBundles);

        // A spawn full of FF runs yields no false embedded bundle.
        var spawn = W.Frame("4136A5AE011F1000C18E2C0000020028A04500788245008031440E86B1437AFC01DF28DF2819070000190700000000000000000000000000005892010064000000F04902000100000000000000A08601000000000090D00300010111010F329A09FFFFFFFFFFFFFFFF8075D52ABB0300008E5509022B5A9B45CADA7B4590C525440702068E2A000002CD00C4040000D0003D0100001E000000E31D030000");
        var (_, p2) = Run.Wire(W.Concat(spawn, W.Heartbeat()), Aligned);
        Assert.Equal(0, p2.ProtocolDiagnostics.EmbeddedBundles);
    }

    [Fact]
    public void Census_counts_frames_and_bytes_per_opcode()
    {
        var (_, pipe) = Run.Wire(W.Concat(W.Heartbeat(), W.Heartbeat(), W.Frame(RealBundle), W.Packet(0x1234, new byte[7])), Aligned);
        var census = pipe.Diagnostics.GetCensus().ToDictionary(c => c.Opcode);
        Assert.Equal(3, census[0x0036].Count);
        Assert.Equal(24, census[0x0036].Bytes);
        Assert.Equal(3, census[0x0036].Decoded);
        Assert.Equal(10, census[0x0438].Count);
        Assert.Equal(10, census[0x0438].Decoded);
        Assert.Equal(1, census[0xFFFF].Count);
        Assert.Equal(1, census[0x1234].Count);
        Assert.Equal(0, census[0x1234].Decoded);
        Assert.Equal("04 38", census[0x0438].Hex);
    }

    [Fact]
    public void Sink_exceptions_never_escape()
    {
        var dec = new FrameDecoder(new ThrowingFrameSink(), null, null, Aligned);
        dec.OnData(W.T0, W.Concat(W.Heartbeat(), W.Heartbeat()));
        Assert.Equal(2, dec.Diagnostics.SinkErrors);

        var pipeline = new ProtocolPipeline(new ThrowingEventSink(), null, Aligned);
        pipeline.Input.OnData(W.T0, W.Concat(W.Heartbeat(), W.Frame(RealBundle)));
        Assert.Equal(21, pipeline.ProtocolDiagnostics.SinkErrors);
    }

    [Fact]
    public void Random_input_never_throws()
    {
        var rng = new Random(99);
        var pipeline = new ProtocolPipeline(new EventCollector());
        for (int i = 0; i < 300; i++)
        {
            var junk = new byte[rng.Next(1, 4000)];
            rng.NextBytes(junk);
            if (i % 3 == 0) junk[0] = 0x0E;
            pipeline.Input.OnData(W.T0, junk);
            if (i % 50 == 0) pipeline.Input.OnDiscontinuity(DiscontinuityReason.TcpGap);
        }

        // And it still decodes real data afterwards.
        pipeline.Input.OnDiscontinuity(DiscontinuityReason.NewConnection);
        var events = new EventCollector();
        var p2 = new ProtocolPipeline(events);
        p2.Input.OnData(W.T0, W.Frame(RealBundle));
        Assert.Equal(4, events.Of<DamageEvent>().Count(d => d.Amount is not null));
    }

    private sealed class ThrowingFrameSink : IFrameSink
    {
        public void OnFrame(in Frame frame) => throw new InvalidOperationException("boom");
    }

    private sealed class ThrowingEventSink : IGameEventSink
    {
        public void OnEvent(GameEvent gameEvent) => throw new InvalidOperationException("boom");
    }
}
