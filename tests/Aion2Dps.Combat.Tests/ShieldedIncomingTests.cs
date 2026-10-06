using Aion2Dps.Contracts;

namespace Aion2Dps.Combat.Tests;

public sealed class ShieldedIncomingTests
{
    private const uint RetaliationSkill = 1_605_910;
    private const uint AbsorbSkill = 17_770_001;
    private const uint AbsorbEffect = 1_777_000_111;

    private static Script Fight()
    {
        var s = Script.Standard(new EngineOptions { MinBossFightSeconds = 0 });
        s.Hit(1, Script.Me, Script.Boss, 100);
        s.Hit(1.1, Script.Ally, Script.Boss, 200, Script.SorSkill);
        return s;
    }

    private static DotEvent Retaliation(double t = 2) => new()
    {
        Time = Script.At(t), Actor = Script.Boss, Target = Script.Ally, BundleDepth = 1,
        Flags = 0x4A, Amount = 500, EffectId = 160_591_011, SkillId = RetaliationSkill,
    };

    private static DotEvent Absorption(double t = 2) => new()
    {
        Time = Script.At(t), Actor = Script.Boss, Target = Script.Ally, BundleDepth = 1,
        Flags = 0x0A, Amount = 424, EffectId = AbsorbEffect, SkillId = AbsorbSkill,
    };

    private static DamageEvent Companion(double t = 2) => new()
    {
        Time = Script.At(t), Actor = Script.Boss, Target = Script.Ally, BundleDepth = 1,
        Switch = 0x44, Layout = 4, DamageType = 9, EffectId = 1_701_000_011, Amount = 76,
        AbsorbEffects = new[] { AbsorbEffect }, EffectValidated = false,
    };

    [Theory]
    [InlineData("RAC")]
    [InlineData("RCA")]
    [InlineData("ARC")]
    [InlineData("ACR")]
    [InlineData("CRA")]
    [InlineData("CAR")]
    public void A_validated_retaliation_absorb_companion_triple_counts_only_net_incoming_in_any_order(string order)
    {
        var s = Fight();
        foreach (char kind in order)
            s.Send(kind switch { 'R' => Retaliation(), 'A' => Absorption(), _ => Companion() });

        var rec = s.Record();
        Assert.Equal(76, Script.Combatant(rec, Script.Ally).DamageTaken);
        Assert.Equal(76, Script.Row(s.Snap(2), Script.Ally).DamageTaken);
        Assert.Equal(300, rec.TotalDamage);
        var incoming = Assert.Single(rec.Hits, h => h.Flags.HasFlag(HitFlags.Incoming));
        Assert.Equal(76, incoming.Amount);
        Assert.Equal(RetaliationSkill, incoming.Skill);
        Assert.Equal(Script.Boss, incoming.Source);
        Assert.Equal(1, s.Engine.DroppedRecords); // raw skill-less companion stays guarded
    }

    [Theory]
    [InlineData("time")]
    [InlineData("actor")]
    [InlineData("target")]
    [InlineData("depth")]
    [InlineData("effect")]
    [InlineData("companion_effect")]
    [InlineData("amount")]
    [InlineData("shape")]
    [InlineData("missing_absorb")]
    public void Unmatched_companions_cannot_rewrite_incoming_damage(string mismatch)
    {
        var s = Fight();
        var companion = Companion();
        companion = mismatch switch
        {
            "time" => companion with { Time = Script.At(2.001) },
            "actor" => companion with { Actor = Script.Trash },
            "target" => companion with { Target = Script.Me },
            "depth" => companion with { BundleDepth = 2 },
            "effect" => companion with { AbsorbEffects = new uint[] { AbsorbEffect + 1 } },
            "companion_effect" => companion with { EffectId = 1_701_000_012 },
            "amount" => companion with { Amount = 75 },
            "shape" => companion with { DamageType = 2 },
            _ => companion with { AbsorbEffects = Array.Empty<uint>() },
        };
        s.Send(Retaliation());
        s.Send(Absorption());
        s.Send(companion);

        Assert.Equal(924, Script.Combatant(s.Record(), Script.Ally).DamageTaken);
        Assert.Equal(300, s.Record().TotalDamage);
        Assert.Equal(2, s.Record().Hits.Count(h => h.Flags.HasFlag(HitFlags.Incoming)));
    }

    [Fact]
    public void Absorb_notifications_without_the_matching_validated_npc_tick_are_never_used_as_net_damage()
    {
        var s = Fight();
        s.Send(Absorption());
        s.Send(Companion());
        Assert.Equal(424, Script.Combatant(s.Record(), Script.Ally).DamageTaken);
        Assert.Equal(300, s.Record().TotalDamage);
    }

    [Fact]
    public void Only_authoritatively_spawned_npcs_can_supply_shielded_incoming_pairs()
    {
        const uint unknownNpc = 9999;
        var s = Fight();
        s.Send(Retaliation() with { Actor = unknownNpc });
        s.Send(Absorption() with { Actor = unknownNpc });
        s.Send(Companion() with { Actor = unknownNpc });
        Assert.Equal(500, Script.Combatant(s.Record(), Script.Ally).DamageTaken); // later class skill rebinds the guessed NPC
    }

    [Fact]
    public void Full_absorption_can_reduce_only_the_matched_retaliation_to_zero()
    {
        var s = Fight();
        s.Send(Retaliation());
        s.Send(Absorption() with { Amount = 500 });
        s.Send(Companion() with { Amount = 0 });
        Assert.Equal(0, Script.Combatant(s.Record(), Script.Ally).DamageTaken);
        Assert.Equal(300, s.Record().TotalDamage);
        Assert.Equal(0, Assert.Single(s.Record().Hits, h => h.Flags.HasFlag(HitFlags.Incoming)).Amount);
    }

    [Fact]
    public void Zone_changes_clear_pending_absorb_links_even_when_entity_ids_and_timestamp_are_reused()
    {
        var s = Fight();
        s.Send(Retaliation());
        s.Send(Absorption());
        s.Map(2, Script.Map2);
        s.SpawnNpc(2, Script.Boss, FakeGameData.BossCode, 3_600_000, 3_600_000);
        s.Player(2, Script.Ally, "Ally", 26);
        s.Hit(2, Script.Me, Script.Boss, 100);
        s.Hit(2, Script.Ally, Script.Boss, 200, Script.SorSkill);
        s.Send(Companion());

        Assert.Equal(924, Script.Combatant(s.Completed.Single(), Script.Ally).DamageTaken);
        Assert.Equal(0, Script.Combatant(s.Record(), Script.Ally).DamageTaken);
    }

    [Fact]
    public void Excess_pending_events_are_bounded_and_fail_closed_without_admitting_guarded_damage()
    {
        var s = Fight();
        for (int i = 0; i < 150; i++) s.Send(Retaliation());
        s.Send(Absorption());
        s.Send(Companion());

        Assert.Equal(149 * 500 + 76, Script.Combatant(s.Record(), Script.Ally).DamageTaken);
        Assert.Equal(300, s.Record().TotalDamage);
    }
}
