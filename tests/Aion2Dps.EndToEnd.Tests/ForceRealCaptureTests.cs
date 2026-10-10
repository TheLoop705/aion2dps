using Aion2Dps.Capture;
using Aion2Dps.Contracts;
using Xunit.Abstractions;

namespace Aion2Dps.EndToEnd.Tests;

/// <summary>
/// Replays the user's real capture of a force (4 parties of 5) at the open-world world boss High Commander Lagta
/// (2026-10-10, recording started mid-fight) when it exists on this machine. Never committed (it contains player names);
/// reported as skipped elsewhere. [real] 2B 96 carries the HP of all 19 other force members, 1B 92 only the 4 of your party,
/// and even in a force the server sends only your own direct hits at an open-world boss.
/// </summary>
public sealed class ForceRealCaptureTests
{
    public const string ForceFile = "force-lagta-20261010.pcapng";

    private readonly ITestOutputHelper _out;

    public ForceRealCaptureTests(ITestOutputHelper output) => _out = output;

    [RealCaptureFact(ForceFile)]
    public void A_force_at_a_world_boss_ranks_the_force_and_hides_everyone_else()
    {
        var path = RealCaptureReplay.Find(ForceFile)!;

        // The app's options: boss fights only, automatic group scope.
        var h = new Harness(new EngineOptions { BossFightsOnly = true, AutoPartyScope = true });
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

        var r = Assert.Single(h.Records, x => x.Kind == EncounterKind.Boss);
        _out.WriteLine($"{r.Outcome} {r.DurationSeconds:0.0}s boss {r.BossNpcCode} partial {r.PartialView}");
        Assert.Equal(EncounterOutcome.Kill, r.Outcome);
        Assert.True(r.PartialView);

        // Your party (1B 92) and the other three parties of the force (2B 96): 4 + 15 players.
        Assert.Equal(4, r.Combatants.Count(c => c.IsPartyMember && !c.IsLocal));
        Assert.Equal(15, r.Combatants.Count(c => c.IsForceMember));
        Assert.DoesNotContain(r.Combatants, c => c.IsPartyMember && c.IsForceMember);
        // Only you have direct hits: every other force member is seen through DoT ticks and heals only.
        Assert.True(r.Combatants.Single(c => c.IsLocal).Quality.Hits > 500);
        Assert.All(r.Combatants.Where(c => (c.IsPartyMember || c.IsForceMember) && !c.IsLocal), c => Assert.Equal(0, c.Quality.Hits));

        Assert.NotNull(live);
        Assert.Equal(FightContext.FieldBoss, live!.Context);
        Assert.Equal(GroupScope.Force, live.Scope);
        Assert.Equal(20, live.GroupSize);
        // The force only: no strangers, no "Others" aggregate of the ~150 other players at the boss.
        Assert.All(live.Rows, row => Assert.True(row.IsLocal || row.IsPartyMember || row.IsForceMember, row.Name));
        Assert.DoesNotContain(live.Rows, row => row.AggregateCount > 0);
        Assert.True(live.Rows.Count <= 20);
    }
}
