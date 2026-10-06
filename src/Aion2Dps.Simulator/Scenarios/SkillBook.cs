using Aion2Dps.Contracts;

namespace Aion2Dps.Simulator;

/// <summary>Real AION 2 skill and NPC ids used by the scenarios (names from the bundled game data).</summary>
public static class SkillBook
{
    // Gladiator (prefix 11)
    public const uint KeenStrike = 11020030;
    public const uint RuptureStrike = 11030030;
    public const uint WrathfulStrike = 11040000;
    public const uint CrushingWave = 11050000;
    public const uint RuinousBlow = 11100010;
    /// <summary>Self-heal (actor == target) in real captures.</summary>
    public const uint BloodAbsorption = 11730007;

    // Templar (12) / Assassin (13)
    public const uint ViciousStrike = 12010000;
    public const uint QuickSlice = 13010000;
    public const uint ThrowShadowblade = 13020000;
    public const uint BreakingSlice = 13030000;

    // Ranger (prefix 14)
    /// <summary>Deadshot variant seen in the real boss self-heal sequence.</summary>
    public const uint Deadshot = 14010243;
    public const uint Snipe = 14020000;
    public const uint RapidFire = 14030000;
    public const uint SpiralArrow = 14040000;

    // Sorcerer (prefix 15)
    public const uint FlameScattershot = 15010000;
    /// <summary>DoT source (in the DoT table).</summary>
    public const uint Firebomb = 15020000;
    public const uint Burst = 15030000;
    public const uint Firestorm = 15040000;
    /// <summary>Bittercold Wind cast variant (layout-0 notice by the Sorcerer).</summary>
    public const uint BittercoldWindCast = 15280240;
    /// <summary>The wind entity's own tick announcement (02 38, cast variant + 1).</summary>
    public const uint BittercoldWindTick = 15280241;
    /// <summary>The wind entity's damage variant.</summary>
    public const uint BittercoldWindHit = 15280243;

    // Elementalist (prefix 16)
    public const uint ColdShock = 16010000;
    public const uint VacuumExplosion = 16020000;
    public const uint Combustion = 16040000;
    /// <summary>DoT source (in the DoT table).</summary>
    public const uint JointstrikeCurse = 16140030;
    public const uint SummonFireSpirit = 16100010;
    public const uint SummonWaterSpirit = 16110000;
    /// <summary>Spirit link record (16,990,000-16,999,999): the spirit's own spawn self-heal. Not damage, not healing.</summary>
    public const uint SpiritLink = 16990004;
    public const uint FireSpiritAttack = 100015;
    public const uint WaterSpiritAttack = 100025;

    // Cleric (prefix 17)
    public const uint EarthsRetribution = 17010000;
    public const uint ThunderAndLightning = 17020000;
    public const uint JudgmentThunder = 17040000;
    /// <summary>Heal family 17100000.</summary>
    public const uint HealingLight = 17100030;
    /// <summary>Heal family 17120000 (used as a HoT).</summary>
    public const uint RadiantRecovery = 17120030;
    public const uint DivineAura = 17150030;

    // Chanter (prefix 18)
    public const uint DarkCrush = 18100000;
    /// <summary>Heal family 18120000 (Recuperation).</summary>
    public const uint Recuperation = 18120000;

    // NPC skills (7 digits = damage taken)
    public const uint NpcAttack = 1800160;
    public const uint NpcCleave = 1800170;
    public const uint NpcSlam = 1800180;
    /// <summary>Monster effect of the real boss self-heal tick (9 digits).</summary>
    public const uint BossSelfHealEffect = 160466011;

    // NPC template codes
    public const uint UltimateBerk = 2300171;
    public const uint EnhancedHarcon = 2300104;
    public const uint KraoMutant = 2300107;
    public const uint KraoMagicTestSubject = 2300101;
    public const uint TrainingScarecrow = 2400032;
    public const uint FireSpirit = 2920114;
    public const uint WaterSpirit = 2920129;
    public const uint BittercoldWind = 2920011;
    public const uint DivineAuraNpc = 2920640;

    // Maps
    public const uint KraoCave = 600001;
    public const uint KraoCaveHard = 600002;
    public const uint KraoCaveTrash = 600003;
    public const uint WorldLA = 1010;

    /// <summary>Heal-family bases (data/healing_skill_ids.json): an amount from these is healing, not damage.</summary>
    public static readonly IReadOnlySet<uint> HealFamilies = new HashSet<uint>
    {
        18120000, 18170000, 16770000, 16190000, 17120000, 17800000, 17100000, 17410000,
    };

    public static bool IsHealSkill(uint skillId) => HealFamilies.Contains(SkillIds.BaseId(skillId));
}
