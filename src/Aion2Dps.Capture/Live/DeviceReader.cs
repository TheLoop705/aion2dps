using Aion2Dps.Contracts;
using SharpPcap;
using SharpPcap.LibPcap;

namespace Aion2Dps.Capture;

internal delegate void FrameHandler(int sourceId, DateTime timestampUtc, int linkType, ReadOnlySpan<byte> frame);

/// <summary>Builds BPF filters for the live capture.</summary>
/// <remarks>Setting a filter on an open Npcap handle (BIOCSETF) discards the packets it has buffered, so the filter text must
/// only change when the captured set really changes: every builder returns one canonical string per set (hinted servers on
/// the game port are already covered by <c>tcp port 13328</c> and add nothing; other servers are sorted).</remarks>
public static class CaptureFilters
{
    public const string AllTcp = "tcp";

    /// <summary>Ports never used by the game whose bulk traffic (HTTP/HTTPS downloads, SMB copies) would only cost CPU in
    /// the heartbeat search and risk a false lock.</summary>
    public static readonly IReadOnlyList<int> BulkPorts = new[] { 80, 443, 445 };

    public static string GamePort(int port = GameSignature.GameServerPort) => $"tcp port {port}";

    /// <summary>The game process runs but its connection is not known yet: every TCP flow except <see cref="BulkPorts"/>
    /// (the game port is included).</summary>
    public static string Detection() => "tcp and not port 80 and not port 443 and not port 445";

    /// <summary>
    /// Filter for the given game servers (PROTOCOL.md §2.2). With <paramref name="includeGamePort"/> the whole game port
    /// is captured (<c>tcp port 13328</c>), so a reconnect to a new server is seen from its first segment (identity records)
    /// before the process table is polled again; servers on the game port then add nothing, and only servers on other
    /// ports get a <c>(host &lt;rip&gt; and port &lt;rport&gt;)</c> clause. The result is canonical: the same set of
    /// servers (in any order, with duplicates) always gives the same text.
    /// </summary>
    public static string ForServers(IEnumerable<IPv4Endpoint> servers, bool includeGamePort = true, int gamePort = GameSignature.GameServerPort)
    {
        var parts = servers
            .Where(s => !(includeGamePort && s.Port == gamePort))
            .Distinct()
            .OrderBy(s => s.Port)
            .ThenBy(s => s.AddressString, StringComparer.Ordinal)
            .Select(s => $"(host {s.AddressString} and port {s.Port})")
            .ToList();
        if (includeGamePort)
        {
            if (parts.Count == 0) return GamePort(gamePort);
            string hosts = parts.Count == 1 ? parts[0] : $"({string.Join(" or ", parts)})";
            return $"{GamePort(gamePort)} or (tcp and {hosts})";
        }

        if (parts.Count == 0) return AllTcp;
        return parts.Count == 1 ? $"tcp and {parts[0]}" : $"tcp and ({string.Join(" or ", parts)})";
    }

    /// <summary>Lower-case, whitespace-collapsed form, so equivalent spellings are not applied again.</summary>
    public static string Normalize(string? filter) =>
        string.Join(' ', (filter ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    public static bool Equivalent(string? a, string? b) => Normalize(a) == Normalize(b);
}

/// <summary>Reads one Npcap device on its own thread and hands every frame to the handler.</summary>
internal sealed class DeviceReader : IDisposable
{
    private readonly LibPcapLiveDevice _device;
    private readonly FrameHandler _handler;
    private readonly Thread _thread;
    private volatile bool _stop;
    private volatile string? _pendingFilter;
    private int _disposed;

    private DeviceReader(LibPcapLiveDevice device, AdapterInfo adapter, int sourceId, string filter, FrameHandler handler)
    {
        _device = device;
        Adapter = adapter;
        SourceId = sourceId;
        Filter = filter;
        _handler = handler;
        LinkType = (int)device.LinkType;
        _thread = new Thread(Loop) { IsBackground = true, Name = $"Aion2Dps capture {adapter.Description}" };
    }

    public AdapterInfo Adapter { get; }
    public int SourceId { get; }
    public int LinkType { get; }
    public string Filter { get; private set; }
    public string? Error { get; private set; }
    public bool IsRunning => _thread.IsAlive && !_stop;
    public long Packets { get; private set; }

    public static DeviceReader Open(AdapterInfo adapter, int sourceId, string filter, CaptureServiceOptions options, FrameHandler handler)
    {
        LibPcapLiveDevice? device = null;
        foreach (var d in LibPcapLiveDeviceList.New())
        {
            if (string.Equals(d.Name, adapter.Name, StringComparison.OrdinalIgnoreCase))
            {
                device = d;
                break;
            }
        }
        if (device is null) throw new InvalidOperationException($"Adapter '{adapter.Description}' is no longer available.");

        var config = new DeviceConfiguration
        {
            Mode = DeviceModes.None, // non-promiscuous
            ReadTimeout = options.ReadTimeoutMs,
            Snaplen = options.SnapLength,
            KernelBufferSize = options.KernelBufferBytes > 0 ? options.KernelBufferBytes : null,
        };
        config.ConfigurationFailed += (_, e) =>
            AppLog.Debug("Capture", $"Adapter {adapter.Description}: optional setting {e.Property} not applied: {e.Error}");
        device.Open(config);
        try
        {
            device.Filter = filter;
        }
        catch
        {
            device.Close();
            throw;
        }
        var reader = new DeviceReader(device, adapter, sourceId, filter, handler);
        reader._thread.Start();
        AppLog.Info("Capture", $"Opened {adapter.Description} ({LinkTypes.Name(reader.LinkType)}) filter \"{filter}\"");
        return reader;
    }

    /// <summary>Changes the BPF filter; applied by the reader thread between reads.</summary>
    /// <remarks>This discards the packets the handle has buffered (BIOCSETF): prefer opening a new handle with the new
    /// filter and retiring this one (<see cref="NpcapCaptureService"/> does that).</remarks>
    public void SetFilter(string filter)
    {
        if (_pendingFilter is null && CaptureFilters.Equivalent(filter, Filter)) return;
        _pendingFilter = filter;
    }

    private void Loop()
    {
        try
        {
            while (!_stop)
            {
                var pending = _pendingFilter;
                if (pending is not null)
                {
                    _pendingFilter = null;
                    try
                    {
                        _device.Filter = pending;
                        Filter = pending;
                        AppLog.Info("Capture", $"{Adapter.Description}: filter \"{pending}\"");
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn("Capture", $"{Adapter.Description}: cannot set filter \"{pending}\": {ex.Message}");
                    }
                }

                var status = _device.GetNextPacket(out PacketCapture capture);
                switch (status)
                {
                    case GetPacketStatus.PacketRead:
                    {
                        Packets++;
                        var tv = capture.Header.Timeval;
                        long ticks = (long)tv.Seconds * TimeSpan.TicksPerSecond + (long)tv.MicroSeconds * 10;
                        var ts = new DateTime(DateTime.UnixEpoch.Ticks + ticks, DateTimeKind.Utc);
                        _handler(SourceId, ts, LinkType, capture.Data);
                        break;
                    }
                    case GetPacketStatus.ReadTimeout:
                        break;
                    case GetPacketStatus.NoRemainingPackets:
                        _stop = true;
                        break;
                    default:
                        Error = _device.LastError is { Length: > 0 } e ? e : "capture error";
                        AppLog.Warn("Capture", $"{Adapter.Description}: {Error}");
                        _stop = true;
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            AppLog.Error("Capture", $"{Adapter.Description}: capture loop failed", ex);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop = true;
        if (_thread.IsAlive && Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(3));
        try { _device.Close(); } catch { /* already closed */ }
        AppLog.Info("Capture", $"Closed {Adapter.Description}");
    }
}
