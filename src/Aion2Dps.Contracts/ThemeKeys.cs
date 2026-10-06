namespace Aion2Dps.Contracts;

/// <summary>
/// WPF resource keys shared by every UI project. The App defines one ResourceDictionary per theme with ALL of these
/// keys and swaps it at Application level at runtime. UI controls must reference them with <c>{DynamicResource}</c>
/// and must NOT merge their own theme dictionaries into control resources (that would shadow the app theme).
/// </summary>
public static class ThemeKeys
{
    // Brushes
    public const string WindowBackground = "Theme.WindowBackground"; // overlay/window background (may be translucent)
    public const string Surface = "Theme.Surface";                   // panels, cards, popups
    public const string SurfaceAlt = "Theme.SurfaceAlt";             // alternating rows, hover
    public const string Border = "Theme.Border";
    public const string Text = "Theme.Text";
    public const string TextMuted = "Theme.TextMuted";
    public const string Accent = "Theme.Accent";
    public const string AccentText = "Theme.AccentText";             // text drawn on Accent
    public const string Positive = "Theme.Positive";
    public const string Negative = "Theme.Negative";
    public const string Warning = "Theme.Warning";
    public const string Crit = "Theme.Crit";
    public const string Dot = "Theme.Dot";
    public const string Heal = "Theme.Heal";
    public const string BarTrack = "Theme.BarTrack";                 // empty part of bars
    public const string BarFill = "Theme.BarFill";                   // bar fill when not coloring by class
    public const string HpHigh = "Theme.HpHigh";
    public const string HpMid = "Theme.HpMid";
    public const string HpLow = "Theme.HpLow";

    // Non-brush values
    public const string FontFamily = "Theme.FontFamily";             // System.Windows.Media.FontFamily
    public const string MonoFontFamily = "Theme.MonoFontFamily";     // numbers
    public const string FontSize = "Theme.FontSize";                 // double (base size)
    public const string CornerRadius = "Theme.CornerRadius";         // System.Windows.CornerRadius
    public const string BarCornerRadius = "Theme.BarCornerRadius";   // System.Windows.CornerRadius

    /// <summary>Per-class brush: <c>ClassBrushPrefix + CharacterClass.ToString()</c>, e.g. "Theme.Class.Gladiator"
    /// (and "Theme.Class.Unknown").</summary>
    public const string ClassBrushPrefix = "Theme.Class.";

    public static string ClassBrush(CharacterClass c) => ClassBrushPrefix + c;
}
