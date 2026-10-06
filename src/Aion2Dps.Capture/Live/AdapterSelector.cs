using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Aion2Dps.Contracts;

namespace Aion2Dps.Capture;

/// <summary>Outcome of <see cref="AdapterSelector.Select"/>.</summary>
public sealed record AdapterSelection(AdapterInfo? Adapter, string Reason);

/// <summary>
/// Maps the game connection's local IPv4 address to a capture device (PROTOCOL.md §2.2): the device that owns the address,
/// the Npcap loopback adapter for 127.x, or a manual override by name. Selection works on <see cref="AdapterInfo"/>
/// lists, so it is testable without Npcap.
/// </summary>
public static class AdapterSelector
{
    /// <summary>Lists Npcap capture devices. Empty when Npcap is missing or enumeration fails. Never throws.
    /// <see cref="AdapterInfo.Name"/> is the NPF device name; <see cref="AdapterInfo.Description"/> is
    /// "&lt;friendly name&gt; (&lt;driver description&gt;)" when Windows provides a friendly name.</summary>
    public static IReadOnlyList<AdapterInfo> ListAdapters()
    {
        if (NpcapAvailability.FindWpcapDll() is null) return Array.Empty<AdapterInfo>();
        try
        {
            NpcapAvailability.EnsureLoaded();
            return Enumerate();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Capture", $"Adapter enumeration failed: {ex.Message}");
            return Array.Empty<AdapterInfo>();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IReadOnlyList<AdapterInfo> Enumerate()
    {
        var result = new List<AdapterInfo>();
        foreach (var dev in SharpPcap.LibPcap.LibPcapLiveDeviceList.New())
            result.Add(ToAdapterInfo(dev));
        return result;
    }

    internal static AdapterInfo ToAdapterInfo(SharpPcap.LibPcap.LibPcapLiveDevice dev)
    {
        string name = dev.Name ?? "";
        string? friendly = null;
        try { friendly = dev.Interface?.FriendlyName; } catch { /* optional */ }
        string desc = dev.Description ?? "";
        string display = !string.IsNullOrWhiteSpace(friendly)
            ? (string.IsNullOrWhiteSpace(desc) || desc == friendly ? friendly! : $"{friendly} ({desc})")
            : (string.IsNullOrWhiteSpace(desc) ? name : desc);
        var ips = new List<string>();
        try
        {
            foreach (var a in dev.Addresses)
            {
                var ip = a.Addr?.ipAddress;
                if (ip is { AddressFamily: AddressFamily.InterNetwork }) ips.Add(ip.ToString());
            }
        }
        catch { /* no addresses */ }
        bool loopback = IsLoopbackName(name);
        try { loopback |= dev.Loopback; } catch { /* flag unavailable */ }
        return new AdapterInfo(name, display, ips, loopback);
    }

    public static bool IsLoopbackName(string name) =>
        name.Contains("NPF_Loopback", StringComparison.OrdinalIgnoreCase) || name.Equals("lo", StringComparison.Ordinal);

    /// <summary>Chooses the capture adapter: override (if set and found) → loopback for 127.x → adapter owning
    /// <paramref name="localAddress"/>.</summary>
    public static AdapterSelection Select(IReadOnlyList<AdapterInfo> adapters, IPAddress? localAddress, string? overrideName)
    {
        if (!string.IsNullOrWhiteSpace(overrideName))
        {
            var o = FindByOverride(adapters, overrideName);
            return o is not null
                ? new AdapterSelection(o, $"manual adapter '{o.Description}'")
                : new AdapterSelection(null, $"the selected adapter '{overrideName}' was not found");
        }
        if (localAddress is null) return new AdapterSelection(null, "no local address known");
        var match = FindForLocalAddress(adapters, localAddress);
        return match is not null
            ? new AdapterSelection(match, $"adapter with address {localAddress}")
            : new AdapterSelection(null, $"no capture adapter has the address {localAddress}");
    }

    /// <summary>The adapter that owns <paramref name="localAddress"/>; the loopback adapter for 127.x / 0.0.0.0.</summary>
    public static AdapterInfo? FindForLocalAddress(IReadOnlyList<AdapterInfo> adapters, IPAddress localAddress)
    {
        if (IPAddress.IsLoopback(localAddress))
            return adapters.FirstOrDefault(a => a.IsLoopback);
        string text = localAddress.ToString();
        return adapters.FirstOrDefault(a => a.IPv4Addresses.Any(ip => string.Equals(ip, text, StringComparison.Ordinal)));
    }

    /// <summary>Matches an override against the device name, the full description, the friendly name part or the driver
    /// description part (case-insensitive), or a GUID contained in the device name.</summary>
    public static AdapterInfo? FindByOverride(IReadOnlyList<AdapterInfo> adapters, string overrideName)
    {
        string o = overrideName.Trim();
        if (o.Length == 0) return null;
        foreach (var a in adapters)
            if (MatchesOverride(a, o)) return a;
        return null;
    }

    public static bool MatchesOverride(AdapterInfo adapter, string overrideName)
    {
        string o = overrideName.Trim();
        if (o.Length == 0) return false;
        const StringComparison ic = StringComparison.OrdinalIgnoreCase;
        if (string.Equals(adapter.Name, o, ic) || string.Equals(adapter.Description, o, ic)) return true;
        if (adapter.Description.StartsWith(o + " (", ic)) return true;
        if (adapter.Description.EndsWith("(" + o + ")", ic)) return true;
        if (Guid.TryParse(o.Trim('{', '}'), out var g) && adapter.Name.Contains(g.ToString("B"), ic)) return true;
        return false;
    }

    /// <summary>Adapters used when the game connection's local address is unknown: the override if set, otherwise every
    /// non-loopback adapter with an IPv4 address (plus the loopback adapter when <paramref name="includeLoopback"/>).</summary>
    public static IReadOnlyList<AdapterInfo> SelectScanSet(IReadOnlyList<AdapterInfo> adapters, string? overrideName, bool includeLoopback)
    {
        if (!string.IsNullOrWhiteSpace(overrideName))
        {
            var o = FindByOverride(adapters, overrideName);
            return o is null ? Array.Empty<AdapterInfo>() : new[] { o };
        }
        return adapters.Where(a => (a.IsLoopback && includeLoopback) || (!a.IsLoopback && a.IPv4Addresses.Count > 0)).ToList();
    }
}
