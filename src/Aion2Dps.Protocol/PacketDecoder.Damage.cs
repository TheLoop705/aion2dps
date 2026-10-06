using System.Buffers.Binary;
using Aion2Dps.Contracts;

namespace Aion2Dps.Protocol;

public sealed partial class PacketDecoder
{
    private const int MaxChainedRecords = 64;

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

            Emit(ev!);
            _diag.IncrementDamageRecords();
            records++;
            if (r.IsAtEnd || !ChainDamageRecords || records >= MaxChainedRecords) break;
        }

        if (!r.IsAtEnd) _diag.IncrementDamageTrailingBytes();
        return true;
    }

    /// <summary>Parses one damage record (§8.2.1). In strict mode every validator of §8.2.3 must pass.</summary>
    private bool TryParseDamageRecord(ref SpanReader r, bool strict, out DamageEvent? ev, out string? why)
    {
        ev = null;
        why = null;
        if (!r.TryReadVarUInt(out uint target)) { why = "truncated target"; return false; }
        if (!r.TryReadVarUInt(out uint sw)) { why = "truncated switch"; return false; }
        byte layout = (byte)(sw & 0x0F);
        if (layout is not (0 or 4 or 5 or 6 or 7)) { why = $"unknown layout {layout} (sw 0x{sw:X2})"; return false; }
        if (strict && (sw & ~0x3Fu) != 0) { why = "unknown switch bits"; return false; }
        if (!r.TryReadVarUInt(out _)) { why = "truncated flag"; return false; }
        if (!r.TryReadVarUInt(out uint actor)) { why = "truncated actor"; return false; }
        if (!r.TryReadU32(out uint skillRaw)) { why = "truncated skill"; return false; }
        if (!r.TryReadU8(out byte hitTag) || !r.TryReadU8(out byte dmgType)) { why = "truncated hit tag/type"; return false; }

        byte mods = 0, dir = 0;
        if ((layout & 0x02) != 0)
        {
            if (!r.TryReadU8(out mods) || !r.TrySkip(1) || !r.TryReadU8(out dir)) { why = "truncated mods/dir"; return false; }
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

            if (a > AmountCap) { why = $"amount {a} above cap"; return false; }
        }

        // Trailer: hit_index (low byte), 00 (§8.2 #15). Only consumed when it matches.
        bool trailer = false;
        var rest = r.RemainingSpan;
        if (rest.Length >= 2 && rest[0] == (byte)hitIndex && rest[1] == 0)
        {
            r.TrySkip(2);
            trailer = true;
        }

        uint skillId = SkillIds.Normalize(skillRaw);
        bool effectOk = SkillIds.EffectMatchesSkill(effect, skillRaw) || SkillIds.EffectMatchesSkill(effect, skillId);

        if (strict)
        {
            if (!effectOk) { why = "chained record: effect check failed"; return false; }
            if (scalar != 0 && scalar is < 1000 or > 200_000) { why = "chained record: power scalar out of range"; return false; }
            if (!trailer && !r.IsAtEnd) { why = "chained record: no trailer"; return false; }
            if (target is 0 or > MaxEntityId || actor is 0 or > MaxEntityId) { why = "chained record: entity out of range"; return false; }
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
            EffectValidated = effectOk,
        };
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
