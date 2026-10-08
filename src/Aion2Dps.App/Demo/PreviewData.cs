using Aion2Dps.App.Overlay;

namespace Aion2Dps.App.Demo;

/// <summary>Deterministic sample snapshots for theme previews, offscreen renders and tests.</summary>
public static class PreviewData
{
    public static readonly DateTime Origin = new(2026, 10, 6, 20, 0, 0, DateTimeKind.Utc);

    private static FakeCombatEngine Engine(bool raid = false) => new(new FakeGameData(), seed: 7, originUtc: Origin, raid: raid);

    /// <summary>Live boss fight about a minute in (boss near half HP, adds up).</summary>
    public static MeterSnapshot LiveBoss(double secondsSinceOrigin = 72, bool raid = false)
    {
        var e = Engine(raid);
        return e.GetSnapshot(Origin.AddSeconds(secondsSinceOrigin));
    }

    /// <summary>
    /// Open-world field boss joined mid-fight (partial view): only your own direct hits are sent, a party member shows
    /// up through DoT ticks, ~40 other players are folded into one "Others" row; % = damage / boss max HP.
    /// </summary>
    public static MeterSnapshot WorldBossPartial()
    {
        var s = LiveBoss();
        var me = s.Rows.FirstOrDefault(r => r.IsLocal) ?? s.Rows[0];
        var mate = s.Rows.First(r => !r.IsLocal);
        const long max = 45_634_088;
        var rows = new List<PlayerRow>
        {
            me with { Rank = 1, Damage = 1_911_496, Dps = 5_416, Contribution = 1_911_496.0 / max, RelativeToTop = 1, Name = "You" },
            mate with { Rank = 2, Damage = 28_048, Dps = 79, Contribution = 28_048.0 / max, RelativeToTop = 28_048 / 1_911_496.0, IsPartyMember = true, Hits = 0, CritRate = 0 },
            new PlayerRow
            {
                EntityId = Aion2Dps.Combat.CombatEngine.OthersEntityId, Name = "Others (39, visible DoT/heal only)", Rank = 3,
                Damage = 488_989, Dps = 1_385, Contribution = 488_989.0 / max, RelativeToTop = 488_989 / 1_911_496.0, AggregateCount = 39,
            },
        };
        return s with
        {
            Rows = rows,
            TotalDamage = 2_428_533,
            HpCheckRatio = 0.054,
            PartialView = true,
            PartialViewText = "Partial view: only your/party damage is visible (5.4 % of the boss HP lost)",
            MapName = "Altgard",
            Target = s.Target is { } t ? t with { Name = "Special Operations Leader Linx", MaxHp = max, Hp = max / 3, HpFraction = 1 / 3.0 } : null,
            Bosses = [],
        };
    }

    /// <summary>Live boss fight late (boss low HP, colour shifted).</summary>
    public static MeterSnapshot LowHpBoss() => LiveBoss(118);

    /// <summary>Last fight ended in a kill.</summary>
    public static MeterSnapshot EndedKill()
    {
        var e = Engine();
        var t = Origin;
        MeterSnapshot s;
        // Walk forward until the first fight ends (deterministic).
        do
        {
            t = t.AddSeconds(2);
            s = e.GetSnapshot(t);
        } while (s.State != MeterState.Ended && t < Origin.AddMinutes(8));
        return e.GetSnapshot(t.AddSeconds(1));
    }

    public static MeterSnapshot Pvp(double secondsSinceOrigin = 64)
    {
        var e = Engine();
        e.GetSnapshot(Origin);
        e.Mode = MeterMode.Pvp;
        return e.GetSnapshot(Origin.AddSeconds(secondsSinceOrigin));
    }

    public static MeterSnapshot WaitingForCombat() => Engine().GetSnapshot(Origin.AddSeconds(1));

    /// <summary>The current encounter record of the live boss fight (for breakdown previews).</summary>
    public static EncounterRecord LiveRecord(double secondsSinceOrigin = 72)
    {
        var e = Engine();
        e.GetSnapshot(Origin.AddSeconds(secondsSinceOrigin));
        return e.GetCurrentEncounter()!;
    }

    public static CaptureStatus Capturing => new()
    {
        State = CaptureState.Capturing,
        Message = "Locked on game connection",
        AdapterName = "\\Device\\NPF_{DEMO}",
        AdapterDescription = "Intel(R) Ethernet Controller I225-V",
        LocalEndpoint = "192.168.1.42:52144",
        ServerEndpoint = "203.0.113.24:7101",
        PacketsSeen = 182_340,
        BytesDelivered = 75_102_000,
    };

    public static OverlayStatus Status(CaptureState state = CaptureState.Capturing) => new()
    {
        Capture = state == CaptureState.Capturing ? Capturing : new CaptureStatus { State = state, Message = state.ToString() },
    };
}
