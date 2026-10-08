using Aion2Dps.Capture;
using Aion2Dps.Combat;
using Aion2Dps.Contracts;
using Xunit.Abstractions;

namespace Aion2Dps.EndToEnd.Tests;

/// <summary>
/// Replays the user's real open-world field-boss capture (2026-10-08, Special Operations Leader Linx, recording started
/// mid-fight: no boss spawn, no <c>33 36</c>, no map load, no party roster) through Capture → Protocol → Combat when it
/// exists on this machine. Never committed (it contains player names); skipped silently elsewhere.
/// </summary>
public sealed class WorldBossRealCaptureTests
{
    public const string WorldBossFile = "worldboss-linx-2026-10-08.pcap";
    private const uint Linx = 2_400_419;
    private const uint LocalEntity = 13888;

    private readonly ITestOutputHelper _out;

    public WorldBossRealCaptureTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void World_boss_fight_is_a_named_partial_view_with_you_marked()
    {
        var path = RealCaptureReplay.Find(WorldBossFile);
        if (path is null)
        {
            _out.WriteLine("World-boss capture not present on this machine: skipped.");
            return;
        }

        var h = new Harness();
        MeterSnapshot? live = null;
        DateTime last = default;
        var stats = PcapReplaySource.Replay(path, h.Input, 0, utc =>
        {
            if ((utc - last).TotalMilliseconds < 250) return;
            last = utc;
            h.Engine.Tick(utc);
            var snap = h.Engine.GetSnapshot(utc);
            if (snap is { State: MeterState.InCombat, EncounterKind: EncounterKind.Boss }) live = snap;
        });
        h.Drain(stats.LastPacketUtc ?? last);

        Assert.Equal(0, h.Pipeline.Diagnostics.DecodeErrors);
        var r = Assert.Single(h.Records, x => x.Kind == EncounterKind.Boss);
        _out.WriteLine($"{r.Outcome} {r.DurationSeconds:0.0}s map {r.MapId} boss {r.BossNpcCode} partial {r.PartialView} ({r.PartialViewReason})");

        // Item 4/5: the boss is named from the field-boss list, the map from the list's map.
        Assert.Equal(Linx, r.BossNpcCode);
        Assert.Equal(1110u, r.MapId);
        Assert.Equal(EncounterOutcome.Kill, r.Outcome);
        Assert.Equal(45_634_088, r.BossMaxHp);

        // Item 2: partial view, contribution = own damage / boss max HP.
        Assert.True(r.PartialView);
        Assert.InRange(r.VisibleDamageRatio!.Value, 0.04, 0.07);
        var me = Assert.Single(r.Combatants, c => c.IsLocal);
        Assert.Equal(LocalEntity, me.EntityId);
        Assert.Equal("You", me.Name); // item 3: never "Player 13888"
        Assert.InRange(me.Contribution, 0.035, 0.05);
        Assert.Equal((double)me.Damage / r.BossMaxHp!.Value, me.Contribution, 6);

        // Item 1: the boss resists critical hits (no crit cluster at all), every hit is decoded and counted.
        Assert.True(me.Quality.Hits > 1500);
        Assert.Equal(0, me.Quality.Crits);

        // Party evidence without a roster: the four group members of the 1B 92 HP updates.
        Assert.True(r.Combatants.Count(c => c.IsPartyMember) >= 3);

        // Live view: partial, you first, DoT-only strangers folded into one row.
        Assert.NotNull(live);
        Assert.True(live!.PartialView);
        Assert.True(live.Rows[0].IsLocal);
        Assert.Equal("Special Operations Leader Linx", live.Target!.Name);
        Assert.Equal("Altgard", live.MapName);
        var others = Assert.Single(live.Rows, x => x.AggregateCount > 0);
        Assert.Equal(CombatEngine.OthersEntityId, others.EntityId);
        Assert.True(others.AggregateCount > 20);
        Assert.True(live.Rows.Count < 12);
    }
}
