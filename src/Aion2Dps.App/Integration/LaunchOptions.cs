namespace Aion2Dps.App.Integration;

public enum LaunchMode
{
    /// <summary>Real pipeline on live Npcap capture (default).</summary>
    Live,
    /// <summary>Self-contained fake services (<c>--demo</c>; also the fallback when the real pipeline is unavailable).</summary>
    Demo,
    /// <summary>Real pipeline fed by the wire simulator (<c>--sim</c>).</summary>
    Simulator,
    /// <summary>Real pipeline fed by a pcap/pcapng file (<c>--replay &lt;file&gt;</c>).</summary>
    Replay,
}

/// <summary>Parsed command line.</summary>
public sealed record LaunchOptions
{
    public LaunchMode Mode { get; init; } = LaunchMode.Live;
    public string? ReplayPath { get; init; }
    /// <summary>Replay speed (1 = real time, 0 = as fast as possible). <c>--speed &lt;x&gt;</c>.</summary>
    public double ReplaySpeed { get; init; } = 1;
    /// <summary><c>--render-screens &lt;dir&gt;</c>: run the real simulated pipeline and render the overlay, breakdown,
    /// history, trends and dashboard offscreen to PNGs, then exit (no windows).</summary>
    public string? RenderScreensDir { get; init; }
    /// <summary><c>--render-demo-screens &lt;dir&gt;</c>: the design catalog (every overlay state × theme) from the demo fakes.</summary>
    public string? RenderDemoScreensDir { get; init; }
    /// <summary><c>--no-overlay</c>: do not show the overlay at start-up.</summary>
    public bool NoOverlay { get; init; }
    /// <summary><c>--allow-multiple</c>: skip the single-instance guard (debugging).</summary>
    public bool AllowMultiple { get; init; }
    /// <summary>
    /// <c>--autostart</c>: started by Windows (the HKCU Run value). Starts quietly in the tray: no dashboard, and the overlay
    /// waits for AION 2 (or follows "Show the overlay on start" when "only while AION 2 is running" is off).
    /// </summary>
    public bool Autostart { get; init; }

    public static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        var o = new LaunchOptions();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i].Trim();
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            switch (a.ToLowerInvariant())
            {
                case "--demo": o = o with { Mode = LaunchMode.Demo }; break;
                case "--sim": case "--simulator": o = o with { Mode = LaunchMode.Simulator }; break;
                case "--replay": o = o with { Mode = LaunchMode.Replay, ReplayPath = Next() }; break;
                case "--speed":
                    if (double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sp)) o = o with { ReplaySpeed = Math.Max(0, sp) };
                    break;
                case "--render-screens": o = o with { RenderScreensDir = Next() ?? "screens" }; break;
                case "--render-demo-screens": o = o with { RenderDemoScreensDir = Next() ?? "screens-demo" }; break;
                case "--no-overlay": o = o with { NoOverlay = true }; break;
                case "--allow-multiple": o = o with { AllowMultiple = true }; break;
                case "--autostart": o = o with { Autostart = true }; break;
            }
        }
        if (o.Mode == LaunchMode.Replay && string.IsNullOrWhiteSpace(o.ReplayPath)) o = o with { Mode = LaunchMode.Live };
        return o;
    }
}
