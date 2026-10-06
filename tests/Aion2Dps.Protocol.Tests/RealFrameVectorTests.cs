using System.Text.Json;
using Aion2Dps.Contracts;

namespace Aion2Dps.Protocol.Tests;

/// <summary>
/// Anonymised real Global frames (Fire Temple expedition + open world, 2026-10-06) in
/// <c>RealVectors/real-frames.json</c>. The expected values were computed by an independent Python reference decoder
/// written from PROTOCOL.md (layouts 0/4/6 cross-checked with a second, older Python decoder). No vector contains a
/// player or legion name.
/// </summary>
public class RealFrameVectorTests
{
    private static readonly Lazy<JsonDocument> Doc = new(() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "RealVectors", "real-frames.json"))));

    public static TheoryData<string> FrameIds()
    {
        var data = new TheoryData<string>();
        foreach (var f in Doc.Value.RootElement.GetProperty("frames").EnumerateArray()) data.Add(f.GetProperty("id").GetString()!);
        return data;
    }

    public static TheoryData<string> BundleIds()
    {
        var data = new TheoryData<string>();
        foreach (var f in Doc.Value.RootElement.GetProperty("bundles").EnumerateArray()) data.Add(f.GetProperty("id").GetString()!);
        return data;
    }

    private static JsonElement Vector(string section, string id) =>
        Doc.Value.RootElement.GetProperty(section).EnumerateArray().Single(f => f.GetProperty("id").GetString() == id);

    private static uint U(JsonElement e, string name) => e.GetProperty(name).GetUInt32();

    private static long? NL(JsonElement e, string name) =>
        e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : e.GetProperty(name).GetInt64();

    [Fact]
    public void Vector_file_covers_every_record_family()
    {
        var ids = Doc.Value.RootElement.GetProperty("frames").EnumerateArray().Select(f => f.GetProperty("id").GetString()!).ToList();
        Assert.True(ids.Count >= 40, $"only {ids.Count} vectors");
        foreach (var prefix in new[] { "dmg-l6", "dmg-l4", "dmg-l0", "dmg-l2", "dmg-placeholder", "dot-0A", "dot-0B", "dot-30", "stats-kind7", "stats-kind0", "spawn-boss", "buff-2A38", "buff-2B38", "buff-0E92" })
            Assert.Contains(ids, i => i.StartsWith(prefix, StringComparison.Ordinal));
        Assert.True(Doc.Value.RootElement.GetProperty("bundles").GetArrayLength() >= 2);
    }

    /// <summary>Every vector decodes as a single top-level frame without errors, through the frame decoder too.</summary>
    [Theory]
    [MemberData(nameof(FrameIds))]
    public void Real_frame_decodes_through_the_pipeline_without_errors(string id)
    {
        var v = Vector("frames", id);
        var (events, pipe) = Run.Wire(W.H(v.GetProperty("frame").GetString()!));
        var d = pipe.ProtocolDiagnostics;
        Assert.True(d.DecodeErrors == 0, string.Join("\n", d.GetRecentErrors()));
        Assert.Equal(1, d.Frames);
        Assert.Equal(0, d.Resyncs);
        Assert.Equal(0, d.DamageTrailingBytes);
    }

    [Theory]
    [MemberData(nameof(FrameIds))]
    public void Real_frame_matches_the_reference_decoder(string id)
    {
        var v = Vector("frames", id);
        ushort op = Convert.ToUInt16(v.GetProperty("opcode").GetString(), 16);
        var exp = v.GetProperty("expect");
        var (events, diag) = Run.Body(op, v.GetProperty("body").GetString()!);
        Assert.True(diag.DecodeErrors == 0, string.Join("\n", diag.GetRecentErrors()));

        switch (op)
        {
            case Opcodes.Damage: CheckDamage(exp, events, diag); break;
            case Opcodes.DotTick: CheckDot(exp, events, diag); break;
            case Opcodes.EntityStats: CheckStats(exp, events); break;
            case Opcodes.Spawn: CheckSpawn(exp, events); break;
            case Opcodes.BuffApplied or Opcodes.BuffApplied2: CheckBuffs(exp, events); break;
            case Opcodes.BuffRemoved: CheckRemoved(exp, events); break;
            case Opcodes.HpUpdate:
            {
                var e = Assert.IsType<HpUpdateEvent>(Assert.Single(events));
                Assert.Equal(U(exp, "entity"), e.Entity);
                Assert.Equal(U(exp, "hp"), e.Hp);
                Assert.Equal(U(exp, "hp_max"), e.HpMax);
                break;
            }
            default: Assert.Fail($"no checker for opcode {op:X4}"); break;
        }
    }

    private static void CheckDamage(JsonElement x, List<GameEvent> events, ProtocolDiagnostics diag)
    {
        if (x.GetProperty("placeholder").GetBoolean())
        {
            Assert.Empty(events);
            Assert.Equal(1, diag.DamagePlaceholders);
            return;
        }

        var e = Assert.IsType<DamageEvent>(Assert.Single(events));
        Assert.Equal(U(x, "target"), e.Target);
        Assert.Equal(U(x, "actor"), e.Actor);
        Assert.Equal(U(x, "switch"), e.Switch);
        Assert.Equal(x.GetProperty("absorbed").EnumerateArray().Select(a => a.GetUInt32()), e.AbsorbEffects);
        Assert.Equal(U(x, "layout"), e.Layout);
        Assert.Equal(U(x, "skill_raw"), e.SkillRaw);
        Assert.Equal(U(x, "skill"), e.SkillId);
        Assert.Equal(U(x, "hit_tag"), e.HitTag);
        Assert.Equal(U(x, "dmg_type"), e.DamageType);
        Assert.Equal(U(x, "mods"), (uint)e.Mods);
        Assert.Equal(U(x, "dir"), (uint)e.Direction);
        Assert.Equal(U(x, "effect"), e.EffectId);
        Assert.Equal(U(x, "hit_index"), e.HitIndex);
        Assert.Equal(U(x, "scalar"), e.PowerScalar);
        Assert.Equal(NL(x, "amount"), e.Amount);
        Assert.Equal(x.GetProperty("extras").EnumerateArray().Select(a => a.GetUInt32()).ToArray(), e.ExtraHits.ToArray());
        Assert.Equal(x.GetProperty("effect_ok").GetBoolean(), e.EffectValidated);
        Assert.Equal(1, diag.DamageRecords);
        Assert.Equal(0, diag.DamageTrailingBytes);
    }

    private static void CheckDot(JsonElement x, List<GameEvent> events, ProtocolDiagnostics diag)
    {
        if (x.GetProperty("trigger").GetBoolean())
        {
            Assert.Empty(events);
            Assert.Equal(1, diag.DotTriggerTicks);
            return;
        }

        var e = Assert.IsType<DotEvent>(Assert.Single(events));
        Assert.Equal(U(x, "target"), e.Target);
        Assert.Equal(U(x, "flags"), e.Flags);
        Assert.Equal(U(x, "actor"), e.Actor);
        Assert.Equal(U(x, "stack"), e.Stack);
        Assert.Equal(U(x, "effect"), e.EffectId);
        Assert.Equal(NL(x, "amount"), e.Amount);
        Assert.Equal(NL(x, "heal"), e.Heal);
        Assert.Equal(NL(x, "skill"), e.SkillId);
    }

    private static void CheckStats(JsonElement x, List<GameEvent> events)
    {
        var e = Assert.IsType<EntityStatsEvent>(Assert.Single(events));
        Assert.Equal(U(x, "entity"), e.Entity);
        Assert.Equal(U(x, "format"), e.Format);
        var s32 = x.GetProperty("s32").EnumerateObject().ToDictionary(p => byte.Parse(p.Name), p => p.Value.GetUInt32());
        var s64 = x.GetProperty("s64").EnumerateObject().ToDictionary(p => byte.Parse(p.Name), p => p.Value.GetInt64());
        Assert.Equal(s32.OrderBy(k => k.Key), e.Stats32.OrderBy(k => k.Key));
        Assert.Equal(s64.OrderBy(k => k.Key), e.Stats64.OrderBy(k => k.Key));
        Assert.Equal(s64.TryGetValue(0, out long hp) ? hp : null, e.CurrentHp);
    }

    private static void CheckSpawn(JsonElement x, List<GameEvent> events)
    {
        var e = Assert.IsType<SpawnEvent>(Assert.Single(events));
        Assert.Equal(U(x, "entity"), e.Entity);
        Assert.Equal(U(x, "kind"), e.KindByte);
        Assert.Equal(U(x, "kind_flags"), e.KindFlags);
        Assert.Equal(U(x, "npc"), e.NpcCode);
        Assert.Equal(NL(x, "hp_cur"), e.HpCurrent);
        Assert.Equal(NL(x, "hp_max"), e.HpMax);
        Assert.Equal(NL(x, "owner"), e.OwnerId);
        Assert.Equal(x.GetProperty("x").GetSingle(), e.X);
        Assert.Equal(x.GetProperty("y").GetSingle(), e.Y);
        Assert.Equal(x.GetProperty("z").GetSingle(), e.Z);
    }

    private static void CheckBuffs(JsonElement x, List<GameEvent> events)
    {
        var expected = x.EnumerateArray().ToList();
        var got = events.Cast<BuffAppliedEvent>().ToList();
        Assert.Equal(expected.Count, got.Count);
        for (int i = 0; i < got.Count; i++)
        {
            var b = expected[i];
            Assert.Equal(U(b, "target"), got[i].Target);
            Assert.Equal(U(b, "stack"), got[i].Stack);
            Assert.Equal(U(b, "buff"), got[i].BuffId);
            Assert.Equal(U(b, "duration"), got[i].DurationMs);
            Assert.Equal(b.GetProperty("expiry").GetUInt64(), got[i].ExpiryUnixMs);
            Assert.Equal(U(b, "caster"), got[i].Caster);
            Assert.Equal(U(b, "source"), got[i].SourceSkill);
        }
    }

    private static void CheckRemoved(JsonElement x, List<GameEvent> events)
    {
        var ids = x.GetProperty("buffs").EnumerateArray().Select(a => a.GetUInt32()).ToList();
        var got = events.Cast<BuffRemovedEvent>().ToList();
        Assert.Equal(ids, got.Select(g => g.BuffId).ToList());
        Assert.All(got, g => Assert.Equal(U(x, "target"), g.Target));
    }

    [Theory]
    [MemberData(nameof(BundleIds))]
    public void Real_lz4_bundle_unpacks_to_the_reference_frames(string id)
    {
        var v = Vector("bundles", id);
        var (frames, dec) = Run.Frames(W.H(v.GetProperty("frame").GetString()!));
        var d = dec.Diagnostics;
        Assert.Equal(1, d.Bundles);
        Assert.Equal(0, d.BundleErrors);
        Assert.Equal(v.GetProperty("inner_frames").GetInt32(), frames.Frames.Count);
        var expected = v.GetProperty("opcodes").EnumerateObject().ToDictionary(p => Convert.ToUInt16(p.Name, 16), p => p.Value.GetInt32());
        var actual = frames.Frames.GroupBy(f => f.Op).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(expected.OrderBy(k => k.Key), actual.OrderBy(k => k.Key));
        Assert.All(frames.Frames, f => Assert.Equal(1, f.Depth));

        // Same bytes through the full pipeline: damage amounts and no decode errors.
        var (events, pipe) = Run.Wire(W.H(v.GetProperty("frame").GetString()!));
        Assert.True(pipe.ProtocolDiagnostics.DecodeErrors == 0, string.Join("\n", pipe.ProtocolDiagnostics.GetRecentErrors()));
        var dmg = events.Of<DamageEvent>();
        Assert.Equal(v.GetProperty("damage_records").GetInt32(), dmg.Count);
        Assert.Equal(v.GetProperty("damage_amount_sum").GetInt64(), dmg.Sum(e => e.Amount ?? 0));
    }

    // ───────────── explicit, readable checks of the new real-traffic findings ─────────────

    [Fact]
    public void Layout_2_is_an_evaded_npc_hit_without_amount()
    {
        var e = Run.Single<DamageEvent>("0438A17B0200CA9804BAC412000201000002B3D8540701000000904E0100");
        Assert.Equal(2, e.Layout);
        Assert.Equal(1, e.DamageType);   // 1 = evaded
        Assert.Null(e.Amount);
        Assert.Equal(HitDirection.Front, e.Direction);
        Assert.True(e.EffectValidated);
    }

    [Fact]
    public void Dodge_trigger_tick_is_counted_not_emitted()
    {
        var (events, diag) = Run.Payload("0538FC5A30FC5A68D107000087970144B81200");
        Assert.Empty(events);
        Assert.Equal(0, diag.DecodeErrors);
        Assert.Equal(1, diag.DotTriggerTicks);
    }

    [Fact]
    public void Shield_absorb_block_is_skipped_and_the_amount_kept()
    {
        var e = Run.Single<DamageEvent>("0438F3B7024600A17B680BC8001502000002D545244E02000000F462C50401C3414A070200");
        Assert.Equal(0x46u, e.Switch);
        Assert.Equal(581, e.Amount);

        var dot = Run.Single<DotEvent>("0538F3B7020EA17B020854D6514D01C3414A07D780D100");
        Assert.Equal(77, dot.Amount);
        Assert.Equal(13730007u, dot.SkillId);
    }

    [Fact]
    public void Max_hp_scaling_placeholder_is_not_a_failure_and_not_emitted()
    {
        var (events, diag) = Run.Payload("0438DCAB020400DCAB025E420F00010251E1F50501000000904E80CAB5EE010100");
        Assert.Empty(events);
        Assert.Equal(0, diag.DecodeErrors);
        Assert.Equal(1, diag.DamagePlaceholders);

        // Above the cap between two different entities is still a decode failure.
        var (e2, d2) = Run.Payload("0438DCAB020400DCAB035E420F00010251E1F50501000000904E80CAB5EE010100");
        Assert.Empty(e2);
        Assert.Equal(1, d2.DecodeErrors);
    }
}
