using System.Windows.Media;
using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis;

/// <summary>Icon substitutes: skills are drawn as class-coloured rounded squares with two-letter initials.</summary>
public static class SkillVisuals
{
    /// <summary>"Ferocious Strike" → "FS", "Cleave" → "Cl", "Skill 11020030" → "30".</summary>
    public static string Initials(string? name, uint skillId = 0)
    {
        if (string.IsNullOrWhiteSpace(name)) return (skillId % 100).ToString("00");
        var trimmed = name.Trim();
        if (trimmed.StartsWith("Skill ", StringComparison.OrdinalIgnoreCase) && trimmed[6..].All(char.IsDigit))
            return skillId != 0 ? (skillId % 100).ToString("00") : trimmed[^Math.Min(2, trimmed.Length - 6)..];

        var words = trimmed.Replace("'", "").Replace("’", "")
            .Split([' ', '-', ':', '(', ')', ','], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 0 && char.IsLetterOrDigit(w[0]))
            .Where(w => !IsMinorWord(w))
            .ToArray();
        if (words.Length == 0) return trimmed[..Math.Min(2, trimmed.Length)];
        if (words.Length == 1)
        {
            var w = words[0];
            // CJK names: the first two characters read better than a capitalised pair.
            return w.Length >= 2 ? char.ToUpperInvariant(w[0]) + w[1].ToString() : w.ToUpperInvariant();
        }
        return string.Concat(char.ToUpperInvariant(words[0][0]), char.ToUpperInvariant(words[1][0]));
    }

    private static bool IsMinorWord(string w) =>
        w.Equals("of", StringComparison.OrdinalIgnoreCase) || w.Equals("the", StringComparison.OrdinalIgnoreCase)
        || w.Equals("and", StringComparison.OrdinalIgnoreCase);

    /// <summary>Class to colour a skill tile with: the skill's class, else the owning combatant's class.</summary>
    public static CharacterClass TileClass(IGameData? gameData, uint skillId, CharacterClass owner)
    {
        var c = gameData?.GetSkillClass(skillId) ?? SkillIds.ClassOf(skillId);
        return c == CharacterClass.Unknown ? owner : c;
    }

    /// <summary>Text colour with enough contrast on <paramref name="background"/>.</summary>
    public static Brush ContrastText(Brush background)
    {
        if (background is SolidColorBrush s)
        {
            var c = s.Color;
            double lum = (0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B));
            return lum > 0.36 ? DarkText : Brushes.White;
        }
        return Brushes.White;

        static double Lin(byte v)
        {
            double x = v / 255.0;
            return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
    }

    private static readonly Brush DarkText = Freeze(new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1A)));

    internal static Brush Freeze(Brush b)
    {
        if (b.CanFreeze) b.Freeze();
        return b;
    }

    internal static Brush Hex(string hex) => Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)));

    internal static Brush WithOpacity(Brush b, double opacity)
    {
        var c = b.CloneCurrentValue();
        c.Opacity = b.Opacity * opacity;
        return Freeze(c);
    }
}
