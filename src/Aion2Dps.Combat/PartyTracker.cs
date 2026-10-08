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

    private readonly HashSet<uint> _hpPeers = new();

    /// <summary>
    /// [real] <c>1B 92</c> (HP/MP) is sent only for the other members of your own group (4/4 party members in the
    /// dungeon capture, 0 strangers; the world-boss capture: the same 4 ids for 6 minutes while ~40 players fought
    /// nearby). Without a roster (meter started mid-session) this is the party evidence. Session ids; cleared on zone change.
    /// </summary>
    public void NoteHpPeer(uint entity)
    {
        if (entity != 0 && _hpPeers.Count < 64) _hpPeers.Add(entity);
    }

    public bool HasHpPeers => _hpPeers.Count > 0;

    public bool IsHpPeer(uint entity) => _hpPeers.Contains(entity);

    /// <summary>A roster member (by name) or, roster or not, an entity the server sends group HP updates for.</summary>
    public bool IsMember(string? name, uint entity) => IsMember(name) || _hpPeers.Contains(entity);

    /// <summary>The party is known from a roster or from group HP updates.</summary>
    public bool IsKnown => HasRoster || HasHpPeers;

    public void ClearHpPeers() => _hpPeers.Clear();

    public PartyMember? Get(string? name) => !string.IsNullOrEmpty(name) && _byName.TryGetValue(name, out var m) ? m : null;

    public void Clear()
    {
        _members = new();
        _byName.Clear();
        PartyKey = 0;
        DungeonId = null;
        _hpPeers.Clear();
    }
}
