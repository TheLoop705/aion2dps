using System.Buffers.Binary;
using Aion2Dps.Contracts;

namespace Aion2Dps.Protocol;

// Combat-adjacent records: heartbeat, DoT, entity stats, HP, death, kill, buffs, casts, toggles.
public sealed partial class PacketDecoder
{
    /// <summary><c>00 36</c> (§8.1): an 11-byte frame (8-byte body) is a heartbeat; larger ones are containers (L).</summary>
    private bool DecodeHeartbeat(ReadOnlySpan<byte> body)
    {
        if (body.Length != 8) return true; // possible container: recognised, nothing to emit
        var r = new SpanReader(body);
        r.TryReadU64(out ulong ms);
        Emit(new HeartbeatEvent { Time = _time, BundleDepth = _depth, ServerUnixMs = ms });
        return true;
    }

    /// <summary>
    /// <c>05 38</c> DoT/HoT tick (§8.3): target, flags u8, actor, stack, effect u32, then per flag bit in this order:
    /// 0x02 amount varint, 0x01 heal varint, 0x04 absorb block (count varint + count × u32 effect), 0x08 skill u32,
    /// 0x10 source entity varint, 0x20 source skill u32, 0x40 effect skill u32. The latter matches effect/100 and
    /// overrides the 0x08 trigger skill (observed on Lakshmi's retaliation ticks). Exact end is required.
    /// Flags 0x30 without amount/heal are trigger notices (a buff of the target, e.g. the dodge window, reacted to the
    /// source's skill): decoded and counted, but not emitted (a <see cref="DotEvent"/> cannot carry the source).
    /// </summary>
    private bool DecodeDot(ReadOnlySpan<byte> body)
    {
        var r = new SpanReader(body);
        if (!r.TryReadVarUInt(out uint target)) return Fail("truncated target");
        if (!r.TryReadU8(out byte flags)) return Fail("truncated flags");
        if ((flags & 0x80) != 0) return Fail($"unknown flags 0x{flags:X2}");
        if (!r.TryReadVarUInt(out uint actor)) return Fail("truncated actor");
        if (!r.TryReadVarUInt(out uint stack)) return Fail("truncated stack");
        if (!r.TryReadU32(out uint effect)) return Fail("truncated effect");
        long? amount = null, heal = null;
        uint? skill = null;
        if ((flags & 0x02) != 0)
        {
            if (!r.TryReadVarUInt(out uint a)) return Fail("truncated amount");
            amount = a;
        }

        if ((flags & 0x01) != 0)
        {
            if (!r.TryReadVarUInt(out uint h)) return Fail("truncated heal");
            heal = h;
        }

        if ((flags & 0x04) != 0 && !TrySkipAbsorbBlock(ref r)) return Fail("bad absorb block");

        if ((flags & 0x08) != 0)
        {
            if (!r.TryReadU32(out uint s)) return Fail("truncated skill");
            skill = SkillIds.Normalize(s);
        }

        if ((flags & 0x10) != 0 && !r.TryReadVarUInt(out _)) return Fail("truncated source entity");
        if ((flags & 0x20) != 0 && !r.TryReadU32(out _)) return Fail("truncated source skill");
        if ((flags & 0x40) != 0)
        {
            if (!r.TryReadU32(out uint s)) return Fail("truncated effect skill");
            uint effectSkill = SkillIds.Normalize(s);
            if (effect == 0 || effectSkill != SkillIds.Normalize(effect / 100)) return Fail("effect skill mismatch");
            skill = effectSkill;
        }

        if (!r.IsAtEnd) return Fail($"{r.Remaining} unexpected trailing bytes (flags 0x{flags:X2})");

        if ((flags & 0x30) != 0 && (flags & 0x03) == 0)
        {
            _diag.IncrementDotTriggerTicks();
            return true;
        }

        Emit(new DotEvent
        {
            Time = _time, BundleDepth = _depth, Target = target, Flags = flags, Actor = actor, Stack = stack,
            EffectId = effect, Amount = amount, Heal = heal, SkillId = skill,
        });
        return true;
    }

    /// <summary><c>00 8D</c> entity stats delta (§8.4): format bits 0/1 select a 4-byte and an 8-byte group; exact end.</summary>
    private bool DecodeEntityStats(ReadOnlySpan<byte> body)
    {
        var r = new SpanReader(body);
        if (!r.TryReadVarUInt(out uint entity)) return Fail("truncated entity");
        if (!r.TryReadU8(out byte format)) return Fail("truncated format");
        if ((format & ~0x03) != 0) return Fail($"unknown format 0x{format:X2}");

        Dictionary<byte, uint>? s32 = null;
        Dictionary<byte, long>? s64 = null;
        long? hp = null;
        if ((format & 0x01) != 0)
        {
            if (!r.TryReadU8(out byte n)) return Fail("truncated 4-byte group count");
            s32 = new Dictionary<byte, uint>(n);
            for (int i = 0; i < n; i++)
            {
                if (!r.TryReadU8(out byte kind) || !r.TryReadU32(out uint v)) return Fail("truncated 4-byte stat");
                s32[kind] = v;
            }
        }

        if ((format & 0x02) != 0)
        {
            if (!r.TryReadU8(out byte n)) return Fail("truncated 8-byte group count");
            s64 = new Dictionary<byte, long>(n);
            for (int i = 0; i < n; i++)
            {
                if (!r.TryReadU8(out byte kind) || !r.TryReadI64(out long v)) return Fail("truncated 8-byte stat");
                s64[kind] = v;
                if (kind == 0) hp = v;
            }
        }

        if (!r.IsAtEnd) return Fail($"{r.Remaining} unexpected trailing bytes");

        Emit(new EntityStatsEvent
        {
            Time = _time, BundleDepth = _depth, Entity = entity, Format = format, CurrentHp = hp,
            Stats32 = (IReadOnlyDictionary<byte, uint>?)s32 ?? EmptyStats32,
            Stats64 = (IReadOnlyDictionary<byte, long>?)s64 ?? EmptyStats64,
        });
        return true;
    }

    private static readonly IReadOnlyDictionary<byte, uint> EmptyStats32 = new Dictionary<byte, uint>();
    private static readonly IReadOnlyDictionary<byte, long> EmptyStats64 = new Dictionary<byte, long>();

    /// <summary><c>1B 92</c> HP/MP (§8.12): entity, hp, hp_max varints.</summary>
    private bool DecodeHpUpdate(ReadOnlySpan<byte> body)
    {
        var r = new SpanReader(body);
        if (!r.TryReadVarUInt(out uint entity) || !r.TryReadVarUInt(out uint hp) || !r.TryReadVarUInt(out uint max))
            return Fail("truncated");
        Emit(new HpUpdateEvent { Time = _time, BundleDepth = _depth, Entity = entity, Hp = hp, HpMax = max });
        return true;
    }

    /// <summary><c>42 36</c> death (§8.9): entity, varint (0), flag.</summary>
    private bool DecodeDeath(ReadOnlySpan<byte> body)
    {
        var r = new SpanReader(body);
        if (!r.TryReadVarUInt(out uint entity) || !r.TryReadVarUInt(out _) || !r.TryReadVarUInt(out uint flag))
            return Fail("truncated");
        Emit(new DeathEvent { Time = _time, BundleDepth = _depth, Entity = entity, Flag = flag });
        return true;
    }

    /// <summary><c>04 8D</c> kill / last blow (§8.8): target, skill u32, killer, server u16, name, legion name.
    /// The despawn form (skill 0, killer 0) may stop early.</summary>
    private bool DecodeKill(ReadOnlySpan<byte> body)
    {
        var r = new SpanReader(body);
        if (!r.TryReadVarUInt(out uint target)) return Fail("truncated target");
        if (!r.TryReadU32(out uint skill)) return Fail("truncated skill");
        if (!r.TryReadVarUInt(out uint killer)) return Fail("truncated killer");
        ushort server = 0;
        string? name = null, guild = null;
        if (r.TryReadU16(out ushort sv))
        {
            server = sv;
            if (r.TryReadString(out string n) && n.Length > 0)
            {
                name = n;
                if (r.TryReadString(out string g) && g.Length > 0) guild = g;
            }
        }

        Emit(new KillEvent
        {
            Time = _time, BundleDepth = _depth, Target = target, SkillRaw = skill, Killer = killer,
            KillerServerId = server, KillerName = name, KillerGuildName = guild,
        });
        return true;
    }

    /// <summary>Largest accepted entry count of a <c>2A 38</c> frame (1 in every real sample).</summary>
    private const int MaxBuffEntries = 16;

    /// <summary>
    /// <c>2A 38</c> / <c>2B 38</c> buff applied (§8.13), verified on 4,866 real Global frames (all of them end exactly):
    /// <c>2A 38</c> = target varint, count u8 (1 in every sample), count × entry; <c>2B 38</c> = target varint, one entry.
    /// entry = flags u8 (0x11/0x13; bit 0x02 = source skill present), stack varint, buff u32, duration u64 ms
    /// (all FF = permanent), expiry u64 server Unix ms, caster varint, level u8, [source skill u32], u8, 3 × f32.
    /// </summary>
    private bool DecodeBuffApplied(ReadOnlySpan<byte> body)
    {
        var r = new SpanReader(body);
        if (!r.TryReadVarUInt(out uint target)) return Fail("truncated target");
        int count = 1;
        if (_opcode != _ops.BuffApplied2)
        {
            if (!r.TryReadU8(out byte n)) return Fail("truncated count");
            if (n is < 1 or > MaxBuffEntries) return Fail($"buff count {n} out of range");
            count = n;
        }

        var found = new BuffAppliedEvent[count];
        for (int k = 0; k < count; k++)
        {
            if (!r.TryReadU8(out byte flags)) return Fail("truncated flags");
            if (!r.TryReadVarUInt(out uint stack)) return Fail("truncated stack");
            if (!r.TryReadU32(out uint buff) || !r.TryReadU64(out ulong duration)) return Fail("truncated buff/duration");
            if (!r.TryReadU64(out ulong expiry)) return Fail("truncated expiry");
            if (!r.TryReadVarUInt(out uint caster)) return Fail("truncated caster");
            if (!r.TrySkip(1)) return Fail("truncated level");
            uint source = 0;
            if ((flags & 0x02) != 0 && !r.TryReadU32(out source)) return Fail("truncated source skill");
            if (!r.TrySkip(1 + 12)) return Fail("truncated position");
            found[k] = new BuffAppliedEvent
            {
                Time = _time, BundleDepth = _depth, Target = target, Stack = stack, BuffId = buff,
                DurationMs = duration >= uint.MaxValue ? uint.MaxValue : (uint)duration,
                ExpiryUnixMs = expiry, Caster = caster, SourceSkill = source,
            };
        }

        if (!r.IsAtEnd) return Fail($"{r.Remaining} unexpected trailing bytes");
        foreach (var e in found) Emit(e);
        return true;
    }

    /// <summary><c>0E 92</c> buff removed (§8.13): target varint, count u8 (0 or 1 in real frames), count × buff u32;
    /// exact end. One event per buff id; count 0 emits nothing.</summary>
    private bool DecodeBuffRemoved(ReadOnlySpan<byte> body)
    {
        var r = new SpanReader(body);
        if (!r.TryReadVarUInt(out uint target) || !r.TryReadU8(out byte n)) return Fail("truncated");
        if (r.Remaining != n * 4) return Fail($"count {n} does not match {r.Remaining} remaining bytes");
        for (int k = 0; k < n; k++)
        {
            r.TryReadU32(out uint buff);
            Emit(new BuffRemovedEvent { Time = _time, BundleDepth = _depth, Target = target, BuffId = buff });
        }

        return true;
    }

    /// <summary><c>02 38</c> cast announcement (§8.14): actor, flag, skill u32, seq u8, kind u8, target.</summary>
    private bool DecodeCast(ReadOnlySpan<byte> body)
    {
        var r = new SpanReader(body);
        if (!r.TryReadVarUInt(out uint actor) || !r.TryReadVarUInt(out _) || !r.TryReadU32(out uint skill)
            || !r.TryReadU8(out _) || !r.TryReadU8(out byte kind) || !r.TryReadVarUInt(out uint target))
            return Fail("truncated");
        // [real] kind 2 (21,000+ frames of the world-boss capture): heading f32, then x, y, z f32 of the caster.
        float? x = null, y = null, z = null;
        if (kind == 2 && r.TrySkip(4) && r.TryReadF32(out float px) && r.TryReadF32(out float py) && r.TryReadF32(out float pz)
            && float.IsFinite(px) && float.IsFinite(py) && float.IsFinite(pz)
            && Math.Abs(px) < 10_000_000 && Math.Abs(py) < 10_000_000 && Math.Abs(pz) < 10_000_000)
        {
            (x, y, z) = (px, py, pz);
        }
        Emit(new CastEvent { Time = _time, BundleDepth = _depth, Actor = actor, SkillRaw = skill, Target = target, X = x, Y = y, Z = z });
        return true;
    }

    /// <summary><c>03 36</c> ping echo (§8.15): <c>00 00</c> + i64 client ms.</summary>
    private bool DecodePing(ReadOnlySpan<byte> body)
    {
        var r = new SpanReader(body);
        if (!r.TrySkip(2) || !r.TryReadI64(out long ms)) return Fail("truncated");
        Emit(new PingEvent { Time = _time, BundleDepth = _depth, ClientSentMs = ms });
        return true;
    }

    /// <summary><c>20 36</c> session ↔ global id (§8.16, L): skip 2, session varint, skip 4, global u32.</summary>
    private bool DecodeGlobalIdLink(ReadOnlySpan<byte> body)
    {
        var r = new SpanReader(body);
        if (!r.TrySkip(2) || !r.TryReadVarUInt(out uint entity) || !r.TrySkip(4) || !r.TryReadU32(out uint global))
            return Fail("truncated");
        Emit(new GlobalIdLinkEvent { Time = _time, BundleDepth = _depth, Entity = entity, CharacterId = global });
        return true;
    }

    /// <summary><c>21 8D</c> battle toggle (§8.16, L): mob, varint, toggle (1/0); ends exactly.</summary>
    private bool DecodeBattleToggle(ReadOnlySpan<byte> body)
    {
        var r = new SpanReader(body);
        if (!r.TryReadVarUInt(out uint entity) || !r.TryReadVarUInt(out _) || !r.TryReadVarUInt(out uint toggle))
            return Fail("truncated");
        if (!r.IsAtEnd) return Fail("unexpected trailing bytes");
        if (toggle > 1) return Fail($"toggle value {toggle}");
        Emit(new BattleToggleEvent { Time = _time, BundleDepth = _depth, Entity = entity, Engaged = toggle == 1 });
        return true;
    }

    /// <summary><c>21 36</c> map load (§8.10): u32 load count, u32 map id.</summary>
    private bool DecodeMapLoad(ReadOnlySpan<byte> body)
    {
        var r = new SpanReader(body);
        if (!r.TryReadU32(out uint count) || !r.TryReadU32(out uint map)) return Fail("truncated");
        Emit(new MapLoadEvent { Time = _time, BundleDepth = _depth, LoadCount = count, MapId = map });
        return true;
    }

    /// <summary><c>23 36</c> teleport (§8.10): entity varint (0 = local player).</summary>
    private bool DecodeTeleport(ReadOnlySpan<byte> body)
    {
        var r = new SpanReader(body);
        if (!r.TryReadVarUInt(out uint entity)) return Fail("truncated");
        Emit(new TeleportEvent { Time = _time, BundleDepth = _depth, Entity = entity });
        return true;
    }

    /// <summary>Largest accepted field-boss slot count (real lists carry 24).</summary>
    private const int MaxFieldBossSlots = 99;

    /// <summary>
    /// <c>01 91</c> field-boss list (§8.16, [real] 143 + 124 frames decode to their exact end): <c>u16 0 | map u32 |
    /// count u8 | count × (alive u8, slot varint, [x y z f32 if alive], [u8 on some slots], time i64) | 00 00 00</c>.
    /// The optional byte has no flag: of the two readings of a slot only one leaves the next slot header (or the zero
    /// tail) where it must be.
    /// </summary>
    private bool DecodeFieldBossList(ReadOnlySpan<byte> body)
    {
        if (body.Length < 7) return Fail("truncated");
        uint map = BinaryPrimitives.ReadUInt32LittleEndian(body[2..]);
        int count = body[6];
        if (map == 0 || count > MaxFieldBossSlots) return Fail("bad header");
        var slots = new List<FieldBossSlot>(count);
        int o = 7;
        for (int n = 0; n < count; n++)
        {
            if (!TryFieldBossSlot(body, ref o, map, last: n == count - 1, out var slot)) return Fail($"slot {n} not recognised");
            slots.Add(slot);
        }
        Emit(new FieldBossListEvent { Time = _time, BundleDepth = _depth, MapId = map, Slots = slots });
        return true;
    }

    private static bool TryFieldBossSlot(ReadOnlySpan<byte> b, ref int o, uint map, bool last, out FieldBossSlot slot)
    {
        slot = null!;
        if (!FieldBossSlotHeader(b, o, map, out bool alive, out uint id, out int at)) return false;
        float x = 0, y = 0, z = 0;
        if (alive)
        {
            if (at + 12 > b.Length) return false;
            x = BinaryPrimitives.ReadSingleLittleEndian(b[at..]);
            y = BinaryPrimitives.ReadSingleLittleEndian(b[(at + 4)..]);
            z = BinaryPrimitives.ReadSingleLittleEndian(b[(at + 8)..]);
            at += 12;
        }
        for (int extra = 0; extra <= 1; extra++)
        {
            int t = at + extra;
            if (t + 8 > b.Length) break;
            long time = BinaryPrimitives.ReadInt64LittleEndian(b[t..]);
            if (time != 0 && time is < 1_600_000_000_000 or > 2_600_000_000_000) continue;
            int end = t + 8;
            bool fits = last
                ? b.Length - end <= 8 && b[end..].IndexOfAnyExcept((byte)0) < 0
                : FieldBossSlotHeader(b, end, map, out _, out _, out _);
            if (!fits) continue;
            slot = new FieldBossSlot { Slot = id, Alive = alive, TimeUnixMs = time, X = x, Y = y, Z = z };
            o = end;
            return true;
        }
        return false;
    }

    /// <summary><c>alive u8 (0/1) | slot varint</c> with the slot inside the map's range (map × 100 + 1..99).</summary>
    private static bool FieldBossSlotHeader(ReadOnlySpan<byte> b, int o, uint map, out bool alive, out uint slot, out int next)
    {
        alive = false;
        slot = 0;
        next = o;
        if (o >= b.Length || b[o] > 1) return false;
        alive = b[o] == 1;
        if (VarInt.TryRead(b[(o + 1)..], out uint v, out int width) != VarIntStatus.Ok) return false;
        ulong lo = (ulong)map * 100;
        if (v <= lo || v >= lo + 100) return false;
        slot = v;
        next = o + 1 + width;
        return true;
    }
}
