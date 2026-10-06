using System.Windows;
using System.Windows.Media;
using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis.Tests;

/// <summary>A complete ThemeKeys dictionary (dark and light) for offscreen renders. The App owns the real themes.</summary>
internal static class TestTheme
{
    public static ResourceDictionary Create(bool dark)
    {
        var d = new ResourceDictionary();
        void B(string key, string hex) => d[key] = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)));
        if (dark)
        {
            B(ThemeKeys.WindowBackground, "#111419");
            B(ThemeKeys.Surface, "#181C23");
            B(ThemeKeys.SurfaceAlt, "#222731");
            B(ThemeKeys.Border, "#2D3440");
            B(ThemeKeys.Text, "#E8EBF0");
            B(ThemeKeys.TextMuted, "#8C95A5");
            B(ThemeKeys.Accent, "#D9A441");
            B(ThemeKeys.AccentText, "#1B1406");
            B(ThemeKeys.Positive, "#4CC38A");
            B(ThemeKeys.Negative, "#E5605A");
            B(ThemeKeys.Warning, "#E3A33B");
            B(ThemeKeys.Crit, "#F2C14E");
            B(ThemeKeys.Dot, "#A774FF");
            B(ThemeKeys.Heal, "#4CC38A");
            B(ThemeKeys.BarTrack, "#262C36");
            B(ThemeKeys.BarFill, "#D9A441");
            B(ThemeKeys.HpHigh, "#4CC38A");
            B(ThemeKeys.HpMid, "#E3A33B");
            B(ThemeKeys.HpLow, "#E5605A");
            foreach (var c in ClassInfo.All.Append(CharacterClass.Unknown)) B(ThemeKeys.ClassBrush(c), ClassInfo.DefaultColor(c));
        }
        else
        {
            B(ThemeKeys.WindowBackground, "#F2F3F6");
            B(ThemeKeys.Surface, "#FFFFFF");
            B(ThemeKeys.SurfaceAlt, "#EEF0F4");
            B(ThemeKeys.Border, "#D8DCE4");
            B(ThemeKeys.Text, "#1A1E26");
            B(ThemeKeys.TextMuted, "#677083");
            B(ThemeKeys.Accent, "#A86F0C");
            B(ThemeKeys.AccentText, "#FFFFFF");
            B(ThemeKeys.Positive, "#1E9461");
            B(ThemeKeys.Negative, "#CF3B34");
            B(ThemeKeys.Warning, "#B9740B");
            B(ThemeKeys.Crit, "#C08B00");
            B(ThemeKeys.Dot, "#7A3BE6");
            B(ThemeKeys.Heal, "#1E9461");
            B(ThemeKeys.BarTrack, "#E4E7ED");
            B(ThemeKeys.BarFill, "#A86F0C");
            B(ThemeKeys.HpHigh, "#1E9461");
            B(ThemeKeys.HpMid, "#B9740B");
            B(ThemeKeys.HpLow, "#CF3B34");
            // Slightly deeper class colours for contrast on white.
            B(ThemeKeys.ClassBrush(CharacterClass.Gladiator), "#D27A2C");
            B(ThemeKeys.ClassBrush(CharacterClass.Templar), "#4A7FC8");
            B(ThemeKeys.ClassBrush(CharacterClass.Ranger), "#5DA937");
            B(ThemeKeys.ClassBrush(CharacterClass.Assassin), "#9550D0");
            B(ThemeKeys.ClassBrush(CharacterClass.Elementalist), "#22A6AE");
            B(ThemeKeys.ClassBrush(CharacterClass.Sorcerer), "#D63E4E");
            B(ThemeKeys.ClassBrush(CharacterClass.Cleric), "#D4B437");
            B(ThemeKeys.ClassBrush(CharacterClass.Chanter), "#5BAEE0");
            B(ThemeKeys.ClassBrush(CharacterClass.Brawler), "#D2618C");
            B(ThemeKeys.ClassBrush(CharacterClass.Unknown), "#80868F");
        }
        d[ThemeKeys.FontFamily] = new FontFamily("Segoe UI");
        d[ThemeKeys.MonoFontFamily] = new FontFamily("Cascadia Mono, Consolas");
        d[ThemeKeys.FontSize] = 12.5;
        d[ThemeKeys.CornerRadius] = new CornerRadius(7);
        d[ThemeKeys.BarCornerRadius] = new CornerRadius(3);
        return d;
    }

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}
