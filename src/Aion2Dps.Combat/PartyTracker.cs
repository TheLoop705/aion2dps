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

    public PartyMember? Get(string? name) => !string.IsNullOrEmpty(name) && _byName.TryGetValue(name, out var m) ? m : null;

    public void Clear()
    {
        _members = new();
        _byName.Clear();
        PartyKey = 0;
        DungeonId = null;
    }
}
