using System.Text;
using Aion2Dps.Contracts;

namespace Aion2Dps.Armory;

/// <summary>Slot, grade and class helpers for the official character-info data.</summary>
public static class ArmorySlots
{
    // slotPos values observed on the live site (2026-10): 1 MainHand, 2 SubHand, 3 Helmet, 4 Shoulder, 5 Torso, 6 Pants,
    // 7 Gloves, 8 Boots, 10 Necklace, 11/12 Earring1/2, 13/14 Ring1/2, 15/16 Bracelet1/2, 17 Belt, 19 Cape, 22 Amulet,
    // 23/24 Rune1/2, 41..46 Arcana1..6. Unknown positions fall back to the slot name.

    public static SlotGroup GroupOf(int slotPos, string? slotName)
    {
        var n = slotName ?? "";
        if (n.StartsWith("Arcana", StringComparison.OrdinalIgnoreCase) || slotPos is >= 41 and <= 49) return SlotGroup.Arcana;
        if (n.StartsWith("Rune", StringComparison.OrdinalIgnoreCase) || slotPos is 23 or 24) return SlotGroup.Rune;
        if (n is "MainHand" or "SubHand" || slotPos is 1 or 2) return SlotGroup.Weapon;
        if (n is "Helmet" or "Shoulder" or "Torso" or "Pants" or "Gloves" or "Boots" or "Cape" || slotPos is >= 3 and <= 8 or 19)
            return SlotGroup.Armor;
        if (slotPos is >= 9 and <= 30 || n.Length > 0) return SlotGroup.Accessory;
        return SlotGroup.Other;
    }

    /// <summary>Sort key that keeps the in-game slot order inside a group (weapons, armor top-down, accessories).</summary>
    public static int SortKey(int slotPos) => slotPos switch
    {
        19 => 9, // cape after boots
        _ => slotPos,
    };

    /// <summary>"MainHand" → "Main Hand", "SubHand" → "Off Hand", "Earring1" → "Earring 1".</summary>
    public static string DisplaySlotName(string? slotName, int slotPos)
    {
        if (string.IsNullOrWhiteSpace(slotName)) return slotPos > 0 ? $"Slot {slotPos}" : "Slot";
        if (slotName == "SubHand") return "Off Hand";
        if (slotName == "Torso") return "Chest";
        if (slotName == "Pants") return "Legs";
        var sb = new StringBuilder(slotName.Length + 4);
        for (var i = 0; i < slotName.Length; i++)
        {
            var c = slotName[i];
            if (i > 0)
            {
                var p = slotName[i - 1];
                if ((char.IsUpper(c) && char.IsLower(p)) || (char.IsDigit(c) && !char.IsDigit(p))) sb.Append(' ');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    public static ItemGrade ParseGrade(string? grade) => grade?.Trim().ToLowerInvariant() switch
    {
        null or "" => ItemGrade.Unknown,
        "common" or "normal" => ItemGrade.Common,
        "rare" => ItemGrade.Rare,
        "legend" or "legendary" => ItemGrade.Legend,
        "unique" => ItemGrade.Unique,
        "epic" or "heroic" => ItemGrade.Epic,
        "special" => ItemGrade.Special,
        "mythic" or "myth" => ItemGrade.Mythic,
        _ => ItemGrade.Unknown,
    };

    public static Faction ParseFaction(int? raceId) => raceId switch
    {
        1 => Faction.Elyos,
        2 => Faction.Asmodian,
        _ => Faction.Unknown,
    };

    private static readonly CharacterClass[] PcIdOrder =
    [
        CharacterClass.Gladiator, CharacterClass.Templar, CharacterClass.Ranger, CharacterClass.Assassin,
        CharacterClass.Elementalist, CharacterClass.Sorcerer, CharacterClass.Cleric, CharacterClass.Chanter,
    ];

    /// <summary>
    /// Maps the site's pcId (class × gender × race, from <c>/api/gameinfo/pcdata</c>: 5-8 Gladiator, 9-12 Templar, 13-16 Ranger,
    /// 17-20 Assassin, 21-24 Elementalist, 25-28 Sorcerer, 29-32 Cleric, 33-36 Chanter) to the game class.
    /// </summary>
    public static CharacterClass ClassFromPcId(int pcId)
    {
        if (pcId < 5) return CharacterClass.Unknown;
        var idx = (pcId - 5) / 4;
        return idx < PcIdOrder.Length ? PcIdOrder[idx] : CharacterClass.Unknown;
    }

    /// <summary>Maps an English class name (site "className": "Sorcerer", "ELEMENTALIST", "Spiritmaster") to the game class.</summary>
    public static CharacterClass ClassFromName(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "gladiator" => CharacterClass.Gladiator,
        "templar" => CharacterClass.Templar,
        "ranger" => CharacterClass.Ranger,
        "assassin" => CharacterClass.Assassin,
        "elementalist" or "spiritmaster" => CharacterClass.Elementalist,
        "sorcerer" => CharacterClass.Sorcerer,
        "cleric" => CharacterClass.Cleric,
        "chanter" => CharacterClass.Chanter,
        "brawler" => CharacterClass.Brawler,
        _ => CharacterClass.Unknown,
    };

    /// <summary>Display name of a class (Elementalist is "Spiritmaster" on the Global site).</summary>
    public static string ClassDisplayName(CharacterClass c) => c switch
    {
        CharacterClass.Elementalist => "Spiritmaster",
        CharacterClass.Unknown => "Unknown",
        _ => c.ToString(),
    };

    public static StatCategory StatCategoryOf(string type) => type switch
    {
        "STR" or "DEX" or "INT" or "CON" or "AGI" or "WIS" => StatCategory.Core,
        "Justice" or "Freedom" or "Illusion" or "Life" or "Time" or "Destruction" or "Death" or "Wisdom" or "Destiny" or "Space"
            => StatCategory.Pantheon,
        "ItemLevel" => StatCategory.ItemLevel,
        _ => StatCategory.Other,
    };
}
