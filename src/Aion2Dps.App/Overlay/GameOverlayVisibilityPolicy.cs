namespace Aion2Dps.App.Overlay;

/// <summary>What the host should do with the overlay after a game/setting/user event.</summary>
public enum OverlayVisibilityCommand
{
    None,
    /// <summary>Show the overlay (automatic: not a user choice).</summary>
    Show,
    /// <summary>Hide the overlay (automatic: does not record "the user hid it").</summary>
    Hide,
}

/// <summary>
/// "Show the overlay only while AION 2 is running" (no WPF, no clocks: unit-testable). The host feeds it the game watcher's
/// events, the user's manual show/hide and setting changes, and executes the returned command. Rules:
/// <list type="bullet">
/// <item>Off (setting off, or not live capture) → never commands anything: the overlay behaves exactly as before.</item>
/// <item>Start-up → hidden; the watcher's first check shows it when the game already runs.</item>
/// <item>Game start → show, when "Show the overlay on start" allows it (and not <c>--no-overlay</c>).</item>
/// <item>Game exit (after the watcher's grace) → hide.</item>
/// <item>The user hid it while the game runs → it stays hidden until the next game start (a repeated report of the same
/// running game, e.g. after the setting was switched off and on, does not bring it back).</item>
/// <item>Switching the setting off → the overlay returns when the user had not hidden it.</item>
/// </list>
/// </summary>
public sealed class GameOverlayVisibilityPolicy
{
    /// <summary>The feature is active (setting on and live capture).</summary>
    public bool Enabled { get; private set; }

    /// <summary>Last known game state (true from a start report until an exit / not-running report).</summary>
    public bool GameRunning { get; private set; }

    /// <summary>The user hid the overlay during the current game session.</summary>
    public bool HiddenByUser { get; private set; }

    /// <summary>Whether the overlay is shown right at start-up (before any game check).</summary>
    public static bool ShowAtStartup(bool gameAware, bool launchOverlayOnStart, bool overlayVisible, bool noOverlay) =>
        !gameAware && !noOverlay && launchOverlayOnStart && overlayVisible;

    /// <summary>
    /// The one-time tray notice after the first Windows autostart. It promises the overlay with the game only when that will
    /// actually happen (feature on and "Show the overlay on start" allows the automatic show).
    /// </summary>
    public static string AutostartNoticeText(bool gameAware, bool allowAutoShow) => gameAware && allowAutoShow
        ? "Aion2Dps runs in the tray and appears when AION 2 starts."
        : "Aion2Dps runs in the tray. Right-click the icon for the menu.";

    /// <summary>Tray status line: null when the feature is off.</summary>
    public string? StatusText => !Enabled ? null : GameRunning ? "AION 2 running" : "Waiting for AION 2";

    /// <summary>Switches the feature on/off. Enabling commands nothing: the watcher's first check decides.</summary>
    /// <param name="overlayVisible">The overlay is on screen now.</param>
    /// <param name="userWantsOverlay">The persisted "overlay visible" choice (false after a manual hide).</param>
    /// <param name="allowAutoShow">"Show the overlay on start" and not <c>--no-overlay</c>.</param>
    public OverlayVisibilityCommand SetEnabled(bool enabled, bool overlayVisible, bool userWantsOverlay, bool allowAutoShow)
    {
        if (enabled == Enabled) return OverlayVisibilityCommand.None;
        Enabled = enabled;
        if (enabled) return OverlayVisibilityCommand.None;
        // Back to the classic behaviour: the overlay is visible unless the user chose otherwise.
        bool show = !overlayVisible && userWantsOverlay && allowAutoShow && !HiddenByUser;
        return show ? OverlayVisibilityCommand.Show : OverlayVisibilityCommand.None;
    }

    /// <summary>The watcher found the game (a new start, or the first check of a watcher that found it running).</summary>
    public OverlayVisibilityCommand OnGameStarted(bool overlayVisible, bool allowAutoShow)
    {
        bool sameSession = GameRunning;
        GameRunning = true;
        if (!sameSession) HiddenByUser = false;
        if (!Enabled || overlayVisible || !allowAutoShow || HiddenByUser) return OverlayVisibilityCommand.None;
        return OverlayVisibilityCommand.Show;
    }

    /// <summary>The game has been gone for the watcher's grace period.</summary>
    public OverlayVisibilityCommand OnGameExited(bool overlayVisible) => OnGameGone(overlayVisible);

    /// <summary>The watcher's first check did not find the game.</summary>
    public OverlayVisibilityCommand OnGameNotRunning(bool overlayVisible) => OnGameGone(overlayVisible);

    private OverlayVisibilityCommand OnGameGone(bool overlayVisible)
    {
        GameRunning = false;
        HiddenByUser = false;
        return Enabled && overlayVisible ? OverlayVisibilityCommand.Hide : OverlayVisibilityCommand.None;
    }

    /// <summary>The user hid the overlay (hotkey, tray, hide button, dashboard).</summary>
    public void OnUserHid()
    {
        if (GameRunning) HiddenByUser = true;
    }

    /// <summary>The user showed the overlay.</summary>
    public void OnUserShowed() => HiddenByUser = false;
}
