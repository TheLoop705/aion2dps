using System.Diagnostics;
using Aion2Dps.Contracts;

namespace Aion2Dps.Capture.Tests;

public class ReplayTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 6, 21, 2, 42, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aion2dps-replay-tests", Guid.NewGuid().ToString("N"));

    public ReplayTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>
    /// Builds a realistic capture: a TLS connection to the same server port, an unrelated web flow, and the game flow
    /// (handshake, identity record, heartbeats ~19/s, damage frames split across segments, one reordering, one
    /// retransmission, encrypted client→server traffic). Returns the expected server→client game bytes.
    /// </summary>
    private static (List<(DateTime, byte[])> Frames, byte[] Expected) BuildCapture(int seconds = 3)
    {
        var frames = new List<(DateTime, byte[])>();
        var expected = new MemoryStream();
        var game = new SyntheticConnection("192.168.178.81:62311", "87.232.75.150:13328", serverIsn: 0xFFFF_F000); // wraps
        var tls = new SyntheticConnection("192.168.178.81:62312", "87.232.75.150:13328");
        var web = new SyntheticConnection("192.168.178.81:62400", "34.117.142.223:8443");
        var t = T0;

        frames.Add((t, tls.ClientSyn()));
        frames.Add((t, tls.ServerSynAck()));
        frames.Add((t, tls.ClientSend(new byte[] { 0x16, 0x03, 0x01, 0x00, 0x05, 1, 2, 3, 4, 5 })));
        frames.Add((t, tls.ServerSend(new byte[] { 0x16, 0x03, 0x03, 0x00, 0x04, 9, 9, 9, 9 })));

        frames.Add((t, game.ClientSyn()));
        frames.Add((t, game.ServerSynAck()));
        frames.Add((t, game.ClientSend(new byte[] { 0x5A, 0x11, 0x22 })));

        var stream = new MemoryStream();
        stream.Write(GameBytes.IdentityFrame);
        int damageSeed = 0;
        int beats = seconds * 19;
        for (int i = 0; i < beats; i++)
        {
            stream.Write(GameBytes.Heartbeat(i));
            if (i % 3 == 0) stream.Write(GameBytes.DamageFrame(++damageSeed, 30 + i % 200));
            if (i % 10 == 0) stream.Write(GameBytes.DamageFrame(++damageSeed, 400)); // 2-byte varint frames
        }
        var all = stream.ToArray();
        expected.Write(all);

        // Cut into segments of varying size (frames straddle segment boundaries).
        var segments = new List<byte[]>();
        int pos = 0, k = 0;
        while (pos < all.Length)
        {
            int len = Math.Min(all.Length - pos, 37 + (k++ * 71) % 900);
            segments.Add(all[pos..(pos + len)]);
            pos += len;
        }

        var frameList = segments.Select(s => game.ServerSend(s)).ToList();
        double step = seconds * 1000.0 / frameList.Count;
        for (int i = 0; i < frameList.Count; i++)
        {
            t = T0.AddMilliseconds(10 + i * step);
            if (i == 5 && i + 1 < frameList.Count)
            {
                frames.Add((t, frameList[i + 1])); // reordered
                frames.Add((t, frameList[i]));
                i++;
                continue;
            }
            frames.Add((t, frameList[i]));
            if (i == 9) frames.Add((t, frameList[i])); // retransmission
            if (i % 4 == 0) frames.Add((t, game.ClientSend(new byte[] { 0x01, 0x02, (byte)i })));
            if (i % 3 == 0)
            {
                var junk = new byte[300];
                new Random(i).NextBytes(junk);
                frames.Add((t, web.ServerSend(junk)));
                frames.Add((t, tls.ServerSend(junk)));
            }
        }
        return (frames, expected.ToArray());
    }

    private string WriteCapture(string name, CaptureFileFormat format, List<(DateTime Ts, byte[] Frame)> frames)
    {
        string path = Path.Combine(_dir, name);
        using var w = PcapFileWriter.Create(path, format);
        foreach (var (ts, f) in frames) w.WritePacket(ts, LinkTypes.Ethernet, f);
        return path;
    }

    [Theory]
    [InlineData("game.pcap", CaptureFileFormat.Pcap)]
    [InlineData("game.pcapng", CaptureFileFormat.PcapNg)]
    public async Task Replay_DeliversByteExactServerToClientStream(string name, CaptureFileFormat format)
    {
        var (frames, expected) = BuildCapture();
        string path = WriteCapture(name, format, frames);
        var sink = new CollectingSink();
        var clock = new List<DateTime>();
        var source = new PcapReplaySource();
        await source.ReplayAsync(path, sink, 0, clock.Add, CancellationToken.None);

        Assert.Equal(DiscontinuityReason.ReplayStarted, sink.Discontinuities[0]);
        Assert.Equal("ReplayStarted", sink.Events[0]);
        Assert.Single(sink.Discontinuities);
        Assert.Equal(expected.Length, sink.Data.Length);
        Assert.Equal(expected, sink.Data);
        Assert.Equal(frames.Count, clock.Count);
        Assert.True(clock.Zip(clock.Skip(1)).All(p => p.First <= p.Second));
        Assert.Equal(frames[^1].Item1.Ticks / 10, clock[^1].Ticks / 10);
        Assert.All(sink.Times, ts => Assert.True(ts >= T0 && ts <= frames[^1].Item1.AddMilliseconds(1)));

        var stats = source.LastStatistics!;
        Assert.Equal(1, stats.Locks);
        Assert.Equal("87.232.75.150:13328", stats.LastServer);
        Assert.Equal("192.168.178.81:62311", stats.LastClient);
        Assert.Equal(0, stats.Gaps);
        Assert.Equal(1, stats.TlsFlowsIgnored);
        Assert.Equal(format == CaptureFileFormat.PcapNg ? "pcapng" : "pcap", stats.Format);
    }

    [Fact]
    public void Replay_WithLostSegment_ReportsGapAndContinues()
    {
        var game = new SyntheticConnection("10.0.0.2:50000", "193.202.112.99:13328");
        var frames = new List<(DateTime, byte[])>();
        var t = T0;
        var expected = new MemoryStream();
        for (int i = 0; i < 40; i++)
        {
            var payload = GameBytes.Concat(GameBytes.Heartbeat(i), GameBytes.DamageFrame(i, 60));
            var f = game.ServerSend(payload);
            t = t.AddMilliseconds(100);
            if (i == 20) continue; // lost on the wire
            expected.Write(payload);
            frames.Add((t, f));
        }
        string path = WriteCapture("gap.pcapng", CaptureFileFormat.PcapNg, frames);
        var sink = new CollectingSink();
        var stats = PcapReplaySource.Replay(path, sink);
        Assert.Equal(new[] { DiscontinuityReason.ReplayStarted, DiscontinuityReason.TcpGap }, sink.Discontinuities);
        Assert.Equal(1, stats.Gaps);
        Assert.Equal(expected.ToArray(), sink.Data);
    }

    [Fact]
    public void Replay_HonoursCancellation()
    {
        var (frames, _) = BuildCapture(seconds: 3);
        string path = WriteCapture("cancel.pcapng", CaptureFileFormat.PcapNg, frames);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var source = new PcapReplaySource();
        var sw = Stopwatch.StartNew();
        var task = source.ReplayAsync(path, new CollectingSink(), 1.0, null, cts.Token);
        var ex = Assert.ThrowsAny<Exception>(() => task.GetAwaiter().GetResult());
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.True(task.IsCanceled);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2.5), $"took {sw.Elapsed}");
    }

    [Fact]
    public void Replay_RealTimeSpeed_PacesByCaptureTimestamps()
    {
        var (frames, expected) = BuildCapture(seconds: 1);
        string path = WriteCapture("pace.pcap", CaptureFileFormat.Pcap, frames);
        var sink = new CollectingSink();
        var sw = Stopwatch.StartNew();
        PcapReplaySource.Replay(path, sink, speed: 4.0); // ~1 s of capture at 4x ≈ 250 ms
        sw.Stop();
        Assert.Equal(expected, sink.Data);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(180), $"too fast: {sw.Elapsed}");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"too slow: {sw.Elapsed}");
    }

    [Fact]
    public void HexLog_Replay_DeliversLockedKey_SkipsCommentsAndTlsKeys()
    {
        var expected = new MemoryStream();
        var sw = new StringWriter();
        sw.WriteLine("# A2Tools-style payload log");
        sw.WriteLine("");
        var t = T0;
        // A TLS-looking stream key first.
        HexLogReader.WriteLine(sw, t, "Client:62312", new byte[] { 0x17, 0x03, 0x03, 0x00, 0x10, 0x0E, 0x00, 0x36 });
        var parts = new List<byte[]> { GameBytes.IdentityFrame };
        for (int i = 0; i < 10; i++) parts.Add(GameBytes.Concat(GameBytes.Heartbeat(i), GameBytes.DamageFrame(i, 50)));
        long epochMs = new DateTimeOffset(T0).ToUnixTimeMilliseconds();
        for (int i = 0; i < parts.Count; i++)
        {
            expected.Write(parts[i]);
            if (i % 2 == 0)
                HexLogReader.WriteLine(sw, t.AddMilliseconds(i * 50), "Client:62311", parts[i]);
            else
                sw.WriteLine($"{epochMs + i * 50}|Client:62311|{Convert.ToHexString(parts[i]).ToLowerInvariant()}");
        }
        sw.WriteLine("garbage line without separators");
        sw.WriteLine("2026-10-06T23:02:43.000+02:00|Client:62311|0E0036"); // odd → bad? no: 6 hex chars = 3 bytes
        expected.Write(new byte[] { 0x0E, 0x00, 0x36 });
        string path = Path.Combine(_dir, "session.log");
        File.WriteAllText(path, sw.ToString());

        var sink = new CollectingSink();
        var clock = new List<DateTime>();
        var stats = PcapReplaySource.Replay(path, sink, 0, clock.Add);
        Assert.Equal("hexlog", stats.Format);
        Assert.Equal(1, stats.BadLines);
        Assert.Equal(1, stats.TlsFlowsIgnored);
        Assert.Equal(DiscontinuityReason.ReplayStarted, sink.Discontinuities[0]);
        Assert.Equal(expected.ToArray(), sink.Data);
        Assert.Equal(new DateTime(2026, 10, 6, 21, 2, 43, DateTimeKind.Utc), clock[^1]);
    }

    [Fact]
    public void HexLog_ParsesTimestamps()
    {
        Assert.True(HexLogReader.TryParseTimestamp("1759784562053", out var a));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1759784562053).UtcDateTime, a);
        Assert.True(HexLogReader.TryParseTimestamp("1759784562.5", out var b));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1759784562500).UtcDateTime, b);
        Assert.True(HexLogReader.TryParseTimestamp("2026-10-04T21:00:00.123+02:00", out var c));
        Assert.Equal(new DateTime(2026, 10, 4, 19, 0, 0, 123, DateTimeKind.Utc), c);
        Assert.True(HexLogReader.TryParseTimestamp("2026-10-04T21:00:00Z", out var d));
        Assert.Equal(DateTimeKind.Utc, d.Kind);
        Assert.False(HexLogReader.TryParseTimestamp("yesterday", out _));
        Assert.True(HexLogReader.TryParseHex("0e 00 36", out var hex));
        Assert.Equal(new byte[] { 0x0E, 0x00, 0x36 }, hex);
        Assert.False(HexLogReader.TryParseHex("0e0", out _));
    }

    [Fact]
    public void Replay_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => PcapReplaySource.Replay(Path.Combine(_dir, "nope.pcapng"), new CollectingSink()));
    }
}
