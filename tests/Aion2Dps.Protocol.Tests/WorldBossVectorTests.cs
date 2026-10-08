using System.Text.Json;
using Aion2Dps.Contracts;

namespace Aion2Dps.Protocol.Tests;

/// <summary>
/// Anonymised real frames of an open-world field-boss fight (Special Operations Leader Linx, 2026-10-08) in
/// <c>RealVectors/worldboss-frames.json</c>: the local player's hits on the boss (never a crit there), a crit on another
/// target of the same capture, the flag-0x10 damage records, a positioned cast of the boss, the <c>01 91</c> field-boss
/// list, the boss's HP reading and a party member's <c>1B 92</c>. Expected values come from the independent Python
/// reference decoder and scratch parsers written from PROTOCOL.md. No vector contains a name.
/// </summary>
public class WorldBossVectorTests
{
    private static readonly Lazy<JsonDocument> Doc = new(() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "RealVectors", "worldboss-frames.json"))));

    public static TheoryData<string> FrameIds()
    {
        var data = new TheoryData<string>();
        foreach (var f in Doc.Value.RootElement.GetProperty("frames").EnumerateArray()) data.Add(f.GetProperty("id").GetString()!);
        return data;
    }

    private static JsonElement Vector(string id) =>
        Doc.Value.RootElement.GetProperty("frames").EnumerateArray().Single(f => f.GetProperty("id").GetString() == id);

    private static (List<GameEvent> Events, ProtocolDiagnostics Diag) Decode(string id)
    {
        var v = Vector(id);
        ushort op = Convert.ToUInt16(v.GetProperty("opcode").GetString(), 16);
        var r = Run.Body(op, v.GetProperty("body").GetString()!);
        Assert.True(r.Diag.DecodeErrors == 0, string.Join("\n", r.Diag.GetRecentErrors()));
        return r;
    }

    private static uint U(JsonElement e, string name) => e.GetProperty(name).GetUInt32();

    [Theory]
    [MemberData(nameof(FrameIds))]
    public void World_boss_frame_decodes_through_the_pipeline_without_errors(string id)
    {
        var (_, pipe) = Run.Wire(W.H(Vector(id).GetProperty("frame").GetString()!));
        var d = pipe.ProtocolDiagnostics;
        Assert.True(d.DecodeErrors == 0, string.Join("\n", d.GetRecentErrors()));
        Assert.Equal(1, d.Frames);
        Assert.Equal(0, d.DamageTrailingBytes);
    }

    [Theory]
    [InlineData("wb-dmg-l6-local")]
    [InlineData("wb-dmg-l4-flag4")]
    [InlineData("wb-dmg-crit-elsewhere")]
    [InlineData("wb-dmg-flag10-l4-tail")]
    [InlineData("wb-dmg-flag10-l6")]
    public void Damage_record_matches_the_reference_decoder(string id)
    {
        var x = Vector(id).GetProperty("expect");
        var (events, diag) = Decode(id);
        var e = Assert.IsType<DamageEvent>(Assert.Single(events));
        Assert.Equal(U(x, "target"), e.Target);
        Assert.Equal(U(x, "actor"), e.Actor);
        Assert.Equal(U(x, "switch"), e.Switch);
        Assert.Equal(U(x, "skill"), e.SkillId);
        Assert.Equal(U(x, "dmg_type"), e.DamageType);
        Assert.Equal(U(x, "dmg_type") == 3, e.IsCritical);
        Assert.Equal(U(x, "mods"), (uint)e.Mods);
        Assert.Equal(U(x, "dir"), (uint)e.Direction);
        Assert.Equal(U(x, "effect"), e.EffectId);
        Assert.Equal(U(x, "hit_index"), e.HitIndex);
        Assert.Equal(U(x, "scalar"), e.PowerScalar);
        Assert.Equal(x.GetProperty("amount").GetInt64(), e.Amount);
        Assert.Equal(x.GetProperty("absorbed").EnumerateArray().Select(a => a.GetUInt32()), e.AbsorbEffects);
        Assert.True(e.EffectValidated);
        Assert.Equal(1, diag.DamageRecords);
        Assert.Equal(0, diag.DamageTrailingBytes);
    }

    [Fact]
    public void Flag_0x10_tail_is_read_only_on_layout_4()
    {
        // Layout 4: one varint follows the trailer; layout 6 (mods 0x80): the trailer ends the record.
        Assert.Equal(1u, U(Vector("wb-dmg-flag10-l4-tail").GetProperty("expect"), "tail"));
        Assert.Equal(JsonValueKind.Null, Vector("wb-dmg-flag10-l6").GetProperty("expect").GetProperty("tail").ValueKind);
        // The layout-6 record with an extra byte appended does not grow a tail: the byte is left over.
        string body = Vector("wb-dmg-flag10-l6").GetProperty("body").GetString()!;
        var (events, diag) = Run.Body(Opcodes.Damage, body + "01");
        Assert.Single(events);
        Assert.Equal(1, diag.DamageTrailingBytes);
    }

    [Fact]
    public void Positioned_cast_carries_the_casters_coordinates()
    {
        var x = Vector("wb-cast-kind2-position").GetProperty("expect");
        var (events, _) = Decode("wb-cast-kind2-position");
        var c = Assert.IsType<CastEvent>(Assert.Single(events));
        Assert.Equal(U(x, "actor"), c.Actor);
        Assert.Equal(U(x, "skill_raw"), c.SkillRaw);
        Assert.Equal(U(x, "target"), c.Target);
        Assert.Equal(x.GetProperty("x").GetSingle(), c.X);
        Assert.Equal(x.GetProperty("y").GetSingle(), c.Y);
        Assert.Equal(x.GetProperty("z").GetSingle(), c.Z);
    }

    [Fact]
    public void Field_boss_list_decodes_every_slot()
    {
        var x = Vector("wb-fieldboss-list").GetProperty("expect");
        var (events, _) = Decode("wb-fieldboss-list");
        var list = Assert.IsType<FieldBossListEvent>(Assert.Single(events));
        Assert.Equal(U(x, "map"), list.MapId);
        var expected = x.GetProperty("slots").EnumerateArray().ToList();
        Assert.Equal(expected.Count, list.Slots.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            var s = expected[i];
            Assert.Equal(U(s, "slot"), list.Slots[i].Slot);
            Assert.Equal(s.GetProperty("alive").GetBoolean(), list.Slots[i].Alive);
            Assert.Equal(s.GetProperty("time").GetInt64(), list.Slots[i].TimeUnixMs);
            Assert.Equal(s.GetProperty("x").GetSingle(), list.Slots[i].X);
            Assert.Equal(s.GetProperty("y").GetSingle(), list.Slots[i].Y);
            Assert.Equal(s.GetProperty("z").GetSingle(), list.Slots[i].Z);
        }
        // Slot 111011 (place 11 of map 1110) is the living Linx, ~1,550 units from the boss's cast position.
        var linx = list.Slots.Single(s => s.Slot == 111011);
        Assert.True(linx.Alive);
        var cast = Vector("wb-cast-kind2-position").GetProperty("expect");
        double dx = linx.X - cast.GetProperty("x").GetSingle(), dy = linx.Y - cast.GetProperty("y").GetSingle();
        Assert.InRange(Math.Sqrt(dx * dx + dy * dy), 0, 2500);
    }

    [Fact]
    public void Truncated_or_misaligned_field_boss_lists_are_rejected()
    {
        string body = Vector("wb-fieldboss-list").GetProperty("body").GetString()!;
        var (events, diag) = Run.Body(Opcodes.FieldBossList, body[..40]);
        Assert.Empty(events);
        Assert.Equal(1, diag.DecodeErrors);
        (events, diag) = Run.Body(Opcodes.FieldBossList, "000056040000");
        Assert.Empty(events);
        Assert.Equal(1, diag.DecodeErrors);
        // A slot id outside the map's range (map × 100 + 1..99) is not a slot.
        (events, diag) = Run.Body(Opcodes.FieldBossList, "00005604000001" + "00" + "01" + "0000000000000000" + "000000");
        Assert.Empty(events);
        Assert.Equal(1, diag.DecodeErrors);
    }

    [Fact]
    public void Boss_hp_reading_and_party_hp_update_decode()
    {
        var (events, _) = Decode("wb-stats-kind0");
        var st = Assert.IsType<EntityStatsEvent>(Assert.Single(events));
        Assert.Equal(22653u, st.Entity);
        Assert.Equal(45_634_088, st.CurrentHp);
        (events, _) = Decode("wb-hp-peer");
        var hp = Assert.IsType<HpUpdateEvent>(Assert.Single(events));
        var x = Vector("wb-hp-peer").GetProperty("expect");
        Assert.Equal(U(x, "entity"), hp.Entity);
        Assert.Equal(U(x, "hp_max"), hp.HpMax);
    }
}
