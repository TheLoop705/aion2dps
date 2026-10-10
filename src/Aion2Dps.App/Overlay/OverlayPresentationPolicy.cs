namespace Aion2Dps.App.Overlay;

/// <summary>How the overlay window is presented: the slim one-row status bar, or the full meter.</summary>
public enum OverlayPresentation
{
    /// <summary>Full meter (header, rows, footer): today's overlay.</summary>
    Expanded,
    /// <summary>One slim status row (dot, short status, zone, last-fight hint, expand chevron).</summary>
    Compact,
}

/// <summary>Everything the presentation decision depends on (no WPF, no clocks: unit-testable).</summary>
public readonly record struct OverlayPresentationInput
{
    /// <summary>"Shrink overlay when not in combat" (off = always expanded, exactly the pre-feature overlay).</summary>
    public bool ShrinkWhenIdle { get; init; }
    public MeterState State { get; init; }
    /// <summary>When the overlay first saw the current encounter in <see cref="MeterState.Ended"/> (snapshot clock).</summary>
    public DateTime? EndedSinceUtc { get; init; }
    public DateTime NowUtc { get; init; }
    /// <summary>How long a finished fight stays expanded (see <see cref="OverlayPresentationPolicy.ResolveLinger"/>).</summary>
    public double LingerSeconds { get; init; }
    /// <summary>The user expanded the compact bar (click / chevron).</summary>
    public bool Pinned { get; init; }
    /// <summary>The user collapsed the overlay while it was expanded by a fight (until the next fight starts).</summary>
    public bool CollapsedByUser { get; init; }
    /// <summary>A toast (personal best, training notice) is showing: it needs the expanded overlay to be legible.</summary>
    public bool ToastVisible { get; init; }
    /// <summary>
    /// The user is using the overlay: the pointer is on it (or left it less than
    /// <see cref="OverlayInteractionTracker.Grace"/> ago) or one of its menus is open. Only delays an automatic shrink
    /// of the full overlay (see <see cref="OverlayPresentationTracker"/>); never expands anything by itself.
    /// </summary>
    public bool Interacting { get; init; }
}

/// <summary>
/// Decides between the compact status bar and the full overlay. Rules, first match wins:
/// <list type="number">
/// <item>Setting off → expanded (unchanged behaviour).</item>
/// <item>A toast is showing → expanded (it only lasts a few seconds).</item>
/// <item>The user collapsed it → compact.</item>
/// <item>The user expanded it (pinned) → expanded.</item>
/// <item>In combat (boss, trash, PvP, a running training run) → expanded.</item>
/// <item>Fight ended → expanded while the result lingers, compact afterwards.</item>
/// <item>Idle / waiting for combat (incl. Npcap missing, waiting for the game, training armed) → compact.</item>
/// </list>
/// </summary>
public static class OverlayPresentationPolicy
{
    /// <summary>Linger used when finished fights are kept until the next one ("Shrink a finished fight after" = 0).</summary>
    public const double DefaultLingerSeconds = 15;

    /// <summary>
    /// The expanded linger after a fight: the "Shrink a finished fight after" delay when set, otherwise
    /// <see cref="DefaultLingerSeconds"/> (the result then stays reachable by expanding the compact bar).
    /// </summary>
    public static double ResolveLinger(double endedDisplaySeconds) =>
        double.IsFinite(endedDisplaySeconds) && endedDisplaySeconds > 0 ? endedDisplaySeconds : DefaultLingerSeconds;

    public static OverlayPresentation Decide(in OverlayPresentationInput i)
    {
        if (!i.ShrinkWhenIdle) return OverlayPresentation.Expanded;
        if (i.ToastVisible) return OverlayPresentation.Expanded;
        if (i.CollapsedByUser) return OverlayPresentation.Compact;
        if (i.Pinned) return OverlayPresentation.Expanded;
        return Automatic(i);
    }

    /// <summary>The decision without any manual override (what the fight state alone asks for).</summary>
    public static OverlayPresentation Automatic(in OverlayPresentationInput i) => i.State switch
    {
        MeterState.InCombat => OverlayPresentation.Expanded,
        MeterState.Ended when i.EndedSinceUtc is not { } since || (i.NowUtc - since).TotalSeconds < i.LingerSeconds => OverlayPresentation.Expanded,
        _ => OverlayPresentation.Compact,
    };
}

/// <summary>
/// The overlay's presentation state machine: remembers when the current fight ended, the user's pin (expand) and
/// collapse, and feeds <see cref="OverlayPresentationPolicy"/>. Pure (the caller passes the snapshot clock).
/// <para>
/// Manual expand ("pin") keeps the full overlay out of combat until the next fight starts; that fight then expands it
/// anyway and, once its result has lingered, the overlay shrinks again on its own. So an expansion never sticks
/// around forever, and the bar is back to small after the next fight without the user having to remember to collapse.
/// Manual collapse during a fight or its linger keeps the bar small until a new fight starts.
/// </para>
/// <para>
/// An automatic shrink (linger over, the engine cleared the fight, a toast ended) waits while the user is interacting
/// with the full overlay, so the window never shrinks out from under the cursor and a click meant for a row, button or
/// menu does not land in the game. The collapse button and turning the setting off still apply at once, and automatic
/// expansion is never delayed.
/// </para>
/// </summary>
public sealed class OverlayPresentationTracker
{
    private Guid? _endedEncounter;
    private DateTime? _endedSince;
    private Guid? _collapsedFor;
    private Guid? _lastEncounter;
    private OverlayPresentationInput _last;

    public bool Pinned { get; private set; }
    public bool CollapsedByUser { get; private set; }
    public OverlayPresentation Current { get; private set; } = OverlayPresentation.Expanded;

    /// <summary>When the current fight was first seen ended (null while not ended).</summary>
    public DateTime? EndedSinceUtc => _endedSince;

    /// <summary>Feeds one refresh; returns (and remembers) the presentation to show.</summary>
    public OverlayPresentation Update(MeterState state, Guid? encounterId, DateTime nowUtc, bool shrinkWhenIdle, double endedDisplaySeconds,
        bool toastVisible, bool interacting = false)
    {
        _lastEncounter = encounterId;
        if (state == MeterState.Ended)
        {
            if (_endedSince is null || _endedEncounter != encounterId)
            {
                _endedEncounter = encounterId;
                _endedSince = nowUtc;
            }
        }
        else
        {
            _endedEncounter = null;
            _endedSince = null;
        }

        // A fight starting releases the pin: the fight keeps the overlay expanded by itself, and the bar shrinks again
        // after it (see the class remarks).
        if (state == MeterState.InCombat) Pinned = false;

        // A collapse lasts until a new fight starts (or there is nothing left to hide).
        if (CollapsedByUser && (state is MeterState.Idle or MeterState.WaitingForCombat
                                || (state == MeterState.InCombat && encounterId != _collapsedFor)))
        {
            CollapsedByUser = false;
            _collapsedFor = null;
        }

        _last = new OverlayPresentationInput
        {
            ShrinkWhenIdle = shrinkWhenIdle,
            State = state,
            EndedSinceUtc = _endedSince,
            NowUtc = nowUtc,
            LingerSeconds = OverlayPresentationPolicy.ResolveLinger(endedDisplaySeconds),
            Pinned = Pinned,
            CollapsedByUser = CollapsedByUser,
            ToastVisible = toastVisible,
            Interacting = interacting,
        };
        var decided = OverlayPresentationPolicy.Decide(_last);
        // Hold the full overlay while the user is on it (see the class remarks). The explicit collapse sets Current to
        // Compact itself, so it is never held; with the setting off Decide never returns Compact.
        if (decided == OverlayPresentation.Compact && Current == OverlayPresentation.Expanded && interacting && !CollapsedByUser)
            decided = OverlayPresentation.Expanded;
        Current = decided;
        return Current;
    }

    /// <summary>The user clicked the compact bar / chevron: show the full overlay until the next fight.</summary>
    public void Expand()
    {
        CollapsedByUser = false;
        _collapsedFor = null;
        Pinned = true;
        Current = OverlayPresentation.Expanded;
    }

    /// <summary>The user clicked the collapse button in the expanded overlay.</summary>
    public void Collapse()
    {
        Pinned = false;
        // Out of combat (and past the linger), dropping the pin is enough. During a fight or its linger the fight
        // itself asks for the full overlay, so remember the collapse until the next fight starts.
        var automatic = OverlayPresentationPolicy.Automatic(_last);
        if (automatic == OverlayPresentation.Expanded)
        {
            CollapsedByUser = true;
            _collapsedFor = _lastEncounter;
        }
        Current = OverlayPresentation.Compact;
    }
}

/// <summary>
/// Whether the user is interacting with the overlay, with a short grace after the pointer leaves (moving the mouse
/// off the edge for a moment, or a pointer that was over it between two refresh ticks). Pure: the caller passes the clock.
/// </summary>
public sealed class OverlayInteractionTracker
{
    /// <summary>How long after the pointer left (or a menu closed) the overlay still counts as in use.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(1);

    private DateTime? _lastActiveUtc;

    /// <summary>Records that the user was interacting at <paramref name="nowUtc"/> (e.g. on MouseLeave).</summary>
    public void Touch(DateTime nowUtc) => _lastActiveUtc = nowUtc;

    /// <summary>Feeds one refresh; true while the pointer is on the overlay, a menu is open, or within the grace.</summary>
    public bool Update(bool pointerOver, bool menuOpen, DateTime nowUtc)
    {
        if (pointerOver || menuOpen)
        {
            _lastActiveUtc = nowUtc;
            return true;
        }
        return _lastActiveUtc is { } last && nowUtc - last < Grace;
    }
}

/// <summary>Window placement maths for the compact/expanded switch (pure; unit-testable).</summary>
public static class OverlayGeometry
{
    /// <summary>
    /// Top-left for a window of <paramref name="size"/> whose preferred top-left is <paramref name="anchor"/>: shifted
    /// up/left just enough to stay inside <paramref name="workArea"/>, never past its top/left edge (and never moved
    /// right/down: an anchor the user dragged partly off the work area stays where it is on that side).
    /// </summary>
    public static Point FitInside(Point anchor, Size size, Rect workArea)
    {
        if (workArea.IsEmpty || !double.IsFinite(size.Width) || !double.IsFinite(size.Height)) return anchor;
        double x = anchor.X, y = anchor.Y;
        if (x + size.Width > workArea.Right) x = Math.Max(workArea.Right - size.Width, Math.Min(anchor.X, workArea.Left));
        if (y + size.Height > workArea.Bottom) y = Math.Max(workArea.Bottom - size.Height, Math.Min(anchor.Y, workArea.Top));
        return new Point(x, y);
    }
}
