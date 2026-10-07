using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Aion2Dps.App.Demo;
using Aion2Dps.App.Integration;
using Aion2Dps.App.Overlay;
using Aion2Dps.App.Settings;
using Aion2Dps.Contracts;

namespace Aion2Dps.App.Tests;

/// <summary>
/// The compact/expanded switch while the user is using the overlay: no automatic shrink under the cursor, no
/// repositioning during a move/resize drag, and the training state after a reset. No window is ever shown.
/// </summary>
public class OverlayInteractionTests
{
    private static readonly DateTime T0 = new(2026, 10, 7, 18, 0, 0, DateTimeKind.Utc);
    private static readonly Guid FightA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid FightB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static OverlayPresentation Step(OverlayPresentationTracker t, MeterState state, Guid? id, double seconds,
        bool interacting = false, bool toast = false, bool shrink = true) =>
        t.Update(state, id, T0.AddSeconds(seconds), shrink, 0, toast, interacting);

    // ───────────── Hold while interacting ─────────────

    [Fact]
    public void Linger_running_out_while_hovered_keeps_the_full_overlay_until_the_pointer_leaves()
    {
        var t = new OverlayPresentationTracker();
        var pointer = new OverlayInteractionTracker();
        bool Hover(bool over, double s) => pointer.Update(over, menuOpen: false, T0.AddSeconds(s));

        Step(t, MeterState.InCombat, FightA, 0);
        Assert.Equal(OverlayPresentation.Expanded, Step(t, MeterState.Ended, FightA, 10, Hover(false, 10)));
        // 14.9 s into the linger the user moves onto a row; the linger ends at 25 s.
        Assert.Equal(OverlayPresentation.Expanded, Step(t, MeterState.Ended, FightA, 24.9, Hover(true, 24.9)));
        Assert.Equal(OverlayPresentation.Expanded, Step(t, MeterState.Ended, FightA, 25, Hover(true, 25)));
        Assert.Equal(OverlayPresentation.Expanded, Step(t, MeterState.Ended, FightA, 40, Hover(true, 40)));
        // The engine clears the fight while still hovered: still held.
        Assert.Equal(OverlayPresentation.Expanded, Step(t, MeterState.WaitingForCombat, null, 41, Hover(true, 41)));
        // Pointer leaves at 41.2 s (MouseLeave): held through the grace, compact after it.
        pointer.Touch(T0.AddSeconds(41.2));
        Assert.Equal(OverlayPresentation.Expanded, Step(t, MeterState.WaitingForCombat, null, 41.5, Hover(false, 41.5)));
        Assert.Equal(OverlayPresentation.Expanded, Step(t, MeterState.WaitingForCombat, null, 42.1, Hover(false, 42.1)));
        Assert.Equal(OverlayPresentation.Compact, Step(t, MeterState.WaitingForCombat, null, 42.2, Hover(false, 42.2)));
        // Hovering the compact bar never expands it by itself.
        Assert.Equal(OverlayPresentation.Compact, Step(t, MeterState.WaitingForCombat, null, 43, Hover(true, 43)));
    }

    [Fact]
    public void An_open_menu_holds_the_full_overlay_like_the_pointer()
    {
        var t = new OverlayPresentationTracker();
        var pointer = new OverlayInteractionTracker();
        Step(t, MeterState.InCombat, FightA, 0);
        Step(t, MeterState.Ended, FightA, 3);
        // The linger ends at 18 s while the stopwatch menu is open (its popup is a separate window: pointer not over).
        Assert.Equal(OverlayPresentation.Expanded, Step(t, MeterState.Ended, FightA, 30, pointer.Update(false, true, T0.AddSeconds(30))));
        Assert.Equal(OverlayPresentation.Compact, Step(t, MeterState.Ended, FightA, 32, pointer.Update(false, false, T0.AddSeconds(32))));
    }

    [Fact]
    public void A_toast_ending_while_hovered_does_not_shrink_under_the_cursor()
    {
        var t = new OverlayPresentationTracker();
        Assert.Equal(OverlayPresentation.Compact, Step(t, MeterState.WaitingForCombat, null, 0));
        Assert.Equal(OverlayPresentation.Expanded, Step(t, MeterState.WaitingForCombat, null, 1, toast: true));
        Assert.Equal(OverlayPresentation.Expanded, Step(t, MeterState.WaitingForCombat, null, 9, interacting: true));
        Assert.Equal(OverlayPresentation.Compact, Step(t, MeterState.WaitingForCombat, null, 11));
    }

    [Fact]
    public void Collapse_button_and_the_setting_apply_at_once_while_hovered()
    {
        var t = new OverlayPresentationTracker();
        Step(t, MeterState.InCombat, FightA, 0);
        t.Collapse();
        Assert.Equal(OverlayPresentation.Compact, Step(t, MeterState.InCombat, FightA, 1, interacting: true));
        Assert.Equal(OverlayPresentation.Compact, Step(t, MeterState.Ended, FightA, 2, interacting: true));

        // Out of combat (pin dropped, no remembered collapse) the button also wins over the hover.
        var u = new OverlayPresentationTracker();
        Step(u, MeterState.WaitingForCombat, null, 0);
        u.Expand();
        Step(u, MeterState.WaitingForCombat, null, 1, interacting: true);
        u.Collapse();
        Assert.Equal(OverlayPresentation.Compact, Step(u, MeterState.WaitingForCombat, null, 2, interacting: true));

        // Setting off → expanded at once, even while hovered.
        Assert.Equal(OverlayPresentation.Expanded, Step(u, MeterState.WaitingForCombat, null, 3, interacting: true, shrink: false));
        // Back on (from the dashboard, pointer not on the overlay) → compact at once.
        Assert.Equal(OverlayPresentation.Compact, Step(u, MeterState.WaitingForCombat, null, 4));
    }

    [Fact]
    public void Automatic_expansion_is_never_delayed_by_interaction()
    {
        var t = new OverlayPresentationTracker();
        Assert.Equal(OverlayPresentation.Compact, Step(t, MeterState.WaitingForCombat, null, 0));
        Assert.Equal(OverlayPresentation.Compact, Step(t, MeterState.WaitingForCombat, null, 1, interacting: true));
        Assert.Equal(OverlayPresentation.Expanded, Step(t, MeterState.InCombat, FightB, 2, interacting: true));
    }

    [Fact]
    public void Interaction_grace_follows_the_last_pointer_or_menu_activity()
    {
        var i = new OverlayInteractionTracker();
        Assert.False(i.Update(false, false, T0));
        Assert.True(i.Update(true, false, T0));
        Assert.True(i.Update(false, false, T0.AddSeconds(0.9)));
        Assert.False(i.Update(false, false, T0.AddSeconds(1)));
        Assert.True(i.Update(false, true, T0.AddSeconds(5)));
        i.Touch(T0.AddSeconds(10));
        Assert.True(i.Update(false, false, T0.AddSeconds(10.5)));
        Assert.False(i.Update(false, false, T0.AddSeconds(11.1)));
    }

    // ───────────── Training after a reset ─────────────

    [Fact]
    public void Compact_bar_follows_the_engine_when_a_reset_disarmed_training()
    {
        // The overlay countdown is still set, but the engine (after Reset) reports plain waiting.
        var bar = CompactBarModel.Build(PreviewData.WaitingForCombat() with { StatusText = "Waiting for combat" },
            PreviewData.Status() with { TrainingRemaining = TimeSpan.FromSeconds(52) });
        Assert.Equal("Waiting for combat", bar.Status);
        Assert.Null(bar.Hint);
        Assert.DoesNotContain("training", bar.Tooltip, StringComparison.OrdinalIgnoreCase);

        var armed = CompactBarModel.Build(PreviewData.WaitingForCombat() with { StatusText = "Training ready (60 s): hit a target to start" },
            PreviewData.Status() with { TrainingRemaining = TimeSpan.FromSeconds(52) });
        Assert.Equal("Training ready", armed.Status);
        Assert.Equal("0:52", armed.Hint);
    }

    [Fact]
    public void Reset_clears_the_training_countdown_like_stop_does() => Sta.Run(() =>
    {
        string dir = Path.Combine(Path.GetTempPath(), "aion2dps-interaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var services = ServiceFactory.CreateDemo(originUtc: T0);
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            using var controller = new OverlayController(services, store, _ => { }); // never shown
            controller.StartTraining(TimeSpan.FromMinutes(1));
            Assert.NotNull(controller.TrainingEndsUtc);
            controller.Reset();
            Assert.Null(controller.TrainingEndsUtc);
        }
        finally { Directory.Delete(dir, true); }
    });

    // ───────────── Window: no repositioning during a drag ─────────────

    /// <summary>
    /// A real overlay window, constructed but never shown, flush with the right edge of the primary work area. A
    /// never-shown window has no layout, so the laid-out size is supplied (width = the window's Width) and
    /// <see cref="Relayout"/> stands in for the SizeChanged that the layout pass raises on screen.
    /// </summary>
    private static (OverlayWindow Window, Rect WorkArea) WindowAtRightEdge(double width = 420)
    {
        var wa = SystemParameters.WorkArea;
        var window = new OverlayWindow(new OverlaySettings { Left = wa.Right - width, Top = wa.Top + 100, Width = width });
        window.WorkAreaOverride = () => wa;
        window.SizeOverride = () => new Size(window.Width, 230);
        window.View.AnimationsEnabled = false;
        window.SetPresentation(compact: false, keepInWorkArea: true);
        Assert.False(window.IsVisible);
        Assert.Equal(wa.Right - width, window.Left, 1);
        return (window, wa);
    }

    private static void Relayout(OverlayWindow window) => window.OnLaidOutSizeChanged();

    [Fact]
    public void Resize_grip_at_the_right_edge_grows_by_the_mouse_distance_not_quadratically() => Sta.Run(() =>
    {
        var (window, wa) = WindowAtRightEdge();
        try
        {
            double startLeft = window.Left, startWidth = window.Width;
            var grip = window.View.ResizeGrip;
            grip.RaiseEvent(new DragStartedEventArgs(0, 0));
            // Thumb.OnMouseMove: HorizontalChange = mouse position relative to the thumb now - the press point
            // relative to the thumb. The thumb sits at the window's right edge (pressed at offset 0), so it moves
            // with Left + Width; 1 DIP per mouse move, like a real drag.
            double pressScreenX = window.Left + window.Width;
            for (int moved = 1; moved <= 30; moved++)
            {
                double thumbScreenX = window.Left + window.Width;
                grip.RaiseEvent(new DragDeltaEventArgs(pressScreenX + moved - thumbScreenX, 0));
                Relayout(window);
                Assert.Equal(startWidth + moved, window.Width, 1);
                Assert.Equal(startLeft, window.Left, 1); // nothing shifts the window under the grip
            }
            grip.RaiseEvent(new DragCompletedEventArgs(0, 0, false));
            Assert.Equal(startWidth + 30, window.ExpandedWidth, 1);
            Assert.Equal(wa.Right - (startWidth + 30), window.Left, 1); // fitted once, when the resize ends
            Assert.Equal(startLeft, window.Anchor.X, 1);                // the user's anchor is unchanged
        }
        finally { window.Close(); }
    });

    private static void Press(UIElement handle, RoutedEvent e) =>
        handle.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = e });

    [Fact]
    public void Presentation_switch_during_a_window_drag_waits_for_the_drop() => Sta.Run(() =>
    {
        var (window, wa) = WindowAtRightEdge();
        try
        {
            int bounds = 0;
            window.BoundsChanged += () => bounds++;
            Press(window.View.DragHandle, UIElement.MouseLeftButtonDownEvent);
            window.SetPresentation(compact: true, keepInWorkArea: true); // the linger ran out mid-drag
            Assert.False(window.IsCompact);
            Assert.False(window.View.IsCompact);
            Press(window.View.DragHandle, UIElement.MouseLeftButtonUpEvent);
            Assert.True(window.IsCompact);
            Assert.True(window.View.IsCompact);
            Assert.Equal(0, bounds); // a press without movement is not a move

            // Compact bar pressed when a fight starts: expands on release, then fits the work area.
            window.SizeOverride = () => new Size(window.IsCompact ? 236 : window.Width, window.IsCompact ? 28 : 230);
            Press(window.View.CompactBar, UIElement.MouseLeftButtonDownEvent);
            window.SetPresentation(compact: false, keepInWorkArea: true);
            Assert.True(window.View.IsCompact);
            Press(window.View.CompactBar, UIElement.MouseLeftButtonUpEvent);
            Assert.False(window.View.IsCompact);
            Assert.True(window.Left + window.Width <= wa.Right + 0.5, $"{window.Left}+{window.Width} inside {wa.Right}");
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Presentation_switch_during_a_grip_resize_waits_for_the_resize_to_end() => Sta.Run(() =>
    {
        var (window, _) = WindowAtRightEdge();
        try
        {
            var grip = window.View.ResizeGrip;
            grip.RaiseEvent(new DragStartedEventArgs(0, 0));
            window.SetPresentation(compact: true, keepInWorkArea: true);
            Assert.False(window.View.IsCompact);
            grip.RaiseEvent(new DragCompletedEventArgs(0, 0, false));
            Assert.True(window.View.IsCompact);
        }
        finally { window.Close(); }
    });
}
