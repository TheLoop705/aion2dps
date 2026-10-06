using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using Aion2Dps.Contracts;

namespace Aion2Dps.Capture.Tests;

public class NpcapCaptureServiceTests
{
    private sealed class FakeLocator : IGameConnectionLocator
    {
        public GameLocatorResult Locate() => GameLocatorResult.Empty;
        public bool IsProcessAlive(int processId) => false;
    }

    private static CaptureStatus WaitForState(NpcapCaptureService svc, BlockingCollection<CaptureStatus> seen, CaptureState state, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (svc.Status.State == state) return svc.Status;
            if (seen.TryTake(out var s, 50) && s.State == state) return s;
        }
        return svc.Status;
    }

    [Fact]
    public void InjectedMissingNpcap_ReportsNpcapMissing_WithoutThrowing()
    {
        var options = new CaptureServiceOptions
        {
            NpcapProbe = () => new NpcapCheckResult(false, "Npcap is not installed (test).", null, 0),
            Locator = new FakeLocator(),
        };
        using var svc = new NpcapCaptureService(options);
        var seen = new BlockingCollection<CaptureStatus>();
        svc.StatusChanged += s => seen.Add(s);
        var sink = new CollectingSink();
        svc.Start(sink);
        svc.Start(sink); // idempotent
        var status = WaitForState(svc, seen, CaptureState.NpcapMissing, TimeSpan.FromSeconds(5));
        Assert.Equal(CaptureState.NpcapMissing, status.State);
        Assert.Equal("Npcap is not installed (test).", status.Message);
        Assert.Contains(DiscontinuityReason.CaptureRestarted, sink.Discontinuities);
        svc.Stop();
        svc.Stop(); // idempotent
        Assert.Equal(CaptureState.Stopped, svc.Status.State);
        svc.Start(sink); // restart after stop
        Assert.Equal(CaptureState.NpcapMissing, WaitForState(svc, seen, CaptureState.NpcapMissing, TimeSpan.FromSeconds(5)).State);
        svc.Dispose();
        svc.Dispose(); // safe
        Assert.Throws<ObjectDisposedException>(() => svc.Start(sink));
    }

    [Fact]
    public void ThrowingProbe_IsReportedAsMissing()
    {
        var options = new CaptureServiceOptions { NpcapProbe = () => throw new DllNotFoundException("wpcap"), Locator = new FakeLocator() };
        using var svc = new NpcapCaptureService(options);
        var seen = new BlockingCollection<CaptureStatus>();
        svc.StatusChanged += s => seen.Add(s);
        svc.Start(new CollectingSink());
        var status = WaitForState(svc, seen, CaptureState.NpcapMissing, TimeSpan.FromSeconds(5));
        Assert.Equal(CaptureState.NpcapMissing, status.State);
        Assert.Contains("wpcap", status.Message);
    }

    [Fact]
    public void OnThisMachine_WithoutNpcap_ServiceReportsNpcapMissing()
    {
        var check = NpcapAvailability.Check();
        if (check.IsAvailable) return; // Npcap installed: covered by the injected-probe test
        Assert.False(string.IsNullOrWhiteSpace(check.Reason));
        Assert.Null(NpcapAvailability.FindWpcapDll());
        Assert.Contains("Npcap", check.Reason);

        using var svc = new NpcapCaptureService();
        Assert.Empty(svc.GetAdapters());
        Assert.Empty(AdapterSelector.ListAdapters());
        var seen = new BlockingCollection<CaptureStatus>();
        svc.StatusChanged += s => seen.Add(s);
        svc.Start(new CollectingSink());
        var status = WaitForState(svc, seen, CaptureState.NpcapMissing, TimeSpan.FromSeconds(5));
        Assert.Equal(CaptureState.NpcapMissing, status.State);
        Assert.Contains("npcap.com", status.Message);

        // Recording without capture still works and produces a valid (empty) file.
        string path = Path.Combine(Path.GetTempPath(), "aion2dps-capture-tests", Guid.NewGuid().ToString("N"), "rec.pcapng");
        svc.StartRecording(path);
        Assert.Equal(Path.GetFullPath(path), svc.Status.RecordingPath);
        svc.StopRecording();
        Assert.Null(svc.Status.RecordingPath);
        using (var r = new PcapFileReader(path)) Assert.Empty(r.ReadAll());
        Directory.Delete(Path.GetDirectoryName(path)!, true);
        svc.Stop();
    }

    private sealed class ScriptedLocator(GameLocatorResult result) : IGameConnectionLocator
    {
        public GameLocatorResult Locate() => result;
        public bool IsProcessAlive(int processId) => true;
    }

    [Fact]
    public void Supervisor_WaitingForGame_WhenNoProcess()
    {
        var options = new CaptureServiceOptions
        {
            NpcapProbe = () => new NpcapCheckResult(true, "ok (test)", null, 1),
            Locator = new ScriptedLocator(GameLocatorResult.Empty),
        };
        using var svc = new NpcapCaptureService(options);
        var seen = new BlockingCollection<CaptureStatus>();
        svc.StatusChanged += s => seen.Add(s);
        svc.Start(new CollectingSink());
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !svc.Status.Message.Contains("Start the game")) Thread.Sleep(20);
        Assert.Equal(CaptureState.WaitingForGame, svc.Status.State);
        Assert.Contains("Waiting for AION 2", svc.Status.Message);
    }

    [Fact]
    public void Supervisor_Detecting_ReportsServerEndpointAndProcess()
    {
        var conn = new GameConnection(43532, "AION2", IPv4Endpoint.Parse("192.168.178.81:61283"), IPv4Endpoint.Parse("87.232.75.150:13328"));
        var options = new CaptureServiceOptions
        {
            NpcapProbe = () => new NpcapCheckResult(true, "ok (test)", null, 1),
            Locator = new ScriptedLocator(new GameLocatorResult(new[] { new GameProcessInfo(43532, "AION2") }, new[] { conn })),
        };
        using var svc = new NpcapCaptureService(options);
        var seen = new BlockingCollection<CaptureStatus>();
        svc.StatusChanged += s => seen.Add(s);
        svc.Start(new CollectingSink());
        var status = WaitForState(svc, seen, CaptureState.Detecting, TimeSpan.FromSeconds(5));
        Assert.Equal(CaptureState.Detecting, status.State);
        Assert.Equal("87.232.75.150:13328", status.ServerEndpoint);
        Assert.Equal("192.168.178.81:61283", status.LocalEndpoint);
        Assert.Equal(43532, status.GameProcessId);
        Assert.Contains("87.232.75.150:13328", status.Message);

        svc.AdapterOverride = "No Such Adapter";
        status = WaitForState(svc, seen, CaptureState.Error, TimeSpan.FromSeconds(5));
        Assert.Equal(CaptureState.Error, status.State);
        Assert.Contains("No Such Adapter", status.Message);
    }

    [Fact]
    public void AdapterOverride_IsNormalised()
    {
        using var svc = new NpcapCaptureService(new CaptureServiceOptions { Locator = new FakeLocator() });
        svc.AdapterOverride = "  Ethernet ";
        Assert.Equal("Ethernet", svc.AdapterOverride);
        svc.AdapterOverride = " ";
        Assert.Null(svc.AdapterOverride);
    }
}

public class AdapterSelectorTests
{
    private static readonly AdapterInfo Ethernet = new(@"\Device\NPF_{11111111-2222-3333-4444-555555555555}",
        "Ethernet (Realtek PCIe 2.5GbE Family Controller)", new[] { "192.168.178.81" }, false);
    private static readonly AdapterInfo Tailscale = new(@"\Device\NPF_{AAAAAAAA-2222-3333-4444-555555555555}",
        "Tailscale (Tailscale Tunnel)", new[] { "100.101.102.103" }, false);
    private static readonly AdapterInfo HyperV = new(@"\Device\NPF_{BBBBBBBB-2222-3333-4444-555555555555}",
        "vEthernet (Default Switch) (Hyper-V Virtual Ethernet Adapter)", new[] { "172.20.0.1" }, false);
    private static readonly AdapterInfo Usb = new(@"\Device\NPF_{CCCCCCCC-2222-3333-4444-555555555555}",
        "Ethernet 3 (USB Ethernet)", Array.Empty<string>(), false);
    private static readonly AdapterInfo Loopback = new(@"\Device\NPF_Loopback", "Adapter for loopback traffic capture",
        Array.Empty<string>(), true);
    private static readonly IReadOnlyList<AdapterInfo> All = new[] { HyperV, Tailscale, Usb, Ethernet, Loopback };

    [Fact]
    public void SelectsAdapterByLocalAddress()
    {
        var sel = AdapterSelector.Select(All, IPAddress.Parse("192.168.178.81"), null);
        Assert.Same(Ethernet, sel.Adapter);
        Assert.Same(Tailscale, AdapterSelector.Select(All, IPAddress.Parse("100.101.102.103"), null).Adapter);
        Assert.Null(AdapterSelector.Select(All, IPAddress.Parse("10.9.9.9"), null).Adapter);
    }

    [Fact]
    public void LoopbackConnection_UsesLoopbackAdapter()
    {
        Assert.Same(Loopback, AdapterSelector.Select(All, IPAddress.Parse("127.0.0.1"), null).Adapter);
        Assert.Same(Loopback, AdapterSelector.FindForLocalAddress(All, IPAddress.Loopback));
    }

    [Theory]
    [InlineData("Ethernet")]
    [InlineData("ethernet")]
    [InlineData("Realtek PCIe 2.5GbE Family Controller")]
    [InlineData("Ethernet (Realtek PCIe 2.5GbE Family Controller)")]
    [InlineData(@"\Device\NPF_{11111111-2222-3333-4444-555555555555}")]
    [InlineData("{11111111-2222-3333-4444-555555555555}")]
    public void ManualOverride_WinsOverAddress(string name)
    {
        var sel = AdapterSelector.Select(All, IPAddress.Parse("100.101.102.103"), name);
        Assert.Same(Ethernet, sel.Adapter);
    }

    [Fact]
    public void UnknownOverride_SelectsNothing()
    {
        var sel = AdapterSelector.Select(All, IPAddress.Parse("192.168.178.81"), "Wi-Fi");
        Assert.Null(sel.Adapter);
        Assert.Contains("Wi-Fi", sel.Reason);
        Assert.False(AdapterSelector.MatchesOverride(Usb, "Ethernet")); // "Ethernet 3" is not "Ethernet"
    }

    [Fact]
    public void ScanSet_UsesAdaptersWithIPv4()
    {
        var set = AdapterSelector.SelectScanSet(All, null, includeLoopback: false);
        Assert.Equal(new[] { HyperV, Tailscale, Ethernet }, set);
        Assert.Contains(Loopback, AdapterSelector.SelectScanSet(All, null, includeLoopback: true));
        Assert.Equal(new[] { Ethernet }, AdapterSelector.SelectScanSet(All, "Ethernet", includeLoopback: true));
    }

    [Fact]
    public void Filters_FollowTheSpec()
    {
        var server = IPv4Endpoint.Parse("87.232.75.150:13328");
        Assert.Equal("tcp and (host 87.232.75.150 and port 13328)", CaptureFilters.ForServers(new[] { server }, includeGamePort: false));
        Assert.Equal("tcp and ((host 87.232.75.150 and port 13328) or port 13328)", CaptureFilters.ForServers(new[] { server }));
        Assert.Equal("tcp port 13328", CaptureFilters.GamePort());
        Assert.Equal("tcp", CaptureFilters.ForServers(Array.Empty<IPv4Endpoint>(), includeGamePort: false));
    }
}

public class GameProcessLocatorTests
{
    private static TcpConnectionEntry Row(string local, string remote, int pid, TcpState state = TcpState.Established) =>
        new(IPv4Endpoint.Parse(local), IPv4Endpoint.Parse(remote), state, pid);

    [Fact]
    public void RankCandidates_PrefersGamePort_SkipsWebAndInternalLoopback()
    {
        var procs = new[] { new GameProcessInfo(43532, "AION2"), new GameProcessInfo(39000, "AION2") };
        var table = new[]
        {
            Row("127.0.0.1:59508", "127.0.0.1:59509", 43532),
            Row("127.0.0.1:59509", "127.0.0.1:59508", 43532),
            Row("192.168.178.81:54506", "20.113.126.57:443", 43532),
            Row("192.168.178.81:54811", "216.107.254.84:80", 43532),
            Row("192.168.178.81:55000", "193.202.112.99:13700", 43532),
            Row("192.168.178.81:61283", "87.232.75.150:13328", 43532),
            Row("192.168.178.81:61290", "87.232.75.150:13328", 43532, TcpState.TimeWait),
            Row("192.168.178.81:50000", "1.1.1.1:13328", 1234), // other process
            Row("0.0.0.0:38600", "0.0.0.0:0", 43532, TcpState.Listen),
        };
        var ranked = GameProcessLocator.RankCandidates(table, procs);
        Assert.Equal(2, ranked.Count);
        Assert.Equal("87.232.75.150:13328", ranked[0].Remote.ToString());
        Assert.Equal("192.168.178.81:61283", ranked[0].Local.ToString());
        Assert.Equal(43532, ranked[0].ProcessId);
        Assert.True(ranked[0].IsGamePort);
        Assert.Equal(13700, ranked[1].Remote.Port);
    }

    [Fact]
    public void RankCandidates_KeepsPingReducerLoopbackRelay()
    {
        var procs = new[] { new GameProcessInfo(1, "AION2-Win64-Shipping") };
        var table = new[] { Row("127.0.0.1:50001", "127.0.0.1:38600", 1), Row("127.0.0.1:38600", "127.0.0.1:50001", 999) };
        var ranked = GameProcessLocator.RankCandidates(table, procs);
        Assert.Single(ranked);
        Assert.True(ranked[0].IsLoopback);
    }

    [Fact]
    public void ProcessNames_MatchCaseInsensitively()
    {
        Assert.True(GameProcessLocator.IsGameProcessName("AION2"));
        Assert.True(GameProcessLocator.IsGameProcessName("aion2"));
        Assert.True(GameProcessLocator.IsGameProcessName("AION2-Win64-Shipping"));
        Assert.False(GameProcessLocator.IsGameProcessName("AION2Launcher"));
        Assert.False(GameProcessLocator.IsGameProcessName(null));
    }

    [Fact]
    public void TcpTable_CanBeReadWithoutAdmin()
    {
        var table = GameProcessLocator.GetTcpConnections();
        Assert.NotEmpty(table);
        // Cross-check with the managed API: every managed ESTABLISHED IPv4 connection appears in our table.
        var ours = table.Where(r => r.State == TcpState.Established).Select(r => (r.Local.ToString(), r.Remote.ToString())).ToHashSet();
        var managed = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
            .Where(c => c.State == TcpState.Established && c.LocalEndPoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(c => (c.LocalEndPoint.ToString(), c.RemoteEndPoint.ToString()))
            .ToList();
        int found = managed.Count(m => ours.Contains(m));
        Assert.True(found >= managed.Count * 0.8, $"{found}/{managed.Count} connections matched");
    }

    [SkippableFact]
    public void Live_FindsGameConnectionOnPort13328_WhenAion2IsRunning()
    {
        var processes = GameProcessLocator.FindGameProcesses();
        Skip.If(processes.Count == 0, "AION 2 is not running on this machine.");
        var result = new GameProcessLocator().Locate();
        Assert.True(result.ProcessFound);
        Assert.True(result.Candidates.Any(c => c.Remote.Port == GameSignature.GameServerPort),
            "AION2 is running but no ESTABLISHED connection to port 13328 was found. Candidates: " +
            string.Join(", ", result.Candidates));
        var best = result.Best!;
        Assert.Equal(GameSignature.GameServerPort, best.Remote.Port);
        Assert.Contains(processes, p => p.ProcessId == best.ProcessId);
        Assert.True(new GameProcessLocator().IsProcessAlive(best.ProcessId));
    }
}
