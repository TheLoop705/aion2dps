using System.Buffers.Binary;
using Aion2Dps.Contracts;

namespace Aion2Dps.Protocol;

public sealed partial class PacketDecoder
{
    private const int MaxChainedRecords = 64;

    /// <summary>Largest accepted count of an absorb block (§8.2 #14b, real frames carry 1).</summary>
    private const int MaxAbsorbEntries = 8;

    /// <summary>
    /// <c>04 38</c> damage / heal / cast notice (§8.2). The first record is decoded leniently (flagged by
    /// <see cref="DamageEvent.EffectValidated"/>); further chained records must pass every §8.2.3 validator.
    /// </summary>
    private bool DecodeDamage(ReadOnlySpan<byte> body)
    {
        var r = new SpanReader(body);
        int records = 0;
        while (true)
        {
            int start = r.Position;
            bool strict = records > 0;
            if (!TryParseDamageRecord(ref r, strict, out var ev, out string? why))
            {
                if (records == 0) return Fail(why ?? "bad damage record");
                r.Position = start;
                break;
            }

            if (ev is not null)
            {
                Emit(ev);
                _diag.IncrementDamageRecords();
            }

            records++;
            if (r.IsAtEnd || !ChainDamageRecords || records >= MaxChainedRecords) break;
        }

        if (!r.IsAtEnd) _diag.IncrementDamageTrailingBytes();
        return true;
    }

    /// <summary>
    /// Parses one damage record (§8.2.1). In strict mode every validator of §8.2.3 must pass. Returns true with
    /// <paramref name="ev"/> = null for a well-formed record that is deliberately not emitted (the 500,000,000
    /// self-targeted "restore to full HP" placeholder sent when an instanced NPC's max HP is scaled, LIVE-FINDINGS).
    /// </summary>
    private bool TryParseDamageRecord(ref SpanReader r, bool strict, out DamageEvent? ev, out string? why, bool probe = false)
    {
        ev = null;
        why = null;
        if (!r.TryReadVarUInt(out uint target)) { why = "truncated target"; return false; }
        if (!r.TryReadVarUInt(out uint sw)) { why = "truncated switch"; return false; }
        byte layout = (byte)(sw & 0x0F);
        // Layout 2 (mods/dir block, no amount) is real on Global: an evaded NPC hit (dmg_type 1).
        if (layout is not (0 or 2 or 4 or 5 or 6 or 7)) { why = $"unknown layout {layout} (sw 0x{sw:X2})"; return false; }
        if (strict && (sw & ~0x7Fu) != 0) { why = "unknown switch bits"; return false; }
        if (!r.TryReadVarUInt(out uint flag)) { why = "truncated flag"; return false; }
        if (!r.TryReadVarUInt(out uint actor)) { why = "truncated actor"; return false; }
        // The first record can be lenient about unverified effects, but impossible entity ids must never enter
        // combat state. Apply the same documented id range to the first record and chained records.
        if (target is 0 or > MaxEntityId || actor is 0 or > MaxEntityId) { why = "entity out of range"; return false; }
        if (!r.TryReadU32(out uint skillRaw)) { why = "truncated skill"; return false; }
        if (!r.TryReadU8(out byte hitTag) || !r.TryReadU8(out byte dmgType)) { why = "truncated hit tag/type"; return false; }

        byte mods = 0, dir = 0;
        if ((layout & 0x02) != 0)
        {
            // mods u8, a varint (0 except with mods 0x20, where it carries a value), dir u8.
            if (!r.TryReadU8(out mods) || !r.TryReadVarUInt(out _) || !r.TryReadU8(out dir)) { why = "truncated mods/dir"; return false; }
        }

        if ((layout & 0x01) != 0)
        {
            // Layouts 5/7 are unverified on Global (§8.2, §18.2): locate the effect id by the validator.
            int skip = LocateEffect(r.RemainingSpan, skillRaw);
            if (skip < 0) { why = $"layout {layout}: effect id not found"; return false; }
            r.TrySkip(skip);
        }

        if (!r.TryReadU32(out uint effect) || !r.TryReadU32(out uint hitIndex)) { why = "truncated effect/hit index"; return false; }
        if (!r.TryReadVarUInt(out uint scalar)) { why = "truncated power scalar"; return false; }

        long? amount = null;
        uint[]? extras = null;
        bool placeholder = false;
        if ((layout & 0x04) != 0)
        {
            if (!r.TryReadVarUInt(out uint a)) { why = "truncated amount"; return false; }
            amount = a;
            if (layout == 4 && (sw & 0x10) != 0 && !r.TryReadVarUInt(out _)) { why = "truncated layout-4 field"; return false; }
            if ((sw & 0x20) != 0)
            {
                if (!r.TryReadVarUInt(out uint n)) { why = "truncated extra-hit count"; return false; }
                if (n is < 1 or > 25) { why = $"extra-hit count {n} out of range"; return false; }
                extras = new uint[n];
                ulong sum = 0;
                for (int i = 0; i < n; i++)
                {
                    if (!r.TryReadVarUInt(out extras[i])) { why = "truncated extra hits"; return false; }
                    sum += extras[i];
                }

                if (sum >= a) { why = $"extra hits {sum} not below amount {a}"; return false; }
            }

            if (a > AmountCap)
            {
                // Real instanced NPCs send actor == target, skills 1000030..1000034, amount 500,000,000 right after a
                // max-HP increase (00 8D kind 7): the HP is set to the new max. Not damage, not a heal worth showing.
                if (actor != target) { why = $"amount {a} above cap"; return false; }
                placeholder = true;
            }
        }

        // Absorb/negation block: sw 0x40 (on damage records) or flag 0x01 (on dmg_type 6 notices): a count varint and
        // that many u32 effect ids of the target's buff that absorbed or negated the hit.
        uint[]? absorbEffects = null;
        if ((sw & 0x40) != 0 || (flag & 0x01) != 0)
        {
            bool absorbOk = probe ? TrySkipAbsorbBlock(ref r) : TryReadAbsorbBlock(ref r, out absorbEffects);
            if (!absorbOk) { why = "bad absorb block"; return false; }
        }

        // Trailer: u8 sequence, 00 (§8.2 #15). The sequence equals hit_index for single-target player hits; NPC area
        // hits carry a per-cast target counter instead. With flag 0x04 a varint follows the trailer (Assassin and
        // Gladiator hits; it grows with the amount, meaning unknown). Consumed when it matches hit_index or when the
        // trailer (plus that varint) ends the frame exactly, or a strictly validated next record follows it.
        // Lookahead reads only the next record's fields: it does not emit events, count placeholders or recurse.
        bool trailer = false;
        var rest = r.RemainingSpan;
        if (!probe && rest.Length >= 2 && rest[1] == 0 &&
            (rest[0] == (byte)hitIndex || EndsAfterTrailer(rest[2..], flag) || ValidRecordAfterTrailer(rest[2..], flag)))
        {
            r.TrySkip(2);
            trailer = true;
            if ((flag & 0x04) != 0 && !r.TryReadVarUInt(out _)) { why = "truncated flag-4 tail"; return false; }
        }

        uint skillId = SkillIds.Normalize(skillRaw);
        bool effectOk = SkillIds.EffectMatchesSkill(effect, skillRaw) || SkillIds.EffectMatchesSkill(effect, skillId);

        if (strict)
        {
            if (!effectOk) { why = "chained record: effect check failed"; return false; }
            if (scalar != 0 && scalar is < 1000 or > 200_000) { why = "chained record: power scalar out of range"; return false; }
            if (!probe && !trailer && !r.IsAtEnd) { why = "chained record: no trailer"; return false; }
        }

        if (probe) return true;

        if (placeholder)
        {
            _diag.IncrementDamagePlaceholders();
            return true;
        }

        ev = new DamageEvent
        {
            Time = _time,
            BundleDepth = _depth,
            Target = target,
            Actor = actor,
            SkillRaw = skillRaw,
            SkillId = skillId,
            Switch = sw,
            Layout = layout,
            HitTag = hitTag,
            DamageType = dmgType,
            Mods = (HitMods)mods,
            Direction = (HitDirection)dir,
            EffectId = effect,
            HitIndex = hitIndex,
            PowerScalar = scalar,
            Amount = amount,
            ExtraHits = extras ?? Array.Empty<uint>(),
            AbsorbEffects = absorbEffects ?? Array.Empty<uint>(),
            EffectValidated = effectOk,
        };
        return true;
    }

    /// <summary>True when the bytes after a trailer are exactly what the record still owns: nothing, or (flag 0x04)
    /// one varint.</summary>
    private static bool EndsAfterTrailer(ReadOnlySpan<byte> after, uint flag)
    {
        if ((flag & 0x04) == 0) return after.IsEmpty;
        return VarInt.TryRead(after, out _, out int width) == VarIntStatus.Ok && width == after.Length;
    }

    /// <summary>A differing area-hit trailer is accepted before another record only when its fields validate.</summary>
    private bool ValidRecordAfterTrailer(ReadOnlySpan<byte> after, uint flag)
    {
        if ((flag & 0x04) != 0)
        {
            if (VarInt.TryRead(after, out _, out int width) != VarIntStatus.Ok) return false;
            after = after[width..];
        }

        if (after.IsEmpty) return false;
        var candidate = new SpanReader(after);
        return TryParseDamageRecord(ref candidate, strict: true, out _, out _, probe: true);
    }

    /// <summary>Absorb block (<c>04 38</c> sw 0x40 / flag 0x01, <c>05 38</c> flag 0x04): count varint (1..8), then
    /// count × u32 effect id.</summary>
    private static bool TrySkipAbsorbBlock(ref SpanReader r)
    {
        if (!r.TryReadVarUInt(out uint n) || n is < 1 or > MaxAbsorbEntries) return false;
        return r.TrySkip((int)n * 4);
    }

    /// <summary>Preserves explicit buff effect ids for combat shield accounting; probe lookahead still skips them.</summary>
    private static bool TryReadAbsorbBlock(ref SpanReader r, out uint[] effects)
    {
        effects = Array.Empty<uint>();
        if (!r.TryReadVarUInt(out uint n) || n is < 1 or > MaxAbsorbEntries || r.Remaining < n * 4) return false;
        effects = new uint[n];
        for (int i = 0; i < effects.Length; i++) r.TryReadU32(out effects[i]);
        return true;
    }

    /// <summary>Offset (0..8) of a u32 that passes the effect check against <paramref name="skill"/>, preferring the
    /// inferred 4 unknown bytes; −1 if none.</summary>
    private static int LocateEffect(ReadOnlySpan<byte> s, uint skill)
    {
        if (EffectAt(s, 4, skill)) return 4;
        for (int k = 0; k <= 8; k++)
        {
            if (k != 4 && EffectAt(s, k, skill)) return k;
        }

        return -1;
    }

    private static bool EffectAt(ReadOnlySpan<byte> s, int k, uint skill)
    {
        if (s.Length < k + 8) return false;
        uint e = BinaryPrimitives.ReadUInt32LittleEndian(s[k..]);
        return SkillIds.EffectMatchesSkill(e, skill) || SkillIds.EffectMatchesSkill(e, SkillIds.Normalize(skill));
    }
}
