using Aion2Dps.Contracts;

namespace Aion2Dps.Protocol.Tests;

/// <summary>Real <c>05 38</c> ticks (PROTOCOL.md §8.3, stacys SelfCheckAion2.cs:320-333, 390-395, 987-995).</summary>
public class DotTests
{
    [Theory]
    [InlineData("960F4AA3E30158A36C9209F4037BB4CB0016811800", 1942u, 500)]
    [InlineData("D17F4AA3E30158A36C9209F403C88D030116811800", 16337u, 500)]
    [InlineData("D85B4AA3E30158A36C9209FB02FC08AD0016811800", 11736u, 379)]
    public void Retaliation_tick_4A_uses_effect_skill_instead_of_player_trigger(string body, uint target, long amount)
    {
        // Lakshmi's attack effect reacts to a player skill: the final 0x40 field names the NPC's ability.
        var (events, diag) = Run.Body(Opcodes.DotTick, body);
        var d = Assert.IsType<DotEvent>(Assert.Single(events));
        Assert.Equal(target, d.Target);
        Assert.Equal(29091u, d.Actor);
        Assert.Equal(160591011u, d.EffectId);
        Assert.Equal(0x4A, d.Flags);
        Assert.Equal(amount, d.Amount);
        Assert.Equal(1605910u, d.SkillId);
        Assert.Equal(SkillKind.Npc, SkillIds.GetKind(d.SkillId!.Value));
        Assert.True(d.IsDamageTick);
        Assert.Equal(0, diag.DecodeErrors);
    }

    [Theory]
    [InlineData("960F4AA3E30158A36C9209F4037BB4CB00")]
    [InlineData("960F4AA3E30158A36C9209F4037BB4CB0016")]
    [InlineData("960F4AA3E30158A36C9209F4037BB4CB001681")]
    [InlineData("960F4AA3E30158A36C9209F4037BB4CB00168118")]
    [InlineData("960F4AA3E30158A36C9209F4037BB4CB0017811800")]
    [InlineData("960F4AA3E30158A36C9209F4037BB4CB001681180000")]
    [InlineData("960FCAA3E30158A36C9209F4037BB4CB0016811800")]
    public void Retaliation_tick_rejects_truncated_mismatched_or_unknown_fields(string body)
    {
        var (events, diag) = Run.Body(Opcodes.DotTick, body);
        Assert.Empty(events);
        Assert.Equal(1, diag.DecodeErrors);
    }

    [Fact]
    public void Damage_tick_0A_has_amount_and_exact_skill()
    {
        var d = Run.Single<DotEvent>("0538D490020AC522108BAF3360A804E046F600");
        Assert.Equal(34900u, d.Target);
        Assert.Equal(4421u, d.Actor);
        Assert.Equal(0x0A, d.Flags);
        Assert.Equal(16u, d.Stack);
        Assert.Equal(552, d.Amount);
        Assert.Null(d.Heal);
        Assert.Equal(16140000u, d.SkillId); // Jointstrike: Curse
        Assert.True(d.IsDamageTick);
        Assert.False(d.IsHealTick);
        Assert.False(d.IsMonsterEffect);
    }

    [Fact]
    public void Second_player_tick_and_monster_dot_on_a_player()
    {
        var a = Run.Single<DotEvent>("0538D490020AD924248BAF3360C901E046F600");
        Assert.Equal(4697u, a.Actor);
        Assert.Equal(201, a.Amount);

        var m = Run.Single<DotEvent>("0538C5220AD49002F402E71327076AE0771B00");
        Assert.Equal(4421u, m.Target);
        Assert.Equal(34900u, m.Actor);
        Assert.Equal(106, m.Amount);
        Assert.Equal(120001511u, m.EffectId);
        Assert.True(m.IsMonsterEffect);
        Assert.Equal(SkillKind.Npc, SkillIds.GetKind(m.SkillId!.Value));
    }

    [Fact]
    public void Flags_0B_is_a_hot_tick_heal_is_the_second_field()
    {
        var d = Run.Single<DotEvent>("0538833A0B833A91015FB2FC0BFF03AA01DDAF1E00");
        Assert.Equal(7427u, d.Target);
        Assert.Equal(7427u, d.Actor);
        Assert.Equal(0x0B, d.Flags);
        Assert.Equal(145u, d.Stack);
        Assert.Equal(511, d.Amount);
        Assert.Equal(170, d.Heal);
        Assert.True(d.IsHealTick);
        Assert.False(d.IsDamageTick);
    }

    [Fact]
    public void Recuperation_announcement_09_then_0B_ticks_of_83()
    {
        var announce = Run.Single<DotEvent>("0538862D09833A240BED006CCE02407D1401");
        Assert.Equal(0x09, announce.Flags);
        Assert.Null(announce.Amount);
        Assert.Equal(334, announce.Heal);
        Assert.Equal(18120000u, announce.SkillId);
        Assert.False(announce.IsHealTick);
        Assert.False(announce.IsDamageTick);

        var t1 = Run.Single<DotEvent>("0538862D0B833A240BED006CFB0153407D1401");
        var t2 = Run.Single<DotEvent>("0538862D0B833A240BED006CA80153407D1401");
        Assert.Equal(251, t1.Amount); // remaining
        Assert.Equal(83, t1.Heal);
        Assert.Equal(168, t2.Amount);
        Assert.Equal(83, t2.Heal);
        Assert.True(t1.IsHealTick && t2.IsHealTick);
    }

    [Fact]
    public void Flags_09_and_08_ticks()
    {
        var hot = Run.Single<DotEvent>("0538D92409833A240BED006CBA02407D1401");
        Assert.Equal(4697u, hot.Target);
        Assert.Equal(314, hot.Heal);
        Assert.Null(hot.Amount);

        var status = Run.Single<DotEvent>("0538C52208C522EC0195D32761E2B7F800");
        Assert.Equal(0x08, status.Flags);
        Assert.Equal(4421u, status.Target);
        Assert.Equal(4421u, status.Actor);
        Assert.Equal(236u, status.Stack);
        Assert.Null(status.Amount);
        Assert.Null(status.Heal);
        Assert.Equal(16300002u, status.SkillId);
        Assert.False(status.IsDamageTick);
    }

    [Fact]
    public void Recuperation_0A_on_a_party_member_decodes_like_a_damage_tick()
    {
        // Heal-vs-damage is decided downstream from data tables (§8.2.2); the decoder reports the wire facts.
        var d = Run.Single<DotEvent>("0538D9240A833A490BED006C4D407D1401");
        Assert.Equal(77, d.Amount);
        Assert.Equal(18120000u, d.SkillId);
    }

    /// <summary>Boss self-heal trap (§8.3): 9-digit monster effect + class skill = the target heals itself.</summary>
    [Fact]
    public void Boss_self_heal_sequence_balances_the_hp_equation()
    {
        string[] frames =
        [
            "0538A2BC020AB4360D5699BE53BD012464D600",
            "008DA2BC020201007C290A0000000000",
            "0538A2BC020AB436175B849009A4BF0783C7D500",
            "0438A2BC023600B43683C7D500EE0280000118A1815301000000FA60E9EF0104FD17FD17FD17FD170100",
            "008DA2BC0202010037910B0000000000",
            "0438A2BC020600B436575FE100F002000001073E095801000000FA60C6060100",
        ];
        var (events, pipe) = Run.Wire(W.Concat(frames.Select(W.Frame).ToArray()));
        Assert.Equal(0, pipe.Diagnostics.DecodeErrors);
        Assert.Equal(6, events.Events.Count);

        var drill = (DotEvent)events.Events[0];
        Assert.Equal(189, drill.Amount);
        Assert.False(drill.IsMonsterEffect);

        var hp1 = (EntityStatsEvent)events.Events[1];
        var selfHeal = (DotEvent)events.Events[2];
        var deadshot = (DamageEvent)events.Events[3];
        var hp2 = (EntityStatsEvent)events.Events[4];
        var rooting = (DamageEvent)events.Events[5];

        Assert.Equal(40482u, hp1.Entity);
        Assert.Equal(665_980, hp1.CurrentHp);
        Assert.True(selfHeal.IsMonsterEffect);
        Assert.Equal(160466011u, selfHeal.EffectId);
        Assert.Equal(14010243u, selfHeal.SkillId);
        Assert.Equal(CharacterClass.Ranger, SkillIds.ClassOf(selfHeal.SkillId!.Value));
        Assert.Equal(122_788, selfHeal.Amount);
        Assert.Equal(30_697, deadshot.Amount);
        Assert.Equal(758_071, hp2.CurrentHp);
        Assert.Equal(838, rooting.Amount);
        Assert.Equal(hp1.CurrentHp + selfHeal.Amount - hp2.CurrentHp, deadshot.Amount);
    }

    [Theory]
    [InlineData("D490020AC522108BAF3360A804E046F600FF")]   // trailing byte
    [InlineData("D490020AC522108BAF3360A804E046")]         // truncated skill
    [InlineData("D49002")]
    public void Dot_frames_must_end_exactly(string body)
    {
        var (events, diag) = Run.Body(Opcodes.DotTick, body);
        Assert.Empty(events);
        Assert.Equal(1, diag.DecodeErrors);
    }

    [Fact]
    public void Synthetic_flags_02_without_skill()
    {
        // cyber's synthetic shape: target, flags 02, actor, stack, effect, amount 500.
        var d = Run.Single<DotEvent>("0538 b1ea01 02 f30a 00 00199B5F f403");
        Assert.Equal(500, d.Amount);
        Assert.Null(d.SkillId);
        Assert.True(d.IsDamageTick);
        Assert.Equal(1_604_000_000u, d.EffectId);
    }
}

/// <summary>Real <c>00 8D</c> frames (§8.4, stacys SelfCheckAion2.cs:424-429, 637-638).</summary>
public class EntityStatsTests
{
    [Theory]
    [InlineData("008DCE8D0102010008DD010000000000", 122_120L)]
    [InlineData("008DCE8D01020100F481010000000000", 98_804L)]
    [InlineData("008DCE8D0102010078E0010000000000", 123_000L)]
    [InlineData("008DCE8D0102010093DD010000000000", 122_259L)]
    public void Boss_hp_frames(string payload, long hp)
    {
        var e = Run.Single<EntityStatsEvent>(payload);
        Assert.Equal(18126u, e.Entity);
        Assert.Equal(0x02, e.Format);
        Assert.Equal(hp, e.CurrentHp);
        Assert.False(e.HasSelfStats);
        Assert.Empty(e.Stats32);
        Assert.Equal(hp, e.Stats64[0]);
    }

    [Fact]
    public void Player_frame_mixes_4_byte_stats_and_hp()
    {
        var e = Run.Single<EntityStatsEvent>("008DCF19030201900A000003D89E01000100BD24000000000000");
        Assert.Equal(3279u, e.Entity);
        Assert.True(e.HasSelfStats);
        Assert.Equal(9_405, e.CurrentHp);
        Assert.Equal(2, e.Stats32.Count);
        Assert.Equal(2704u, e.Stats32[1]);
        Assert.Equal(106_200u, e.Stats32[3]);
    }

    [Fact]
    public void Stats_only_frame_has_no_current_hp()
    {
        var e = Run.Single<EntityStatsEvent>("008DCF19030508CD0700000A60B201000BF04902000CA08601000D00E2040001075B1B000000000000");
        Assert.Null(e.CurrentHp);
        Assert.Equal(5, e.Stats32.Count);
        Assert.Equal(7003L, e.Stats64[7]);
    }

    [Theory]
    [InlineData("008DBB5B010103508B0100", 3, 0x18B50u)]
    [InlineData("008DBB5B010101500B0000", 1, 0xB50u)]
    [InlineData("008DBB5B01010612DF0400", 6, 0x4DF12u)]
    public void Format_01_frames(string payload, byte kind, uint value)
    {
        var e = Run.Single<EntityStatsEvent>(payload);
        Assert.Equal(11707u, e.Entity);
        Assert.Null(e.CurrentHp);
        Assert.Equal(value, e.Stats32[kind]);
    }

    [Theory]
    [InlineData("CE8D0102010008DD01000000000000")]   // one byte too many
    [InlineData("CE8D0102010008DD0100000000")]       // truncated
    [InlineData("CE8D010401")]                       // unknown format bit
    public void Malformed_stats_frames_fail(string body)
    {
        var (events, diag) = Run.Body(Opcodes.EntityStats, body);
        Assert.Empty(events);
        Assert.Equal(1, diag.DecodeErrors);
    }
}
