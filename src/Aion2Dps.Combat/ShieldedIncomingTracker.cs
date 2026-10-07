namespace Aion2Dps.Combat;

/// <summary>Matches the observed retaliation/absorb/net-damage triple without admitting unvalidated damage records.</summary>
internal sealed class ShieldedIncomingTracker
{
    private const int MaxPending = 128;
    private readonly record struct Key(uint Actor, uint Target, int Depth);
    private sealed record Tick(DotEvent Event, Hit Hit, Encounter Encounter);
    private sealed class Pending
    {
        public readonly List<Tick> Retaliations = new();
        public readonly List<Tick> Absorptions = new();
        public readonly List<DamageEvent> Companions = new();
    }

    private readonly Dictionary<Key, Pending> _pending = new();
    private DateTime? _time;
    private int _count;

    public void BeginEvent(DateTime time)
    {
        // These records share the exact capture timestamp. Never associate later hits with an earlier shield.
        if (_time != time) Clear();
        _time = time;
    }

    public void Clear()
    {
        _pending.Clear();
        _time = null;
        _count = 0;
    }

    public void OnTick(DotEvent e, Hit hit, Encounter enc)
    {
        bool retaliation = e.Flags == 0x4A && e.SkillId is uint npcSkill
            && SkillIds.GetKind(npcSkill) == SkillKind.Npc && e.EffectId / 100 == npcSkill;
        bool absorption = e.Flags == 0x0A && e.SkillId is uint playerSkill
            && SkillIds.GetKind(playerSkill) == SkillKind.Player && e.EffectId / 100 == playerSkill;
        if (!retaliation && !absorption) return;
        var key = new Key(e.Actor, e.Target, e.BundleDepth);
        var pending = GetPending(key);
        (retaliation ? pending.Retaliations : pending.Absorptions).Add(new Tick(e, hit, enc));
        TryMatch(key, pending);
    }

    public void OnCompanion(DamageEvent e)
    {
        // Only the observed shape: skill-less type-9 net damage with one explicit absorb effect.
        // Other unvalidated records remain discarded by CombatCore's normal validator.
        if (e.EffectValidated || e.SkillRaw != 0 || e.SkillId != 0 || e.Switch != 0x44 || e.Layout != 4
            || e.DamageType != 9 || e.HitIndex != 0 || e.PowerScalar != 0 || e.ExtraHits.Count != 0
            || e.EffectId != 1_701_000_011 || e.AbsorbEffects.Count != 1 || e.AbsorbEffects[0] == 0
            || e.Amount is not long amount || amount < 0 || amount > CombatCore.MaxAmount) return;
        var key = new Key(e.Actor, e.Target, e.BundleDepth);
        var pending = GetPending(key);
        pending.Companions.Add(e);
        TryMatch(key, pending);
    }

    private Pending GetPending(Key key)
    {
        // Bound memory even if malformed traffic repeats a timestamp forever. Failure to pair keeps normal guards.
        if (_count >= MaxPending)
        {
            _pending.Clear();
            _count = 0;
        }
        _count++;
        if (!_pending.TryGetValue(key, out var p)) _pending[key] = p = new Pending();
        return p;
    }

    private void TryMatch(Key key, Pending p)
    {
        foreach (var companion in p.Companions)
            foreach (var retaliation in p.Retaliations)
                foreach (var absorption in p.Absorptions)
                {
                    if (absorption.Event.EffectId != companion.AbsorbEffects[0]
                        || retaliation.Hit.Amount - absorption.Hit.Amount != companion.Amount
                        || !ReferenceEquals(retaliation.Encounter, absorption.Encounter)
                        || retaliation.Encounter.Finalized) continue;
                    var enc = retaliation.Encounter;
                    // Correct only the already recorded NPC tick. The invalid companion is never admitted as a hit.
                    retaliation.Hit.Amount = companion.Amount!.Value;
                    enc.Hits.Remove(absorption.Hit);
                    enc.RebuildLive(new HashSet<uint> { key.Target });
                    p.Companions.Remove(companion);
                    p.Retaliations.Remove(retaliation);
                    p.Absorptions.Remove(absorption);
                    _count -= 3;
                    if (p.Companions.Count + p.Retaliations.Count + p.Absorptions.Count == 0) _pending.Remove(key);
                    return;
                }
    }
}
