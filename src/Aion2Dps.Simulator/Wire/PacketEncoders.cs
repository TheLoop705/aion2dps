using Aion2Dps.Contracts;

namespace Aion2Dps.Simulator;

/// <summary>
/// One encoder per packet type, the inverse of the decoder in PROTOCOL.md §8. Every <c>EncodeX</c> takes the same
/// field values as the matching Contracts event and returns the <b>body</b> (the bytes after the 2 opcode bytes).
/// <see cref="EncodePayload"/> returns opcode + body and <see cref="EncodeFrame"/> the complete framed bytes.
/// Wire fields the events do not carry come from the optional <c>*EncodeOptions</c> (defaults = real observed values).
/// </summary>
public static class PacketEncoders
{
    /// <summary>Caster anchor marker inside <c>41 36</c> (§8.5 #2).</summary>
    public static ReadOnlySpan<byte> CasterAnchor => [0x80, 0x75, 0xD5, 0x2A, 0xBB, 0x03, 0x00, 0x00];

    /// <summary>Owner marker inside <c>41 36</c> (§8.5 #1), followed by the owner's entity id as u32 LE.</summary>
    public static ReadOnlySpan<byte> OwnerMarker => [0x07, 0x02, 0x06];

    /// <summary>A player effect id that passes the §8.2.3 validator: skill × 100 + <paramref name="suffix"/>.</summary>
    public static uint EffectFor(uint skillId, uint suffix = 11) => checked(skillId * 100 + suffix);

    /// <summary>Identity/roster class code for a class (§10.4): 4 × classId + faction (2 = Elyos).</summary>
    public static uint ClassCodeFor(CharacterClass cls, uint faction = 2) => cls == CharacterClass.Unknown ? 0 : (uint)cls * 4 + faction;

    // ───────────────────────────── 04 38 damage ─────────────────────────────

    /// <summary>Effective switch varint of a damage event: <see cref="DamageEvent.Switch"/> (or the layout when 0), with
    /// <c>0x20</c> forced on when extra hits are present.</summary>
    public static uint SwitchOf(DamageEvent e)
    {
        uint sw = e.Switch != 0 ? e.Switch : e.Layout;
        if (e.ExtraHits.Count > 0) sw |= 0x20;
        return sw;
    }

    /// <summary><c>04 38</c> record (§8.2). Layout = <c>sw &amp; 0x0F</c>; amount is written for layouts with bit 0x04.</summary>
    public static byte[] EncodeDamage(DamageEvent e, DamageEncodeOptions? options = null)
    {
        var w = new WireWriter(40);
        WriteDamageRecord(w, e, options ?? DamageEncodeOptions.Default);
        return w.ToArray();
    }

    /// <summary>Several chained <c>04 38</c> records in one body (§8.2 #16).</summary>
    public static byte[] EncodeDamageChain(IEnumerable<DamageEvent> records, DamageEncodeOptions? options = null)
    {
        var w = new WireWriter(80);
        foreach (var e in records) WriteDamageRecord(w, e, options ?? DamageEncodeOptions.Default);
        return w.ToArray();
    }

    private static void WriteDamageRecord(WireWriter w, DamageEvent e, DamageEncodeOptions o)
    {
        uint sw = SwitchOf(e);
        uint layout = sw & 0x0F;
        if (layout is not (0 or 4 or 5 or 6 or 7))
            throw new ArgumentException($"Layout {layout} is not a valid 04 38 layout.", nameof(e));

        w.Varint((ulong)e.Target).Varint((ulong)sw).Varint((ulong)o.Flag).Varint((ulong)e.Actor);
        w.U32(e.SkillRaw).U8(e.HitTag).U8(e.DamageType);
        if ((layout & 0x02) != 0) w.U8((byte)e.Mods).U8(0).U8((byte)e.Direction);
        if ((layout & 0x01) != 0) w.U32(o.Layout1Bytes);
        w.U32(e.EffectId).U32(e.HitIndex).Varint((ulong)e.PowerScalar);

        if ((layout & 0x04) != 0)
        {
            long amount = e.Amount ?? 0;
            w.Varint(amount);
            if (layout == 4 && (sw & 0x10) != 0) w.Varint((ulong)o.Layout4Extra);
            if ((sw & 0x20) != 0)
            {
                if (e.ExtraHits.Count is < 1 or > 25)
                    throw new ArgumentException("sw & 0x20 needs 1..25 extra hits.", nameof(e));
                long sum = 0;
                foreach (uint x in e.ExtraHits) sum += x;
                if (sum >= amount) throw new ArgumentException("The extra hits must sum to less than the amount.", nameof(e));
                w.Varint((ulong)e.ExtraHits.Count);
                foreach (uint x in e.ExtraHits) w.Varint((ulong)x);
            }
        }

        if (o.Trailer) w.U8((byte)e.HitIndex).U8(0);
    }

    // ───────────────────────────── 05 38 DoT / HoT ─────────────────────────────

    /// <summary><c>05 38</c> tick (§8.3): amount if flags &amp; 0x02, heal if &amp; 0x01, skill if &amp; 0x08.</summary>
    public static byte[] EncodeDot(DotEvent e)
    {
        var w = new WireWriter(24);
        w.Varint((ulong)e.Target).U8(e.Flags).Varint((ulong)e.Actor).Varint((ulong)e.Stack).U32(e.EffectId);
        if ((e.Flags & 0x02) != 0) w.Varint(e.Amount ?? 0);
        if ((e.Flags & 0x01) != 0) w.Varint(e.Heal ?? 0);
        if ((e.Flags & 0x08) != 0) w.U32(e.SkillId ?? 0);
        return w.ToArray();
    }

    // ───────────────────────────── 00 8D entity stats ─────────────────────────────

    /// <summary>Format byte of a stats event: the event's own <see cref="EntityStatsEvent.Format"/> or, when 0, derived
    /// from which groups are present.</summary>
    public static byte FormatOf(EntityStatsEvent e)
    {
        if (e.Format != 0) return e.Format;
        byte f = 0;
        if (e.Stats32.Count > 0) f |= 0x01;
        if (e.CurrentHp is not null || e.Stats64.Count > 0) f |= 0x02;
        return f;
    }

    /// <summary><c>00 8D</c> (§8.4). 8-byte kind 0 = <see cref="EntityStatsEvent.CurrentHp"/>; kinds are written ascending.</summary>
    public static byte[] EncodeEntityStats(EntityStatsEvent e)
    {
        byte format = FormatOf(e);
        var w = new WireWriter(24);
        w.Varint((ulong)e.Entity).U8(format);
        if ((format & 0x01) != 0)
        {
            var stats = e.Stats32.OrderBy(p => p.Key).ToList();
            w.U8((byte)stats.Count);
            foreach (var (kind, value) in stats) w.U8(kind).U32(value);
        }

        if ((format & 0x02) != 0)
        {
            var stats = new List<KeyValuePair<byte, long>>();
            if (e.CurrentHp is long hp) stats.Add(new(0, hp));
            stats.AddRange(e.Stats64.Where(p => !(p.Key == 0 && e.CurrentHp is not null)).OrderBy(p => p.Key));
            w.U8((byte)stats.Count);
            foreach (var (kind, value) in stats) w.U8(kind).I64(value);
        }

        return w.ToArray();
    }

    // ───────────────────────────── 41 36 spawn ─────────────────────────────

    /// <summary>
    /// <c>41 36</c> spawn (§8.5): structured head (entity, kind, kind_flags, gate, [caster name], npc_code, 00|40, 02,
    /// x y z heading, u16, 01, hp_cur, hp_max) and a tail modelled on the real frames: for summon-like spawns the first
    /// run of eight <c>FF</c>, the caster anchor + anchor varint, and <c>07 02 06</c> + owner u32; monsters carry the
    /// anchor (= themselves) and no owner block.
    /// </summary>
    public static byte[] EncodeSpawn(SpawnEvent e, SpawnEncodeOptions? options = null)
    {
        var o = options ?? SpawnEncodeOptions.Default;
        bool summon = e.IsSummonLike || e.OwnerId is not null;
        long hpCur = e.HpCurrent ?? e.HpMax ?? 0;
        long hpMax = e.HpMax ?? hpCur;

        var w = new WireWriter(160);
        w.Varint((ulong)e.Entity).U8(e.KindByte).U8(e.KindFlags).U8(e.CasterName is null ? (byte)0 : (byte)1);
        if (e.CasterName is not null) w.String(e.CasterName);
        w.U32(e.NpcCode).U8(o.CodeSuffix ?? (summon ? (byte)0x40 : (byte)0x00)).U8(0x02);
        w.F32(e.X).F32(e.Y).F32(e.Z).F32(o.Heading).U16(o.Unknown16).U8(0x01);
        w.Varint(hpCur).Varint(hpMax);
        if (!o.IncludeTail) return w.ToArray();

        uint cur32 = (uint)Math.Min(hpCur, uint.MaxValue);
        uint max32 = (uint)Math.Min(hpMax, uint.MaxValue);
        uint anchor = e.AnchorId ?? e.Entity;
        (float x, float y, float z) = (e.X, e.Y, e.Z);

        if (summon)
        {
            w.U32(cur32).U32(max32).Zeros(13).U32(0x00018B50);
            w.Bytes([0x64, 0, 0, 0, 0xF0, 0x49, 0x02, 0, 0x01, 0, 0, 0, 0, 0, 0, 0, 0xA0, 0x86, 0x01, 0, 0, 0, 0, 0]);
            w.Bytes([0x00, 0xE2, 0x04, 0x00, 0x01, 0x01, 0x01, 0x11, 0x01, 0x81, 0x96, 0x98, 0x00]);
            int ffStart = w.Length;
            w.Bytes([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]).Bytes(CasterAnchor).Varint((ulong)anchor).U8(0x01).U8(0x02);
            int beforeFloats = w.Length;
            // The owner search takes the first 07 02 06 after the FF run: keep the anchor/floats free of that pattern.
            (x, y, z) = SafeFloats(w.WrittenSpan[ffStart..beforeFloats].ToArray(), x, y, z);
            w.F32(x).F32(y).F32(z);
            w.Bytes(OwnerMarker).U32(e.OwnerId ?? 0);
            w.U8(0x02).U8(0xCD).U8(0x00).U32(0).U8(0xD0).U8(0x00).U32(0x131).U32(0x2D).U8(0x00);
        }
        else
        {
            w.U32(100).U32(100).Zeros(24).U32(1).Zeros(18);
            w.Bytes([0x06, 0x03, 0x11, 0x01, 0x81, 0x96, 0x98, 0x00]);
            int ffStart = w.Length;
            w.Bytes([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]).Bytes(CasterAnchor).Varint((ulong)anchor).U8(0x01).U8(0x28);
            byte[] prefix = w.WrittenSpan[ffStart..].ToArray();
            byte[] after = [0x01, 0x00, 0x2D, 0x00, 0x00, 0x00, 0x03, 0x01, 0xEE, 0x02, 0x00, 0x00, 0xEE, 0x02, 0x00, 0x00, 0x00];
            (x, y, z) = SafeFloats(prefix, x, y, z, after);
            w.F32(x).F32(y).F32(z).Bytes(after);
        }

        return w.ToArray();
    }

    /// <summary>Nudges the tail copy of the position until the bytes after the FF run contain no <c>07 02 06</c>.</summary>
    private static (float, float, float) SafeFloats(byte[] prefix, float x, float y, float z, byte[]? suffix = null)
    {
        for (int attempt = 0; attempt < 64; attempt++)
        {
            var probe = new WireWriter(64);
            probe.Bytes(prefix).F32(x).F32(y).F32(z);
            if (suffix is not null) probe.Bytes(suffix);
            if (probe.WrittenSpan.IndexOf(OwnerMarker) < 0) return (x, y, z);
            x = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(x) + 1);
            y = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(y) + 1);
            z = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(z) + 1);
        }

        return (x, y, z);
    }

    // ───────────────────────────── 33 36 / 45 36 identity ─────────────────────────────

    /// <summary><c>33 36</c> own character record (§8.6).</summary>
    public static byte[] EncodeSelfInfo(SelfInfoEvent e, SelfInfoEncodeOptions? options = null)
    {
        var o = options ?? SelfInfoEncodeOptions.Default;
        uint code = e.ClassCode != 0 ? e.ClassCode : ClassCodeFor(e.Class);
        var w = new WireWriter(48);
        w.Varint((ulong)e.Entity).U32(o.Mask1).U8(o.Mask2).String(e.Name).U16(e.ServerId).U32(code).U8(0x01).U32(e.Level);
        w.Bytes(o.Tail);
        return w.ToArray();
    }

    /// <summary><c>45 36</c> other player (§8.7). A guild run (server, guild id, 00 00, server, name) follows when the
    /// event has a guild name.</summary>
    public static byte[] EncodePlayerInfo(PlayerInfoEvent e, PlayerInfoEncodeOptions? options = null)
    {
        var o = options ?? PlayerInfoEncodeOptions.Default;
        uint code = e.ClassCode != 0 ? e.ClassCode : ClassCodeFor(e.Class);
        var w = new WireWriter(48);
        w.Varint((ulong)e.Entity).U32(o.Mask1).U8(o.Mask2).String(e.Name).U32(code);
        if (e.GuildName is not null)
        {
            ushort server = e.ServerId ?? 0;
            w.U16(server).U32(o.GuildId).U8(0).U8(0).U16(server).String(e.GuildName);
        }

        return w.ToArray();
    }

    // ───────────────────────────── 04 8D / 42 36 ─────────────────────────────

    /// <summary><c>04 8D</c> kill / last-blow record (§8.8).</summary>
    public static byte[] EncodeKill(KillEvent e, KillEncodeOptions? options = null)
    {
        var o = options ?? KillEncodeOptions.Default;
        var w = new WireWriter(40);
        w.Varint((ulong)e.Target).U32(e.SkillRaw).Varint((ulong)e.Killer).U16(e.KillerServerId)
            .String(e.KillerName).String(e.KillerGuildName).Bytes(o.Trailer);
        return w.ToArray();
    }

    /// <summary><c>42 36</c> death (§8.9): entity, 0, flag.</summary>
    public static byte[] EncodeDeath(DeathEvent e) =>
        new WireWriter(8).Varint((ulong)e.Entity).Varint(0UL).Varint((ulong)e.Flag).ToArray();

    // ───────────────────────────── 21 36 / 23 36 ─────────────────────────────

    /// <summary><c>21 36</c> map load (§8.10).</summary>
    public static byte[] EncodeMapLoad(MapLoadEvent e, MapLoadEncodeOptions? options = null)
    {
        var o = options ?? MapLoadEncodeOptions.Default;
        var w = new WireWriter(48);
        w.U32(e.LoadCount).U32(e.MapId).U32(o.InstanceKey).U32(0).F32(o.X).F32(o.Y).F32(o.Z).F32(o.Heading).Zeros(11).U8(o.TailValue).U16(0);
        return w.ToArray();
    }

    /// <summary><c>23 36</c> teleport: entity varint + position.</summary>
    public static byte[] EncodeTeleport(TeleportEvent e, float x = 0, float y = 0, float z = 0) =>
        new WireWriter(16).Varint((ulong)e.Entity).F32(x).F32(y).F32(z).ToArray();

    // ───────────────────────────── 02 97 roster ─────────────────────────────

    /// <summary><c>02 97</c> own party roster (§8.11): head and, per member, the stable prefix
    /// (presence, slot, dbid, name, class, level, gear score, born server, 3 bytes, combat power) plus a short tail.</summary>
    public static byte[] EncodePartyRoster(PartyRosterEvent e, RosterEncodeOptions? options = null)
    {
        var o = options ?? RosterEncodeOptions.Default;
        var members = e.Members;
        var w = new WireWriter(64 + members.Count * 64);
        w.U32(e.PartyKey).String(o.Title).U8(o.PartySize ?? (byte)members.Count).U32(e.DungeonId ?? 0).Bytes(o.HeadBytes1);
        w.U64(members.Count > 0 ? DbIdOf(members[0]) : 0).Bytes(o.HeadBytes2).Varint((ulong)(o.SlotCount ?? (uint)members.Count));
        foreach (var m in members)
        {
            uint code = m.ClassCode != 0 ? m.ClassCode : ClassCodeFor(m.Class);
            w.U8(o.Presence).U8((byte)m.Slot).U64(DbIdOf(m)).String(m.Name).U32(code).U32(m.Level);
            if ((o.Presence & 0x01) != 0) w.U32(0);
            w.U32(m.GearScore ?? 0);
            if ((o.Presence & 0x04) != 0) w.U16(m.ServerId);
            w.U8(0xD8).U8(0x10).U8(0x04).U64(m.CombatPower ?? 0).U64(0).U8(0x01).U8(0x01);
        }

        return w.ToArray();
    }

    /// <summary>dbid of a roster member: <see cref="PartyMember.DbId"/> or (server &lt;&lt; 48) | character id.</summary>
    public static ulong DbIdOf(PartyMember m) => m.DbId != 0 ? m.DbId : ((ulong)m.ServerId << 48) | m.CharacterId;

    // ───────────────────────────── small records ─────────────────────────────

    /// <summary><c>1B 92</c> HP/MP (§8.12): entity, hp, hp_max varints.</summary>
    public static byte[] EncodeHpUpdate(HpUpdateEvent e) =>
        new WireWriter(16).Varint((ulong)e.Entity).Varint(e.Hp).Varint(e.HpMax).ToArray();

    /// <summary><c>2A 38</c> buff applied (§8.13), the layout verified on 4,866 real Global frames: target, count
    /// (<see cref="BuffEncodeOptions.Byte1"/>, 1), then one entry = flags (<see cref="BuffEncodeOptions.Byte2"/>; bit 0x02 =
    /// source skill present), stack, buff u32, duration u64 ms (all FF when permanent), expiry u64, caster, level u8,
    /// [source skill u32], tail u8, 3 × f32.</summary>
    public static byte[] EncodeBuffApplied(BuffAppliedEvent e, BuffEncodeOptions? options = null)
    {
        var o = options ?? BuffEncodeOptions.Default;
        var w = new WireWriter(48);
        ulong duration = e.DurationMs == uint.MaxValue ? ulong.MaxValue : e.DurationMs;
        w.Varint((ulong)e.Target).U8(o.Byte1).U8(o.Byte2).Varint((ulong)e.Stack).U32(e.BuffId).U64(duration)
            .U64(e.ExpiryUnixMs).Varint((ulong)e.Caster).U8(o.Level);
        if ((o.Byte2 & 0x02) != 0) w.U32(e.SourceSkill);
        w.U8(o.Tail).F32(o.X).F32(o.Y).F32(o.Z);
        return w.ToArray();
    }

    /// <summary><c>0E 92</c> buff removed: target, 01, buff id.</summary>
    public static byte[] EncodeBuffRemoved(BuffRemovedEvent e) =>
        new WireWriter(12).Varint((ulong)e.Target).U8(0x01).U32(e.BuffId).ToArray();

    /// <summary><c>02 38</c> cast announcement (§8.14).</summary>
    public static byte[] EncodeCast(CastEvent e, CastEncodeOptions? options = null)
    {
        var o = options ?? CastEncodeOptions.Default;
        var w = new WireWriter(40);
        w.Varint((ulong)e.Actor).Varint((ulong)o.Flag).U32(e.SkillRaw).U8(o.Sequence).U8(o.Kind).Varint((ulong)e.Target)
            .F32(o.X).F32(o.Y).F32(o.Z).F32(o.Heading).Bytes(o.Tail);
        return w.ToArray();
    }

    /// <summary><c>03 36</c> ping echo (§8.15): <c>00 00</c>, i64 client-sent ms, i64 server ms.</summary>
    public static byte[] EncodePing(PingEvent e, long serverMs = 0) =>
        new WireWriter(18).U8(0).U8(0).I64(e.ClientSentMs).I64(serverMs).ToArray();

    /// <summary><c>00 36</c> heartbeat (§8.1): u64 server Unix ms.</summary>
    public static byte[] EncodeHeartbeat(HeartbeatEvent e) => new WireWriter(8).U64(e.ServerUnixMs).ToArray();

    /// <summary><c>20 36</c> session ↔ global id (§8.16): 2 bytes, session varint, 4 bytes, global id u32.</summary>
    public static byte[] EncodeGlobalIdLink(GlobalIdLinkEvent e) =>
        new WireWriter(16).U8(0).U8(0).Varint((ulong)e.Entity).U32(0).U32(e.CharacterId).ToArray();

    /// <summary><c>21 8D</c> battle toggle (§8.16): mob, 0, toggle.</summary>
    public static byte[] EncodeBattleToggle(BattleToggleEvent e) =>
        new WireWriter(8).Varint((ulong)e.Entity).Varint(0UL).Varint(e.Engaged ? 1UL : 0UL).ToArray();

    // ───────────────────────────── dispatch ─────────────────────────────

    /// <summary>The opcode (wire order) a game event is sent with.</summary>
    public static ushort OpcodeOf(GameEvent e) => e switch
    {
        HeartbeatEvent => Opcodes.Heartbeat,
        DamageEvent => Opcodes.Damage,
        DotEvent => Opcodes.DotTick,
        EntityStatsEvent => Opcodes.EntityStats,
        SpawnEvent => Opcodes.Spawn,
        SelfInfoEvent => Opcodes.SelfInfo,
        PlayerInfoEvent => Opcodes.PlayerInfo,
        KillEvent => Opcodes.Kill,
        DeathEvent => Opcodes.Death,
        MapLoadEvent => Opcodes.MapLoad,
        TeleportEvent => Opcodes.Teleport,
        PartyRosterEvent => Opcodes.PartyRoster,
        HpUpdateEvent => Opcodes.HpUpdate,
        BuffAppliedEvent => Opcodes.BuffApplied,
        BuffRemovedEvent => Opcodes.BuffRemoved,
        CastEvent => Opcodes.Cast,
        PingEvent => Opcodes.Ping,
        GlobalIdLinkEvent => Opcodes.GlobalIdLink,
        BattleToggleEvent => Opcodes.BattleToggle,
        _ => throw new NotSupportedException($"No encoder for {e.GetType().Name}."),
    };

    /// <summary>Body bytes for any supported event (default options).</summary>
    public static byte[] EncodeBody(GameEvent e) => e switch
    {
        HeartbeatEvent x => EncodeHeartbeat(x),
        DamageEvent x => EncodeDamage(x),
        DotEvent x => EncodeDot(x),
        EntityStatsEvent x => EncodeEntityStats(x),
        SpawnEvent x => EncodeSpawn(x, new SpawnEncodeOptions { Heading = (x.Entity % 360) + 0.5f }),
        SelfInfoEvent x => EncodeSelfInfo(x),
        PlayerInfoEvent x => EncodePlayerInfo(x),
        KillEvent x => EncodeKill(x),
        DeathEvent x => EncodeDeath(x),
        MapLoadEvent x => EncodeMapLoad(x),
        TeleportEvent x => EncodeTeleport(x),
        PartyRosterEvent x => EncodePartyRoster(x),
        HpUpdateEvent x => EncodeHpUpdate(x),
        BuffAppliedEvent x => EncodeBuffApplied(x),
        BuffRemovedEvent x => EncodeBuffRemoved(x),
        CastEvent x => EncodeCast(x),
        PingEvent x => EncodePing(x, x.ClientSentMs + 40),
        GlobalIdLinkEvent x => EncodeGlobalIdLink(x),
        BattleToggleEvent x => EncodeBattleToggle(x),
        _ => throw new NotSupportedException($"No encoder for {e.GetType().Name}."),
    };

    /// <summary>Opcode + body.</summary>
    public static byte[] EncodePayload(GameEvent e) => Payload(OpcodeOf(e), EncodeBody(e));

    /// <summary>Complete frame: varint(L) + opcode + body.</summary>
    public static byte[] EncodeFrame(GameEvent e) => Framing.Frame(EncodePayload(e));

    /// <summary>Concatenates opcode (wire order) and body.</summary>
    public static byte[] Payload(ushort opcode, ReadOnlySpan<byte> body)
    {
        var payload = new byte[body.Length + 2];
        payload[0] = (byte)(opcode >> 8);
        payload[1] = (byte)opcode;
        body.CopyTo(payload.AsSpan(2));
        return payload;
    }
}
