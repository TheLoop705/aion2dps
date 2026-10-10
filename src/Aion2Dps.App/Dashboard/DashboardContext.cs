using Aion2Dps.App.Infrastructure;
using Aion2Dps.App.Integration;
using Aion2Dps.App.Settings;

namespace Aion2Dps.App.Dashboard;

/// <summary>Everything the dashboard pages need (services, settings and host actions).</summary>
public sealed class DashboardContext
{
    public required AppServices Services { get; init; }
    public required SettingsStore Settings { get; init; }
    public FileLogSink? Log { get; init; }
    /// <summary>Rift / event / field-boss timers; null hides the Timers page content.</summary>
    public Timers.TimerService? Timers { get; init; }
    public Func<IReadOnlyList<HotkeyBinding>> Hotkeys { get; init; } = () => GlobalHotkeys.Defaults();
    /// <summary>Applies settings that need a service call (engine options, language, adapter). Called after edits.</summary>
    public Action ApplyServiceSettings { get; init; } = () => { };
    public Action RestartCapture { get; init; } = () => { };
    public Action ToggleOverlay { get; init; } = () => { };
    /// <summary>Shows another dashboard page by key (set by <see cref="DashboardView"/>).</summary>
    public Action<string>? Navigate { get; set; }
    public Func<bool> OverlayVisible { get; init; } = () => false;
    /// <summary>"Start Aion2Dps with Windows" (the HKCU Run value); null = not available (previews, renders).</summary>
    public AutostartRegistration? Autostart { get; init; }

    public string CaptureFolder => string.IsNullOrWhiteSpace(Settings.Current.General.CaptureFolder)
        ? AppPaths.DefaultCaptureDirectory
        : Settings.Current.General.CaptureFolder!;
}

/// <summary>Base class of dashboard pages. <see cref="Refresh"/> is called ~1×/s while the page is visible.</summary>
public abstract class DashboardPage : UserControl
{
    protected DashboardPage(DashboardContext context)
    {
        Context = context;
    }

    protected DashboardContext Context { get; }

    public abstract string Key { get; }
    public abstract string Title { get; }
    public abstract string Icon { get; }

    /// <summary>Called when the page becomes visible (build lazy content here).</summary>
    public virtual void OnShown() { }

    public virtual void Refresh() { }
}
