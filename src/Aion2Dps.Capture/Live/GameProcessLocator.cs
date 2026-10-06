using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Aion2Dps.Contracts;

namespace Aion2Dps.Capture;

/// <summary>A running AION 2 process.</summary>
public sealed record GameProcessInfo(int ProcessId, string ProcessName);

/// <summary>One row of the IPv4 TCP connection table.</summary>
public readonly record struct TcpConnectionEntry(IPv4Endpoint Local, IPv4Endpoint Remote, TcpState State, int ProcessId);

/// <summary>An ESTABLISHED TCP connection of a game process that may be the game connection.</summary>
public sealed record GameConnection(int ProcessId, string ProcessName, IPv4Endpoint Local, IPv4Endpoint Remote)
{
    public bool IsGamePort => Remote.Port == GameSignature.GameServerPort;
    public bool IsLoopback => Remote.IsLoopback;
    public FlowHint Hint => new(Local, Remote);
    public override string ToString() => $"{ProcessName}({ProcessId}) {Local} -> {Remote}";
}

/// <summary>Result of one <see cref="IGameConnectionLocator.Locate"/> call.</summary>
public sealed record GameLocatorResult(IReadOnlyList<GameProcessInfo> Processes, IReadOnlyList<GameConnection> Candidates)
{
    public static readonly GameLocatorResult Empty = new(Array.Empty<GameProcessInfo>(), Array.Empty<GameConnection>());
    public bool ProcessFound => Processes.Count > 0;
    /// <summary>The best candidate (remote port 13328 first), or null.</summary>
    public GameConnection? Best => Candidates.Count > 0 ? Candidates[0] : null;
}

/// <summary>Finds the game process and its connections (injectable for tests).</summary>
public interface IGameConnectionLocator
{
    GameLocatorResult Locate();
    bool IsProcessAlive(int processId);
}

/// <summary>
/// Finds AION 2 processes and their ESTABLISHED IPv4 TCP connections via iphlpapi <c>GetExtendedTcpTable</c>
/// (<c>TCP_TABLE_OWNER_PID_ALL</c>). Works without administrator rights (PROTOCOL.md §2.2).
/// </summary>
public sealed class GameProcessLocator : IGameConnectionLocator
{
    /// <summary>Process names (without .exe) of the game client.</summary>
    public static readonly IReadOnlyList<string> ProcessNames = ["AION2", "Aion2", "AION2-Win64-Shipping"];

    /// <summary>Remote ports that are never the game connection (web/launcher traffic).</summary>
    public static readonly IReadOnlySet<int> IgnoredRemotePorts = new HashSet<int> { 80, 443 };

    public GameLocatorResult Locate()
    {
        try
        {
            var processes = FindGameProcesses();
            if (processes.Count == 0) return GameLocatorResult.Empty;
            var table = GetTcpConnections();
            return new GameLocatorResult(processes, RankCandidates(table, processes));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Capture", $"Game connection lookup failed: {ex.Message}");
            return GameLocatorResult.Empty;
        }
    }

    public bool IsProcessAlive(int processId)
    {
        try
        {
            using var p = Process.GetProcessById(processId);
            return IsGameProcessName(p.ProcessName);
        }
        catch (ArgumentException)
        {
            return false; // not running
        }
        catch (InvalidOperationException)
        {
            return false; // exited
        }
        catch
        {
            return true; // access problems: assume alive, the watchdog still applies
        }
    }

    public static bool IsGameProcessName(string? name) =>
        name is not null && ProcessNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>All running processes with a game process name.</summary>
    public static IReadOnlyList<GameProcessInfo> FindGameProcesses()
    {
        var result = new List<GameProcessInfo>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (IsGameProcessName(p.ProcessName)) result.Add(new GameProcessInfo(p.Id, p.ProcessName));
            }
            catch
            {
                // process exited while enumerating
            }
            finally
            {
                p.Dispose();
            }
        }
        return result;
    }

    /// <summary>
    /// Picks game-connection candidates from a connection table: ESTABLISHED rows owned by a game process, remote port not
    /// 80/443, remote address not 0.0.0.0, and not a loopback connection between two sockets of game processes
    /// (internal IPC). Ordered: remote port 13328 first, then non-loopback, then by local port.
    /// </summary>
    public static IReadOnlyList<GameConnection> RankCandidates(IEnumerable<TcpConnectionEntry> table, IReadOnlyList<GameProcessInfo> processes)
    {
        var names = new Dictionary<int, string>();
        foreach (var p in processes) names[p.ProcessId] = p.ProcessName;
        var rows = table as IReadOnlyCollection<TcpConnectionEntry> ?? table.ToList();
        var gameLocalEndpoints = new HashSet<IPv4Endpoint>();
        foreach (var r in rows)
            if (names.ContainsKey(r.ProcessId)) gameLocalEndpoints.Add(r.Local);

        var list = new List<GameConnection>();
        foreach (var r in rows)
        {
            if (r.State != TcpState.Established) continue;
            if (!names.TryGetValue(r.ProcessId, out var name)) continue;
            if (IgnoredRemotePorts.Contains(r.Remote.Port)) continue;
            if (r.Remote.Address == 0 || r.Remote.Port == 0) continue;
            if (r.Remote.IsLoopback && gameLocalEndpoints.Contains(r.Remote)) continue; // game <-> game IPC
            list.Add(new GameConnection(r.ProcessId, name, r.Local, r.Remote));
        }
        return list
            .OrderByDescending(c => c.IsGamePort)
            .ThenBy(c => c.IsLoopback)
            .ThenBy(c => c.Local.Port)
            .ToList();
    }

    // ───────────────────────────── iphlpapi ─────────────────────────────

    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;
    private const uint NoError = 0;
    private const uint ErrorInsufficientBuffer = 122;

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf, int tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    /// <summary>The full IPv4 TCP connection table with owning process ids.</summary>
    public static IReadOnlyList<TcpConnectionEntry> GetTcpConnections()
    {
        int size = 0;
        uint rc = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (size <= 0) size = 64 * 1024;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                rc = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
                if (rc == ErrorInsufficientBuffer) continue;
                if (rc != NoError) throw new InvalidOperationException($"GetExtendedTcpTable failed with error {rc}.");
                int count = Marshal.ReadInt32(buffer);
                int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
                var result = new List<TcpConnectionEntry>(count);
                IntPtr row = buffer + 4;
                for (int i = 0; i < count; i++, row += rowSize)
                {
                    var r = Marshal.PtrToStructure<MibTcpRowOwnerPid>(row);
                    result.Add(new TcpConnectionEntry(
                        new IPv4Endpoint(FromNetworkAddress(r.LocalAddr), FromNetworkPort(r.LocalPort)),
                        new IPv4Endpoint(FromNetworkAddress(r.RemoteAddr), FromNetworkPort(r.RemotePort)),
                        (TcpState)r.State,
                        (int)r.OwningPid));
                }
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        throw new InvalidOperationException("GetExtendedTcpTable: the table kept growing.");
    }

    /// <summary>dwAddr holds the address bytes in network order in memory (little-endian host: a.b.c.d = d&lt;&lt;24|...|a).</summary>
    private static uint FromNetworkAddress(uint dw) =>
        (uint)IPAddress.NetworkToHostOrder(unchecked((int)dw));

    private static ushort FromNetworkPort(uint dw) => (ushort)(((dw & 0xFF) << 8) | ((dw >> 8) & 0xFF));
}
