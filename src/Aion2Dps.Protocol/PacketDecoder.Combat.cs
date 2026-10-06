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

    /// <summary><c>05 38</c> DoT/HoT tick (§8.3). The frame must end exactly after the last flagged field.</summary>
    private bool DecodeDot(ReadOnlySpan<byte> body)
    {
        var r = new SpanReader(body);
        if (!r.TryReadVarUInt(out uint target)) return Fail("truncated target");
        if (!r.TryReadU8(out byte flags)) return Fail("truncated flags");
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

        if ((flags & 0x08) != 0)
        {
            if (!r.TryReadU32(out uint s)) return Fail("truncated skill");
            skill = SkillIds.Normalize(s);
        }

        if (!r.IsAtEnd) return Fail($"{r.Remaining} unexpected trailing bytes (flags 0x{flags:X2})");

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

    /// <summary>
    /// <c>2A 38</c>/<c>2B 38</c> buff applied, Global layout (§8.13): target, 01, 13, stack, buff u32, duration u32,
    /// reserved zeros, expiry u64 (server Unix ms), caster, 0C, source skill u32. The real bundle frames carry 4 reserved
    /// zero bytes (the §8.13 table lists u32 + u8 = 5); the expiry is located as a plausible Unix-ms value after 4
    /// (preferred, real frames), 5 or 6 bytes.
    /// </summary>
    private bool DecodeBuffApplied(ReadOnlySpan<byte> body)
    {
        var r = new SpanReader(body);
        if (!r.TryReadVarUInt(out uint target)) return Fail("truncated target");
        if (!r.TrySkip(2)) return Fail("truncated header");
        if (!r.TryReadVarUInt(out uint stack)) return Fail("truncated stack");
        if (!r.TryReadU32(out uint buff) || !r.TryReadU32(out uint duration)) return Fail("truncated buff/duration");
        int reserved = 4;
        var rest = r.RemainingSpan;
        if (!IsPlausibleUnixMs(rest, 4))
        {
            if (IsPlausibleUnixMs(rest, 5)) reserved = 5;
            else if (IsPlausibleUnixMs(rest, 6)) reserved = 6;
        }

        if (!r.TrySkip(reserved)) return Fail("truncated reserved");
        if (!r.TryReadU64(out ulong expiry)) return Fail("truncated expiry");
        if (!r.TryReadVarUInt(out uint caster)) return Fail("truncated caster");
        uint source = 0;
        if (r.TrySkip(1) && r.TryReadU32(out uint s)) source = s;

        Emit(new BuffAppliedEvent
        {
            Time = _time, BundleDepth = _depth, Target = target, Stack = stack, BuffId = buff, DurationMs = duration,
            ExpiryUnixMs = expiry, Caster = caster, SourceSkill = source,
        });
        return true;
    }

    /// <summary>A u64 at <paramref name="offset"/> that reads as a Unix-ms time between 2020 and 2100.</summary>
    private static bool IsPlausibleUnixMs(ReadOnlySpan<byte> s, int offset)
    {
        if (s.Length < offset + 8) return false;
        ulong v = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(s[offset..]);
        return v is >= 1_577_836_800_000UL and <= 4_102_444_800_000UL;
    }

    /// <summary><c>0E 92</c> buff removed/refreshed (L): target, u8, buff u32.</summary>
    private bool DecodeBuffRemoved(ReadOnlySpan<byte> body)
    {
        var r = new SpanReader(body);
        if (!r.TryReadVarUInt(out uint target) || !r.TrySkip(1) || !r.TryReadU32(out uint buff)) return Fail("truncated");
        Emit(new BuffRemovedEvent { Time = _time, BundleDepth = _depth, Target = target, BuffId = buff });
        return true;
    }

    /// <summary><c>02 38</c> cast announcement (§8.14): actor, flag, skill u32, seq u8, kind u8, target.</summary>
    private bool DecodeCast(ReadOnlySpan<byte> body)
    {
        var r = new SpanReader(body);
        if (!r.TryReadVarUInt(out uint actor) || !r.TryReadVarUInt(out _) || !r.TryReadU32(out uint skill)
            || !r.TrySkip(2) || !r.TryReadVarUInt(out uint target))
            return Fail("truncated");
        Emit(new CastEvent { Time = _time, BundleDepth = _depth, Actor = actor, SkillRaw = skill, Target = target });
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
}
