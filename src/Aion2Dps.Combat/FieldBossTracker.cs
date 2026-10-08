namespace Aion2Dps.Combat;

/// <summary>
/// Names open-world bosses whose spawn (<c>41 36</c>, the only record carrying the NPC code) was missed because the meter
/// started mid-fight. The <c>01 91</c> field-boss list gives every field boss slot of the region's map with its position;
/// a boss NPC's own cast announcements (<c>02 38</c>) give its position. A boss standing next to exactly one living slot
/// is that slot's boss, and the slot's place (slot − map × 100) indexes the map's bosses in NPC-code order
/// (<c>field_boss_maps.json</c> block). Blocks of maps missing from the data file are learned from a boss seen with
/// its spawn record. Not thread-safe; owned by <see cref="CombatCore"/>.
/// </summary>
internal sealed class FieldBossTracker
{
    /// <summary>A boss within this distance of a slot (world units, real: 1,550 for a boss walking around its spawn
    /// point; the next slot was 23,000 away) can be matched to it.</summary>
    public const float MatchRadius = 6000f;
    /// <summary>The second-nearest living slot must be at least this many times farther than the nearest.</summary>
    public const float Ambiguity = 3f;
    private const int MaxPositions = 512;

    private readonly Dictionary<uint, IReadOnlyList<FieldBossSlot>> _lists = new();
    private readonly Dictionary<uint, uint> _learnedBlocks = new();
    private readonly Dictionary<uint, (float X, float Y, float Z)> _positions = new();

    /// <summary>The map of the latest field-boss list (the region's overworld map); a map hint when no <c>21 36</c>
    /// was captured.</summary>
    public uint? MapHint { get; private set; }

    public void OnList(FieldBossListEvent e)
    {
        if (e.MapId == 0 || e.Slots.Count == 0) return;
        _lists[e.MapId] = e.Slots;
        MapHint = e.MapId;
    }

    public void NotePosition(uint entity, float x, float y, float z)
    {
        if (_positions.Count >= MaxPositions && !_positions.ContainsKey(entity)) _positions.Clear();
        _positions[entity] = (x, y, z);
    }

    public bool TryGetPosition(uint entity, out (float X, float Y, float Z) pos) => _positions.TryGetValue(entity, out pos);

    /// <summary>
    /// The NPC code of the field boss standing at the given position, or null when no living slot is close enough, two
    /// are comparably close, or the map's code block is unknown.
    /// </summary>
    public uint? Resolve(IGameData gd, float x, float y, float z)
    {
        if (!TryNearest(x, y, z, out uint map, out uint slot)) return null;
        uint? block = gd.GetFieldBossBlock(map) ?? (_learnedBlocks.TryGetValue(map, out uint lb) ? lb : null);
        if (block is not uint b) return null;
        uint? code = gd.GetFieldBossNpcCode(b, (int)(slot - map * 100));
        return code is uint c && gd.GetNpc(c) is { IsBoss: true, IsDummy: false } ? c : null;
    }

    /// <summary>
    /// A boss with a known NPC code stands next to a slot of a map whose block is not in the data file: learn the block
    /// (code / 1000), but only when that block's place order agrees with the slot.
    /// </summary>
    public void Learn(IGameData gd, uint npcCode, float x, float y, float z)
    {
        if (!TryNearest(x, y, z, out uint map, out uint slot)) return;
        if (gd.GetFieldBossBlock(map) is not null || _learnedBlocks.ContainsKey(map)) return;
        uint block = npcCode / 1000;
        if (gd.GetFieldBossNpcCode(block, (int)(slot - map * 100)) == npcCode) _learnedBlocks[map] = block;
    }

    private bool TryNearest(float x, float y, float z, out uint map, out uint slot)
    {
        map = 0;
        slot = 0;
        double best = double.MaxValue, second = double.MaxValue;
        foreach (var (m, slots) in _lists)
        {
            foreach (var s in slots)
            {
                if (!s.Alive) continue;
                double dx = s.X - x, dy = s.Y - y, dz = s.Z - z;
                double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d < best)
                {
                    second = best;
                    best = d;
                    map = m;
                    slot = s.Slot;
                }
                else if (d < second)
                {
                    second = d;
                }
            }
        }
        return best <= MatchRadius && second >= Ambiguity * best && second > MatchRadius;
    }

    /// <summary>Zone change: entity ids are reissued (the lists and learned blocks stay valid).</summary>
    public void ClearPositions() => _positions.Clear();

    public void Clear()
    {
        _lists.Clear();
        _learnedBlocks.Clear();
        _positions.Clear();
        MapHint = null;
    }
}
