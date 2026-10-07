using System.Windows;
using Aion2Dps.App.Demo;
using Aion2Dps.App.Integration;
using Aion2Dps.App.Overlay;
using Aion2Dps.Contracts;

namespace Aion2Dps.App.Tests;

/// <summary>The compact/expanded decision (pure policy + tracker) and the window placement maths.</summary>
public class OverlayPresentationTests
{
    private static readonly DateTime T0 = new(2026, 10, 7, 18, 0, 0, DateTimeKind.Utc);
    private static readonly Guid FightA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid FightB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static OverlayPresentationInput Input(MeterState state, double sinceEnded = double.NaN, bool shrink = true, bool pinned = false,
        bool collapsed = false, bool toast = false, double linger = 15) => new()
    {
        ShrinkWhenIdle = shrink,
        State = state,
        EndedSinceUtc = double.IsNaN(sinceEnded) ? null : T0,
        NowUtc = double.IsNaN(sinceEnded) ? T0 : T0.AddSeconds(sinceEnded),
        LingerSeconds = linger,
        Pinned = pinned,
        CollapsedByUser = collapsed,
        ToastVisible = toast,
    };

    // ───────────── Policy ─────────────

    [Theory]
    [InlineData(MeterState.Idle, OverlayPresentation.Compact)]
    [InlineData(MeterState.WaitingForCombat, OverlayPresentation.Compact)]
    [InlineData(MeterState.InCombat, OverlayPresentation.Expanded)]
    [InlineData(MeterState.Ended, OverlayPresentation.Expanded)] // just ended (not yet timed)
    public void States_map_to_presentations(MeterState state, OverlayPresentation expected) =>
        Assert.Equal(expected, OverlayPresentationPolicy.Decide(Input(state)));

    [Theory]
    [InlineData(0, OverlayPresentation.Expanded)]
    [InlineData(14.9, OverlayPresentation.Expanded)]
    [InlineData(15, OverlayPresentation.Compact)]
    [InlineData(600, OverlayPresentation.Compact)]
    public void Ended_fight_lingers_expanded_then_compacts(double secondsSinceEnd, OverlayPresentation expected) =>
        Assert.Equal(expected, OverlayPresentationPolicy.Decide(Input(MeterState.Ended, secondsSinceEnd)));

    [Theory]
    [InlineData(MeterState.Idle)]
    [InlineData(MeterState.WaitingForCombat)]
    [InlineData(MeterState.InCombat)]
    [InlineData(MeterState.Ended)]
    public void Setting_off_always_expands_exactly_like_before(MeterState state)
    {
        Assert.Equal(OverlayPresentation.Expanded, OverlayPresentationPolicy.Decide(Input(state, 999, shrink: false)));
        Assert.Equal(OverlayPresentation.Expanded, OverlayPresentationPolicy.Decide(Input(state, 999, shrink: false, collapsed: true)));
    }

    [Fact]
    public void Manual_overrides_and_toasts_follow_the_documented_priority()
    {
        Assert.Equal(OverlayPresentation.Expanded, OverlayPresentationPolicy.Decide(Input(MeterState.WaitingForCombat, pinned: true)));
        Assert.Equal(OverlayPresentation.Compact, OverlayPresentationPolicy.Decide(Input(MeterState.InCombat, collapsed: true)));
        Assert.Equal(OverlayPresentation.Compact, OverlayPresentationPolicy.Decide(Input(MeterState.InCombat, collapsed: true, pinned: true)));
        Assert.Equal(OverlayPresentation.Expanded, OverlayPresentationPolicy.Decide(Input(MeterState.WaitingForCombat, toast: true)));
        Assert.Equal(OverlayPresentation.Expanded, OverlayPresentationPolicy.Decide(Input(MeterState.Ended, 60, toast: true, collapsed: true)));
    }

    [Theory]
    [InlineData(0, 15)]
    [InlineData(-3, 15)]
    [InlineData(double.NaN, 15)]
    [InlineData(double.PositiveInfinity, 15)]
    [InlineData(5, 5)]
    [InlineData(30, 30)]
    public void Linger_reuses_the_clear_finished_fights_delay(double endedDisplaySeconds, double expected) =>
        Assert.Equal(expected, OverlayPresentationPolicy.ResolveLinger(endedDisplaySeconds));

    // ───────────── Tracker ─────────────

    private static OverlayPresentation Step(OverlayPresentationTracker t, MeterState state, Guid? id, double seconds, bool shrink = true,
        double endedDisplay = 0, bool toast = false) =>
        t.Update(state, id, T0.AddSeconds(seconds), shrink, endedDisplay, toast);

    [Fact]
    public void Tracker_follows_a_fight_from_idle_to_linger_and_back()
    {
        var t = new OverlayPresentationTracker();
        Assert.Equal(OverlayPresentation.Compact, Step(t, MeterState.Idle, null, 0));
        Assert.Equal(OverlayPresentation.Compact, Step(t, MeterState.WaitingForCombat, null, 1));
        Assert.Equal(OverlayPresentation.Expanded, Step(t, MeterState.InCombat, FightA, 2));
        Assert.Equal(OverlayPresentation.Expanded, Step(t, MeterState.Ended, FightA, 100)); // first seen ended at 100 s
        Assert.Equal(T0.AddSeconds(100), t.EndedSinceUtc);
        Assert.Equal(OverlayPresentation.Expanded, Step(t, MeterState.Ended, FightA, 114.9));
        Assert.Equal(OverlayPresentation.Compact, Step(t, MeterState.Ended, FightA, 115)); // "keep until next fight": 15 s default
        Assert.Equal(OverlayPresentation.Expanded, Step(t, MeterState.InCombat, FightB, 200));
        Assert.Null(t.EndedSinceUtc);
    }

    [Fact]
    public void Tracker_uses_the_configured_clear_delay_and_compacts_when_the_engine_clears()
    {
        var t = new OverlayPresentationTracker();
        Step(t, MeterState.InCombat, FightA, 0, endedDisplay: 30);
        Assert.Equal(OverlayPresentation.Expanded, Step(t, MeterState.Ended, FightA, 10, endedDisplay: 30));
        Assert.Equal(OverlayPresentation.Expanded, Step(t, MeterState.Ended, FightA, 25, endedDisplay: 30)); // past the 15 s default
        Assert.Equal(OverlayPresentation.Compact, Step(t, MeterState.Ended, FightA, 40, endedDisplay: 30));
        // The engine itself hides finished numbers after the delay (WaitingForCombat): compact right away.
        Step(t, MeterState.InCombat, FightB, 50, endedDisplay: 30);
        Step(t, MeterState.Ended, FightB, 60, endedDisplay: 30);
        Assert.Equal(OverlayPresentation.Compact, Step(t, MeterState.WaitingForCombat, null, 61, endedDisplay: 30));
    }

    [Fact]
    public void A_new_ended_fight_restarts_the_linger()
    {
        var t = new OverlayPresentationTracker();
        Step(t, MeterState.Ended, FightA, 0);
        Assert.Equal(OverlayPresentation.Compact, Step(t, MeterState.Ended, FightA, 20));
        // A replay/zone change can jump straight from one ended encounter to the next.
        Assert.Equal(OverlayPresentation.Expanded, Step(t, MeterState.Ended, FightB, 21));
        Assert.Equal(OverlayPresentation.Compact, Step(t, MeterState.Ended, FightB, 36));
    }

    [Fact]
    public void Pin_keeps_the_overlay_expanded_until_the_next_fight_has_lingered()
    {
        var t = new OverlayPresentationTracker();
        Step(t, MeterState.WaitingForCombat, null, 0);
        t.Expand();
        Assert.True(t.Pinned);
        Assert.Equal(OverlayPresentation.Expanded, t.Current);
        for (int s = 1; s < 600; s += 37) Assert.Equal(OverlayPresentation.Expanded, Step(t, MeterState.WaitingForCombat, null, s));
        // The next fight releases the pin; the overlay stays expanded through it and its linger, then shrinks.
        Assert.Equal(OverlayPresentation.Expanded, Step(t, MeterState.InCombat, FightA, 600));
        Assert.False(t.Pinned);
        Assert.Equal(OverlayPresentation.Expanded, Step(t, MeterState.Ended, FightA, 700));
        Assert.Equal(OverlayPresentation.Compact, Step(t, MeterState.Ended, FightA, 716));
        Assert.Equal(OverlayPresentation.Compact, Step(t, MeterState.WaitingForCombat, null, 720));
    }

    [Fact]
    public void Pin_after_the_linger_shows_the_kept_result_again()
    {
        var t = new OverlayPresentationTracker();
        Step(t, MeterState.Ended, FightA, 0);
        Assert.Equal(OverlayPresentation.Compact, Step(t, MeterState.Ended, FightA, 30));
        t.Expand();
        Assert.Equal(OverlayPresentation.Expanded, Step(t, MeterState.Ended, FightA, 31));
        Assert.Equal(OverlayPresentation.Expanded, Step(t, MeterState.Ended, FightA, 300));
    }

    [Fact]
    public void Collapse_out_of_combat_just_drops_the_pin()
    {
        var t = new OverlayPresentationTracker();
        Step(t, MeterState.WaitingForCombat, null, 0);
        t.Expand();
        Step(t, MeterState.WaitingForCombat, null, 1);
        t.Collapse();
        Assert.False(t.Pinned);
        Assert.False(t.CollapsedByUser);
        Assert.Equal(OverlayPresentation.Compact, t.Current);
        Assert.Equal(OverlayPresentation.Compact, Step(t, MeterState.WaitingForCombat, null, 2));
        Assert.Equal(OverlayPresentation.Expanded, Step(t, MeterState.InCombat, FightA, 3)); // next fight expands as usual
    }

    [Fact]
    public void Collapse_during_a_fight_lasts_until_the_next_fight()
    {
        var t = new OverlayPresentationTracker();
        Step(t, MeterState.InCombat, FightA, 0);
        t.Collapse();
        Assert.True(t.CollapsedByUser);
        Assert.Equal(OverlayPresentation.Compact, Step(t, MeterState.InCombat, FightA, 1));
        Assert.Equal(OverlayPresentation.Compact, Step(t, MeterState.Ended, FightA, 2)); // no linger re-expansion
        Assert.Equal(OverlayPresentation.Expanded, Step(t, MeterState.InCombat, FightB, 30));
        Assert.False(t.CollapsedByUser);
    }

    [Fact]
    public void Collapse_during_the_linger_dismisses_the_result_and_a_click_expands_again()
    {
        var t = new OverlayPresentationTracker();
        Step(t, MeterState.InCombat, FightA, 0);
        Assert.Equal(OverlayPresentation.Expanded, Step(t, MeterState.Ended, FightA, 5));
        t.Collapse();
        Assert.Equal(OverlayPresentation.Compact, Step(t, MeterState.Ended, FightA, 6));
        t.Expand();
        Assert.False(t.CollapsedByUser);
        Assert.Equal(OverlayPresentation.Expanded, Step(t, MeterState.Ended, FightA, 7));
        // Back to waiting clears any leftover collapse state.
        t.Collapse();
        Step(t, MeterState.WaitingForCombat, null, 8);
        Assert.False(t.CollapsedByUser);
    }

    [Fact]
    public void Toast_expands_the_compact_bar_while_it_shows()
    {
        var t = new OverlayPresentationTracker();
        Assert.Equal(OverlayPresentation.Compact, Step(t, MeterState.WaitingForCombat, null, 0));
        Assert.Equal(OverlayPresentation.Expanded, Step(t, MeterState.WaitingForCombat, null, 1, toast: true));
        Assert.Equal(OverlayPresentation.Compact, Step(t, MeterState.WaitingForCombat, null, 9));
    }

    [Fact]
    public void Turning_the_setting_off_and_on_applies_immediately()
    {
        var t = new OverlayPresentationTracker();
        Assert.Equal(OverlayPresentation.Compact, Step(t, MeterState.WaitingForCombat, null, 0));
        Assert.Equal(OverlayPresentation.Expanded, Step(t, MeterState.WaitingForCombat, null, 1, shrink: false));
        Assert.Equal(OverlayPresentation.Compact, Step(t, MeterState.WaitingForCombat, null, 2));
    }

    [Fact]
    public void Training_armed_is_compact_and_a_running_training_run_expands()
    {
        // Armed: the engine reports WaitingForCombat with "Training ready" until the first hit.
        var t = new OverlayPresentationTracker();
        var armed = new MeterSnapshot { State = MeterState.WaitingForCombat, StatusText = "Training ready (60 s): hit a target to start", TimeUtc = T0 };
        Assert.Equal(OverlayPresentation.Compact, t.Update(armed.State, armed.EncounterId, T0, true, 15, false));
        var bar = CompactBarModel.Build(armed, PreviewData.Status() with { TrainingRemaining = TimeSpan.FromSeconds(58) });
        Assert.Equal("Training ready", bar.Status);
        Assert.Equal("0:58", bar.Hint);
        // Running: the training encounter is in combat.
        var running = PreviewData.LiveBoss() with { EncounterKind = EncounterKind.Training };
        Assert.Equal(MeterState.InCombat, running.State);
        Assert.Equal(OverlayPresentation.Expanded, t.Update(running.State, running.EncounterId, T0.AddSeconds(5), true, 15, false));
    }

    [Fact]
    public void Pvp_fights_expand()
    {
        var pvp = PreviewData.Pvp();
        Assert.Equal(MeterMode.Pvp, pvp.Mode);
        var t = new OverlayPresentationTracker();
        Assert.Equal(OverlayPresentation.Expanded, t.Update(pvp.State, pvp.EncounterId, pvp.TimeUtc, true, 15, false));
    }

    [Fact]
    public void Npcap_missing_is_compact_with_a_warning_dot()
    {
        var snap = PreviewData.WaitingForCombat() with { State = MeterState.Idle, MapName = null };
        var t = new OverlayPresentationTracker();
        Assert.Equal(OverlayPresentation.Compact, t.Update(snap.State, null, T0, true, 15, false));
        var bar = CompactBarModel.Build(snap, PreviewData.Status(CaptureState.NpcapMissing));
        Assert.Equal("Npcap missing", bar.Status);
        Assert.Equal(ThemeKeys.Negative, bar.DotKey);
        Assert.Contains("download", bar.Tooltip);
    }

    [Fact]
    public void Demo_engine_fight_expands_lingers_and_shrinks_end_to_end()
    {
        using var services = ServiceFactory.CreateDemo(originUtc: T0);
        var engine = services.Engine;
        engine.Options.EndedDisplaySeconds = 5;
        var t = new OverlayPresentationTracker();
        var seen = new List<(MeterState State, OverlayPresentation P)>();
        DateTime? endedAt = null;
        for (double s = 0; s <= 400; s += 0.5)
        {
            var now = T0.AddSeconds(s);
            var snap = engine.GetSnapshot(now);
            var p = t.Update(snap.State, snap.EncounterId, now, true, engine.Options.EndedDisplaySeconds, false);
            seen.Add((snap.State, p));
            if (snap.State == MeterState.Ended) endedAt ??= now;
            if (endedAt is { } e && (now - e).TotalSeconds > 8) break;
        }
        Assert.NotNull(endedAt);
        Assert.All(seen.Where(x => x.State == MeterState.InCombat), x => Assert.Equal(OverlayPresentation.Expanded, x.P));
        Assert.All(seen.Where(x => x.State == MeterState.Ended), x => Assert.Equal(OverlayPresentation.Expanded, x.P));
        Assert.All(seen.Where(x => x.State is MeterState.WaitingForCombat or MeterState.Idle), x => Assert.Equal(OverlayPresentation.Compact, x.P));
        Assert.Equal(OverlayPresentation.Compact, seen[^1].P); // the engine cleared the finished fight → compact
        Assert.Contains(seen, x => x.State == MeterState.InCombat);
    }

    // ───────────── Geometry ─────────────

    private static readonly Rect Work = new(0, 0, 1920, 1040);

    [Fact]
    public void Fit_keeps_an_anchor_that_already_fits() =>
        Assert.Equal(new Point(100, 200), OverlayGeometry.FitInside(new Point(100, 200), new Size(380, 300), Work));

    [Fact]
    public void Fit_shifts_up_near_the_bottom_edge_and_left_near_the_right_edge()
    {
        Assert.Equal(new Point(100, 740), OverlayGeometry.FitInside(new Point(100, 1010), new Size(380, 300), Work));
        Assert.Equal(new Point(1540, 200), OverlayGeometry.FitInside(new Point(1700, 200), new Size(380, 300), Work));
        Assert.Equal(new Point(1540, 740), OverlayGeometry.FitInside(new Point(1800, 1020), new Size(380, 300), Work));
    }

    [Fact]
    public void Fit_never_goes_above_the_work_area_or_moves_the_window_right_or_down()
    {
        // Taller than the work area: pinned to its top.
        Assert.Equal(new Point(100, 0), OverlayGeometry.FitInside(new Point(100, 500), new Size(380, 1400), Work));
        // Partly off the left/top edge on purpose: left alone on that side.
        Assert.Equal(new Point(-50, -5), OverlayGeometry.FitInside(new Point(-50, -5), new Size(380, 300), Work));
        // Second monitor on the right with its own work area.
        var right = new Rect(1920, 0, 2560, 1400);
        Assert.Equal(new Point(4100, 1100), OverlayGeometry.FitInside(new Point(4300, 1300), new Size(380, 300), right));
    }
}
