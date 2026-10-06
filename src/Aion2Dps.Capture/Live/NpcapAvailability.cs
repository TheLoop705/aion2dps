using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aion2Dps.Capture;

/// <summary>Result of <see cref="NpcapAvailability.Check"/>.</summary>
/// <param name="IsAvailable">wpcap.dll was found and SharpPcap enumerated at least one device.</param>
/// <param name="Reason">Human-readable explanation (shown in the UI when unavailable).</param>
/// <param name="WpcapPath">Full path of the wpcap.dll that was found, if any.</param>
/// <param name="DeviceCount">Number of capture devices enumerated.</param>
public sealed record NpcapCheckResult(bool IsAvailable, string Reason, string? WpcapPath, int DeviceCount);

/// <summary>Detects whether Npcap is installed and usable. Never throws.</summary>
public static class NpcapAvailability
{
    public const string DownloadUrl = "https://npcap.com/#download";
    private static readonly object Sync = new();
    private static bool _preloaded;

    /// <summary>Directories searched for wpcap.dll: %SystemRoot%\System32\Npcap, then %SystemRoot%\System32.</summary>
    public static IReadOnlyList<string> CandidateDirectories
    {
        get
        {
            string root = Environment.GetEnvironmentVariable("SystemRoot") ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrEmpty(root)) root = @"C:\Windows";
            string sys = Path.Combine(root, "System32");
            return new[] { Path.Combine(sys, "Npcap"), sys };
        }
    }

    /// <summary>Full path of wpcap.dll, or null when Npcap (or WinPcap) is not installed.</summary>
    public static string? FindWpcapDll()
    {
        try
        {
            foreach (var dir in CandidateDirectories)
            {
                string p = Path.Combine(dir, "wpcap.dll");
                if (File.Exists(p)) return p;
            }
        }
        catch
        {
            // fall through
        }
        return null;
    }

    /// <summary>Checks for wpcap.dll and that SharpPcap can enumerate devices.</summary>
    public static NpcapCheckResult Check()
    {
        string? dll = FindWpcapDll();
        if (dll is null)
        {
            return new NpcapCheckResult(false,
                $"Npcap is not installed (wpcap.dll not found in {string.Join(" or ", CandidateDirectories)}). " +
                $"Install Npcap from {DownloadUrl} and restart the meter.", null, 0);
        }
        try
        {
            EnsureLoaded(dll);
            int count = CountDevices();
            if (count == 0)
            {
                return new NpcapCheckResult(false,
                    $"Npcap found ({dll}) but no capture devices are available. Check that the Npcap driver service is running " +
                    "or reinstall Npcap.", dll, 0);
            }
            return new NpcapCheckResult(true, $"Npcap OK ({dll}, {count} devices)", dll, count);
        }
        catch (Exception ex)
        {
            string msg = ex is TypeInitializationException { InnerException: { } inner } ? inner.Message : ex.Message;
            return new NpcapCheckResult(false, $"Npcap found ({dll}) but could not be used: {msg}", dll, 0);
        }
    }

    /// <summary>Preloads Packet.dll and wpcap.dll from the Npcap directory so SharpPcap's "wpcap" import resolves to them.
    /// Returns false when wpcap.dll is missing.</summary>
    public static bool EnsureLoaded() => FindWpcapDll() is { } dll && EnsureLoaded(dll);

    private static bool EnsureLoaded(string wpcapPath)
    {
        lock (Sync)
        {
            if (_preloaded) return true;
            try
            {
                string dir = Path.GetDirectoryName(wpcapPath)!;
                string packet = Path.Combine(dir, "Packet.dll");
                if (File.Exists(packet)) NativeLibrary.TryLoad(packet, out _);
                _preloaded = NativeLibrary.TryLoad(wpcapPath, out _);
            }
            catch
            {
                _preloaded = false;
            }
            return _preloaded;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CountDevices()
    {
        var list = SharpPcap.LibPcap.LibPcapLiveDeviceList.New();
        return list.Count;
    }
}
