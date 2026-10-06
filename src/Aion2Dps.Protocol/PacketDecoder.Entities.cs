using System.Buffers.Binary;
using System.Text;
using System.Text.Unicode;
using Aion2Dps.Contracts;

namespace Aion2Dps.Protocol;

// Entity records: spawn, own character, other players, party roster.
public sealed partial class PacketDecoder
{
    /// <summary>Caster anchor in <c>41 36</c> (§8.5 owner source 2): these 8 bytes, then an owner varint.</summary>
    private static ReadOnlySpan<byte> CasterAnchor => [0x80, 0x75, 0xD5, 0x2A, 0xBB, 0x03, 0x00, 0x00];

    private static ReadOnlySpan<byte> EightFf => [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    private static ReadOnlySpan<byte> OwnerMarker => [0x07, 0x02, 0x06];

    /// <summary>Kind bytes of summons, spirits and skill entities (§8.5).</summary>
    public static bool IsSummonKind(byte kind) => kind is 0x1F or 0x1D or 0x5D or 0x5F or 0x1C;

    private struct SpawnHead
    {
        public string? Name;
        public uint NpcCode;
        public float X, Y, Z;
        public long? HpCur, HpMax;
    }

    /// <summary><c>41 36</c> spawn (§8.5): structured head with the gate at +2 (fallback +4), then owner markers.</summary>
    private bool DecodeSpawn(ReadOnlySpan<byte> body)
    {
        var r = new SpanReader(body);
        if (!r.TryReadVarUInt(out uint entity)) return Fail("truncated entity");
        if (!r.TryReadU8(out byte kind) || !r.TryReadU8(out byte kindFlags)) return Fail("truncated kind");
        int afterEntity = r.Position - 2;

        bool headOk = TryParseSpawnHead(body, afterEntity + 2, out var head) || TryParseSpawnHead(body, afterEntity + 4, out head);
        if (!headOk) head = default;

        uint? owner = FindOwner(body, afterEntity, entity);
        uint? anchor = FindAnchor(body, afterEntity);

        Emit(new SpawnEvent
        {
            Time = _time, BundleDepth = _depth, Entity = entity, KindByte = kind, KindFlags = kindFlags,
            CasterName = head.Name, NpcCode = head.NpcCode, X = head.X, Y = head.Y, Z = head.Z,
            HpCurrent = head.HpCur, HpMax = head.HpMax, OwnerId = owner, AnchorId = anchor,
            IsSummonLike = IsSummonKind(kind),
        });

        // An unrecognised head is still emitted (entity, kind and owner are useful) but counted as a failure so that
        // layout drift shows up in the census.
        return headOk || Fail("spawn head not recognised (gate +2/+4)");
    }

    private static bool TryParseSpawnHead(ReadOnlySpan<byte> body, int gateOffset, out SpawnHead head)
    {
        head = default;
        var r = new SpanReader(body) { Position = gateOffset };
        if (r.Position != gateOffset || !r.TryReadU8(out byte gate)) return false;
        if ((gate & 0x01) != 0)
        {
            if (!r.TryReadString(out string name) || name.Length == 0) return false;
            head.Name = name;
        }

        if (!r.TryReadU32(out uint npc) || npc > 0x00FF_FFFF) return false;
        if (!r.TryReadU8(out byte m1) || (m1 & 0xBF) != 0) return false; // 00 or 40
        if (!r.TryReadU8(out byte m2) || m2 != 0x02) return false;
        head.NpcCode = npc;
        if (!r.TryReadF32(out head.X) || !r.TryReadF32(out head.Y) || !r.TryReadF32(out head.Z) || !r.TryReadF32(out _))
            return true; // head identified, positions missing
        if (!r.TrySkip(2) || !r.TryReadU8(out byte m3) || m3 != 0x01) return true;
        if (r.TryReadVarUInt(out uint cur) && r.TryReadVarUInt(out uint max))
        {
            head.HpCur = cur;
            head.HpMax = max;
        }

        return true;
    }

    /// <summary>§8.5 owner source 1: first <c>07 02 06</c> after the first run of eight FF bytes, then owner u32 LE,
    /// valid in 1..9,999,999 and ≠ entity.</summary>
    private static uint? FindOwner(ReadOnlySpan<byte> body, int from, uint entity)
    {
        if (from < 0 || from > body.Length) return null;
        var tail = body[from..];
        int ff = tail.IndexOf(EightFf);
        if (ff < 0) return null;
        var afterFf = tail[(ff + 8)..];
        int m = afterFf.IndexOf(OwnerMarker);
        if (m < 0 || afterFf.Length < m + 3 + 4) return null;
        uint owner = BinaryPrimitives.ReadUInt32LittleEndian(afterFf[(m + 3)..]);
        return owner is >= 1 and <= MaxEntityId && owner != entity ? owner : null;
    }

    /// <summary>§8.5 owner source 2: the caster anchor followed by a varint (may equal the entity itself).</summary>
    private static uint? FindAnchor(ReadOnlySpan<byte> body, int from)
    {
        if (from < 0 || from > body.Length) return null;
        var tail = body[from..];
        int a = tail.IndexOf(CasterAnchor);
        if (a < 0) return null;
        return VarInt.TryRead(tail[(a + CasterAnchor.Length)..], out uint id, out _) == VarIntStatus.Ok ? id : null;
    }

    /// <summary><c>33 36</c> own character (§8.6): entity, mask1 u32, mask2 u8, name, server u16, class u32, 01, level u32.</summary>
    private bool DecodeSelfInfo(ReadOnlySpan<byte> body)
    {
        var r = new SpanReader(body);
        if (!r.TryReadVarUInt(out uint entity)) return Fail("truncated entity");
        if (!r.TrySkip(4) || !r.TryReadU8(out byte mask2)) return Fail("truncated masks");
        string name = "";
        if ((mask2 & 0x01) != 0 && (!r.TryReadString(out name) || name.Length == 0)) return Fail("bad name");
        if (!r.TryReadU16(out ushort server)) return Fail("truncated server");
        if (server is < 1000 or > 2999) return Fail($"server id {server} out of range");
        if (!r.TryReadU32(out uint classCode)) return Fail("truncated class");
        uint level = 0;
        if (r.TrySkip(1) && r.TryReadU32(out uint lv)) level = lv;

        Emit(new SelfInfoEvent
        {
            Time = _time, BundleDepth = _depth, Entity = entity, Name = name, ServerId = server, ClassCode = classCode,
            Class = ClassInfo.FromCode(classCode), Level = level,
        });
        return true;
    }

    /// <summary><c>45 36</c> another player (§8.7): entity, mask1, mask2, name, class u32; then a guild run
    /// <c>server u16, guildId u32, 00 00, server u16, name</c> (gives the server id).</summary>
    private bool DecodePlayerInfo(ReadOnlySpan<byte> body)
    {
        var r = new SpanReader(body);
        if (!r.TryReadVarUInt(out uint entity)) return Fail("truncated entity");
        if (!r.TrySkip(4) || !r.TryReadU8(out byte mask2)) return Fail("truncated masks");
        string name = "";
        if ((mask2 & 0x01) != 0 && (!r.TryReadString(out name) || name.Length == 0)) return Fail("bad name");
        if (!r.TryReadU32(out uint classCode)) return Fail("truncated class");

        FindGuildRun(r.RemainingSpan, out ushort? server, out string? guild);
        Emit(new PlayerInfoEvent
        {
            Time = _time, BundleDepth = _depth, Entity = entity, Name = name, ClassCode = classCode,
            Class = ClassInfo.FromCode(classCode), ServerId = server, GuildName = guild,
        });
        return true;
    }

    private static void FindGuildRun(ReadOnlySpan<byte> s, out ushort? server, out string? guild)
    {
        server = null;
        guild = null;
        for (int i = 0; i + 12 <= s.Length; i++)
        {
            ushort sv = BinaryPrimitives.ReadUInt16LittleEndian(s[i..]);
            if (sv is < 1000 or > 2999) continue;
            if (s[i + 6] != 0 || s[i + 7] != 0) continue;
            if (BinaryPrimitives.ReadUInt16LittleEndian(s[(i + 8)..]) != sv) continue;
            int len = s[i + 10];
            if (len is < 1 or > 48 || i + 11 + len > s.Length) continue;
            var nameBytes = s.Slice(i + 11, len);
            if (!IsPlausibleName(nameBytes, out string? n)) continue;
            server = sv;
            guild = n;
            return;
        }
    }

    /// <summary>Valid UTF-8 without control characters.</summary>
    private static bool IsPlausibleName(ReadOnlySpan<byte> bytes, out string? name)
    {
        name = null;
        if (bytes.IsEmpty || !Utf8.IsValid(bytes)) return false;
        string s = Encoding.UTF8.GetString(bytes);
        foreach (char c in s)
        {
            if (char.IsControl(c)) return false;
        }

        name = s;
        return true;
    }

    /// <summary>
    /// <c>02 97</c> own party roster (§8.11), best effort: the head is read structurally; members are re-acquired by
    /// header shape (presence ≠ 0, increasing slot, dbid whose top 16 bits are a server id, a valid name, class and
    /// level), because the per-member tail does not line up.
    /// </summary>
    private bool DecodePartyRoster(ReadOnlySpan<byte> body)
    {
        var r = new SpanReader(body);
        if (!r.TryReadU32(out uint partyKey)) return Fail("truncated party key");
        if (!r.TryReadString(out _)) return Fail("bad party title");
        if (!r.TrySkip(1) || !r.TryReadU32(out uint dungeon)) return Fail("truncated dungeon");
        if (!r.TrySkip(2) || !r.TrySkip(8) || !r.TrySkip(3)) return Fail("truncated leader");
        if (!r.TryReadVarUInt(out uint count)) return Fail("truncated member count");
        int maxMembers = (int)Math.Min(count == 0 ? 8u : count, 24u);

        var members = new List<PartyMember>(maxMembers);
        int pos = r.Position;
        int prevSlot = 0;
        while (members.Count < maxMembers && pos < body.Length)
        {
            int at = FindMemberHeader(body, pos, prevSlot, out var member, out int end);
            if (at < 0) break;
            members.Add(member!);
            prevSlot = member!.Slot;
            pos = end;
        }

        if (members.Count == 0) return Fail("no roster member found");
        Emit(new PartyRosterEvent
        {
            Time = _time, BundleDepth = _depth, PartyKey = partyKey, DungeonId = dungeon == 0 ? null : dungeon,
            Members = members,
        });
        return true;
    }

    private static int FindMemberHeader(ReadOnlySpan<byte> s, int from, int prevSlot, out PartyMember? member, out int end)
    {
        member = null;
        end = from;
        for (int i = from; i + 2 + 8 + 1 + 8 <= s.Length; i++)
        {
            byte presence = s[i];
            byte slot = s[i + 1];
            if (presence == 0 || slot <= prevSlot || slot > 24) continue;
            ulong dbid = BinaryPrimitives.ReadUInt64LittleEndian(s[(i + 2)..]);
            ushort server = (ushort)(dbid >> 48);
            uint charId = (uint)dbid;
            if (server is < 1000 or > 2999 || charId == 0 || ((dbid >> 32) & 0xFFFF) != 0) continue;

            var r = new SpanReader(s) { Position = i + 10 };
            if (!r.TryPeekU8(out byte nameLen) || nameLen is < 1 or > 48) continue;
            if (!r.TryReadString(out string name) || !IsPlausibleName(Encoding.UTF8.GetBytes(name), out _)) continue;
            if (!r.TryReadU32(out uint classCode) || classCode is < 1 or > 63) continue;
            if (!r.TryReadU32(out uint level) || level is < 1 or > 99) continue;

            // Stable prefix found. The rest is best effort.
            uint? gear = null;
            ulong? power = null;
            int afterLevel = r.Position;
            if ((presence & 0x01) != 0) r.TrySkip(4); // conqueror level (rat)
            if (r.TryReadU32(out uint gs) && gs < 100_000) gear = gs;
            int p = r.Position;
            for (int k = 0; k <= 3 && p + k + 2 + 3 + 8 <= s.Length; k++)
            {
                ushort born = BinaryPrimitives.ReadUInt16LittleEndian(s[(p + k)..]);
                if (born is < 1000 or > 2999) continue;
                ulong cp = BinaryPrimitives.ReadUInt64LittleEndian(s[(p + k + 5)..]);
                if (cp < 10_000_000_000UL) power = cp;
                break;
            }

            member = new PartyMember
            {
                Slot = slot, DbId = dbid, CharacterId = charId, ServerId = server, Name = name, ClassCode = classCode,
                Class = ClassInfo.FromCode(classCode), Level = level, GearScore = gear, CombatPower = power,
            };
            end = afterLevel;
            return i;
        }

        return -1;
    }
}
