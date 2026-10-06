using Aion2Dps.App.Settings;

namespace Aion2Dps.App.Theming;

/// <summary>A complete theme palette. Every <see cref="ThemeKeys"/> value is derived from these fields.</summary>
public sealed record ThemeDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public bool IsDark { get; init; } = true;

    public required Color WindowBackground { get; init; }
    public required Color Surface { get; init; }
    public required Color SurfaceAlt { get; init; }
    public required Color Border { get; init; }
    public required Color Text { get; init; }
    public required Color TextMuted { get; init; }
    public required Color Accent { get; init; }
    public required Color AccentText { get; init; }
    public required Color Positive { get; init; }
    public required Color Negative { get; init; }
    public required Color Warning { get; init; }
    public required Color Crit { get; init; }
    public required Color Dot { get; init; }
    public required Color Heal { get; init; }
    public required Color BarTrack { get; init; }
    public required Color BarFill { get; init; }
    public required Color HpHigh { get; init; }
    public required Color HpMid { get; init; }
    public required Color HpLow { get; init; }

    /// <summary>Top/bottom of the overlay header band gradient.</summary>
    public required Color HeaderTop { get; init; }
    public required Color HeaderBottom { get; init; }
    /// <summary>Opaque background for the dashboard and popups.</summary>
    public required Color DashboardBackground { get; init; }

    public string FontFamily { get; init; } = "Segoe UI";
    public string MonoFontFamily { get; init; } = "Bahnschrift";
    public double FontSize { get; init; } = 12;
    public double CornerRadius { get; init; } = 6;
    public double BarCornerRadius { get; init; } = 2;
    /// <summary>Bar style the theme looks best with (used when the user resets appearance).</summary>
    public BarStyle DefaultBarStyle { get; init; } = BarStyle.Gradient;
    /// <summary>Theme-specific class colours (others fall back to <see cref="ClassInfo.DefaultColor"/>).</summary>
    public IReadOnlyDictionary<CharacterClass, Color> ClassColors { get; init; } = new Dictionary<CharacterClass, Color>();

    public Color ClassColor(CharacterClass c) =>
        ClassColors.TryGetValue(c, out var col) ? col : ColorUtil.Parse(ClassInfo.DefaultColor(c));
}
