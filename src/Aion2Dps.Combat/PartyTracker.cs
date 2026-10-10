namespace Aion2Dps.Combat;

/// <summary>Own party roster (§13). The latest complete <c>02 97</c> replaces the party. Members are joined to entities by name.</summary>
internal sealed class PartyTracker
{
    private readonly Dictionary<string, PartyMember> _byName = new(StringComparer.OrdinalIgnoreCase);
    private List<PartyMember> _members = new();

    public IReadOnlyList<PartyMember> Members => _members;
    public uint PartyKey { get; private set; }
    public uint? DungeonId { get; private set; }
    public bool HasRoster => _members.Count > 0;

    public void Replace(PartyRosterEvent e)
    {
        _members = e.Members.Where(m => !string.IsNullOrEmpty(m.Name)).ToList();
        _byName.Clear();
        foreach (var m in _members) _byName[m.Name] = m;
        PartyKey = e.PartyKey;
        DungeonId = e.DungeonId;
    }

    public bool IsMember(string? name) => !string.IsNullOrEmpty(name) && _byName.ContainsKey(name);

    /// <summary>A party has at most 5 players and a force 4 parties ([real] rosters say party size 5; 20 in a force).</summary>
    public const int MaxPartySize = 5, MaxForceSize = 20;

    /// <summary>Ids kept per kind; at the cap the one silent the longest makes room.</summary>
    private const int MaxPeers = 64;

    /// <summary>Group HP-update senders by entity, with the time of their latest update.</summary>
    private readonly Dictionary<uint, DateTime> _hpPeers = new();
    private readonly Dictionary<uint, DateTime> _forcePeers = new();

    /// <summary>
    /// [real] <c>1B 92</c> (HP/MP) is sent only for the other members of your own group (4/4 party members in the
    /// dungeon capture, 0 strangers; the world-boss capture: the same 4 ids for 6 minutes while ~40 players fought
    /// nearby). Without a roster (meter started mid-session) this is the party evidence. Session ids; cleared on zone
    /// change. A member counts while its updates keep coming (see <c>CombatCore.GroupSince</c>).
    /// </summary>
    /// <returns>True when the entity was not a member at <paramref name="since"/> before (new, or back after a pause).</returns>
    public bool NoteHpPeer(uint entity, DateTime time, DateTime since) => Note(_hpPeers, entity, time, since);

    /// <summary>
    /// [real] <c>2B 96</c> (force HP/MP) is sent for every other member of your force, your own party included: 19 of 19
    /// at two open-world field bosses (4 parties of 5), never for strangers. Members come and go within a zone ([real] 21
    /// distinct ids in one zone visit at Gartua), so membership lasts only while updates keep coming.
    /// </summary>
    /// <returns>True when the entity was not a force member at <paramref name="since"/> before.</returns>
    public bool NoteForcePeer(uint entity, DateTime time, DateTime since) => Note(_forcePeers, entity, time, since);

    private static bool Note(Dictionary<uint, DateTime> peers, uint entity, DateTime time, DateTime since)
    {
        if (entity == 0) return false;
        bool known = peers.TryGetValue(entity, out var seen);
        bool wasMember = known && seen >= since;
        if (!known && peers.Count >= MaxPeers) peers.Remove(peers.MinBy(kv => kv.Value).Key);
        if (!known || time > seen) peers[entity] = time;
        return !wasMember && peers[entity] >= since;
    }

    public bool IsHpPeer(uint entity, DateTime since) => _hpPeers.TryGetValue(entity, out var t) && t >= since;

    public bool HasHpPeers(DateTime since) => _hpPeers.Values.Any(t => t >= since);

    /// <summary>The party is known from a roster or from recent group HP updates.</summary>
    public bool IsKnown(DateTime since) => HasRoster || HasHpPeers(since);

    public void ClearHpPeers()
    {
        _hpPeers.Clear();
        _forcePeers.Clear();
    }

    public bool IsForcePeer(uint entity, DateTime since) => _forcePeers.TryGetValue(entity, out var t) && t >= since;

    /// <summary>You are in a force (several parties joined): its members' HP updates arrive.</summary>
    public bool HasForcePeers(DateTime since) => _forcePeers.Values.Any(t => t >= since);

    /// <summary>Other force members (your own party included) with an HP update since <paramref name="since"/>, at most 19.</summary>
    public int ActiveForcePeerCount(DateTime since) => Math.Min(MaxForceSize - 1, _forcePeers.Values.Count(t => t >= since));

    /// <summary>Other members of your own party: the roster (it always lists you too) without you, or the group HP updates
    /// since <paramref name="since"/> (at most 4).</summary>
    public int OtherPartyMemberCount(DateTime since) =>
        HasRoster ? Math.Max(0, _members.Count - 1) : Math.Min(MaxPartySize - 1, _hpPeers.Values.Count(t => t >= since));

    public PartyMember? Get(string? name) => !string.IsNullOrEmpty(name) && _byName.TryGetValue(name, out var m) ? m : null;

    public void Clear()
    {
        _members = new();
        _byName.Clear();
        PartyKey = 0;
        DungeonId = null;
        ClearHpPeers();
    }
}
