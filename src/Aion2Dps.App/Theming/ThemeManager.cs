using Aion2Dps.App.Settings;

namespace Aion2Dps.App.Theming;

/// <summary>App-private resource keys (in addition to the shared <see cref="ThemeKeys"/>).</summary>
public static class AppThemeKeys
{
    /// <summary>Overlay header band (vertical gradient brush).</summary>
    public const string HeaderBackground = "App.HeaderBackground";
    /// <summary>Opaque window background for the dashboard.</summary>
    public const string DashboardBackground = "App.DashboardBackground";
    /// <summary>Opaque background for popups, menus and tooltips.</summary>
    public const string PopupBackground = "App.PopupBackground";
    public const string SidebarBackground = "App.SidebarBackground";
    /// <summary>Accent at low alpha (selected nav item, chips).</summary>
    public const string AccentSoft = "App.AccentSoft";
    public const string Divider = "App.Divider";
    /// <summary>Boolean: the theme has a dark background.</summary>
    public const string IsDark = "App.IsDark";
    /// <summary>Marker key identifying a palette dictionary produced by <see cref="ThemeManager"/>.</summary>
    public const string PaletteMarker = "App.PaletteMarker";
    /// <summary>String id of the theme that produced the palette.</summary>
    public const string ThemeId = "App.ThemeId";
}

/// <summary>
/// Generates a ResourceDictionary containing every <see cref="ThemeKeys"/> key (plus class brushes for all
/// <see cref="CharacterClass"/> values and the <see cref="AppThemeKeys"/>) from a <see cref="ThemeDefinition"/> and the
/// user's custom overrides, and swaps it into Application.Resources at runtime. UI uses {DynamicResource} so the swap
/// re-themes every open window instantly.
/// </summary>
public static class ThemeManager
{
    public static ThemeDefinition CurrentTheme { get; private set; } = ThemeCatalog.Obsidian;
    public static AppearanceSettings CurrentAppearance { get; private set; } = new();

    /// <summary>Bar style currently in effect (rows read it when they are created/updated).</summary>
    public static BarStyle BarStyle => CurrentAppearance.BarStyle;

    /// <summary>Raised on the UI thread after a theme is applied.</summary>
    public static event Action? ThemeChanged;

    /// <summary>All keys every palette dictionary must contain.</summary>
    public static IEnumerable<string> RequiredKeys()
    {
        foreach (var f in typeof(ThemeKeys).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            if (f.IsLiteral && f.FieldType == typeof(string) && f.Name != nameof(ThemeKeys.ClassBrushPrefix))
                yield return (string)f.GetRawConstantValue()!;
        foreach (CharacterClass c in Enum.GetValues<CharacterClass>())
            yield return ThemeKeys.ClassBrush(c);
    }

    /// <summary>Applies custom overrides to a theme (background/accent/text, corner radius, font).</summary>
    public static ThemeDefinition Effective(ThemeDefinition theme, AppearanceSettings? custom)
    {
        if (custom is null) return theme;
        var t = theme;
        if (ColorUtil.TryParse(custom.Background, out var bg))
        {
            // Keep the theme's translucency unless the user gave an explicit alpha.
            bool explicitAlpha = custom.Background!.Trim().TrimStart('#').Length == 8;
            var win = explicitAlpha ? bg : ColorUtil.WithAlpha(bg, theme.WindowBackground.A);
            var opaque = ColorUtil.WithAlpha(bg, (byte)255);
            bool dark = ColorUtil.IsDark(opaque);
            var lift = dark ? Colors.White : Colors.Black;
            t = t with
            {
                WindowBackground = win,
                DashboardBackground = opaque,
                Surface = ColorUtil.Blend(opaque, lift, 0.05),
                SurfaceAlt = ColorUtil.Blend(opaque, lift, 0.10),
                HeaderTop = ColorUtil.WithAlpha(ColorUtil.Blend(opaque, lift, 0.08), theme.HeaderTop.A),
                HeaderBottom = ColorUtil.WithAlpha(opaque, (byte)0),
                IsDark = dark,
                BarTrack = ColorUtil.WithAlpha(lift, dark ? 0.12 : 0.08),
            };
        }
        if (ColorUtil.TryParse(custom.Accent, out var accent))
        {
            t = t with
            {
                Accent = accent,
                BarFill = accent,
                AccentText = ColorUtil.ContrastText(accent),
                Border = custom.Background is null ? t.Border : ColorUtil.Blend(t.DashboardBackground, accent, 0.35),
            };
        }
        if (ColorUtil.TryParse(custom.Text, out var text))
        {
            var basis = ColorUtil.WithAlpha(t.DashboardBackground, (byte)255);
            t = t with { Text = text, TextMuted = ColorUtil.Blend(text, basis, 0.38) };
        }
        if (custom.CornerRadius is { } cr)
            t = t with { CornerRadius = cr, BarCornerRadius = Math.Min(4, Math.Round(cr / 3)) };
        if (!string.IsNullOrWhiteSpace(custom.FontFamily))
            t = t with { FontFamily = custom.FontFamily! };
        if (custom.FontSize is { } fs)
            t = t with { FontSize = fs };
        return t;
    }

    /// <summary>Builds the palette dictionary (all theme keys, no control styles).</summary>
    public static ResourceDictionary BuildPalette(ThemeDefinition theme, AppearanceSettings? custom = null)
    {
        var t = Effective(theme, custom);
        var d = new ResourceDictionary
        {
            [AppThemeKeys.PaletteMarker] = true,
            [AppThemeKeys.ThemeId] = theme.Id,
            [ThemeKeys.WindowBackground] = ColorUtil.Brush(t.WindowBackground),
            [ThemeKeys.Surface] = ColorUtil.Brush(t.Surface),
            [ThemeKeys.SurfaceAlt] = ColorUtil.Brush(t.SurfaceAlt),
            [ThemeKeys.Border] = ColorUtil.Brush(t.Border),
            [ThemeKeys.Text] = ColorUtil.Brush(t.Text),
            [ThemeKeys.TextMuted] = ColorUtil.Brush(t.TextMuted),
            [ThemeKeys.Accent] = ColorUtil.Brush(t.Accent),
            [ThemeKeys.AccentText] = ColorUtil.Brush(t.AccentText),
            [ThemeKeys.Positive] = ColorUtil.Brush(t.Positive),
            [ThemeKeys.Negative] = ColorUtil.Brush(t.Negative),
            [ThemeKeys.Warning] = ColorUtil.Brush(t.Warning),
            [ThemeKeys.Crit] = ColorUtil.Brush(t.Crit),
            [ThemeKeys.Dot] = ColorUtil.Brush(t.Dot),
            [ThemeKeys.Heal] = ColorUtil.Brush(t.Heal),
            [ThemeKeys.BarTrack] = ColorUtil.Brush(t.BarTrack),
            [ThemeKeys.BarFill] = ColorUtil.Brush(t.BarFill),
            [ThemeKeys.HpHigh] = ColorUtil.Brush(t.HpHigh),
            [ThemeKeys.HpMid] = ColorUtil.Brush(t.HpMid),
            [ThemeKeys.HpLow] = ColorUtil.Brush(t.HpLow),
            [ThemeKeys.FontFamily] = new FontFamily(t.FontFamily + ", Segoe UI"),
            [ThemeKeys.MonoFontFamily] = new FontFamily(t.MonoFontFamily + ", Segoe UI"),
            [ThemeKeys.FontSize] = t.FontSize,
            [ThemeKeys.CornerRadius] = new CornerRadius(t.CornerRadius),
            [ThemeKeys.BarCornerRadius] = new CornerRadius(t.BarCornerRadius),

            [AppThemeKeys.DashboardBackground] = ColorUtil.Brush(t.DashboardBackground),
            [AppThemeKeys.PopupBackground] = ColorUtil.Brush(ColorUtil.Blend(t.DashboardBackground, t.IsDark ? Colors.White : Colors.Black, 0.04)),
            [AppThemeKeys.SidebarBackground] = ColorUtil.Brush(ColorUtil.Blend(t.DashboardBackground, t.IsDark ? Colors.Black : Colors.White, t.IsDark ? 0.25 : 0.5)),
            [AppThemeKeys.AccentSoft] = ColorUtil.Brush(ColorUtil.WithAlpha(t.Accent, 0.18)),
            [AppThemeKeys.Divider] = ColorUtil.Brush(ColorUtil.WithAlpha(t.Text, t.IsDark ? 0.10 : 0.12)),
            [AppThemeKeys.IsDark] = t.IsDark,
        };
        var header = new LinearGradientBrush(t.HeaderTop, t.HeaderBottom, 90);
        header.Freeze();
        d[AppThemeKeys.HeaderBackground] = header;

        var mode = custom?.ColorMode ?? RowColorMode.ClassColors;
        Color single = ColorUtil.TryParse(custom?.SingleColor, out var sc) ? sc : t.BarFill;
        foreach (CharacterClass c in Enum.GetValues<CharacterClass>())
        {
            var color = mode switch
            {
                RowColorMode.SingleColor => single,
                RowColorMode.ThemeColor => t.BarFill,
                _ => t.ClassColor(c),
            };
            d[ThemeKeys.ClassBrush(c)] = ColorUtil.Brush(color);
        }
        return d;
    }

    /// <summary>Palette + control styles: a self-contained dictionary for offscreen roots and previews.</summary>
    public static ResourceDictionary BuildFull(ThemeDefinition theme, AppearanceSettings? custom = null)
    {
        var d = new ResourceDictionary();
        d.MergedDictionaries.Add(new ControlStyles());
        d.MergedDictionaries.Add(BuildPalette(theme, custom));
        return d;
    }

    /// <summary>Swaps the palette in <paramref name="resources"/> (normally Application.Current.Resources).</summary>
    public static void Apply(ResourceDictionary resources, ThemeDefinition theme, AppearanceSettings? custom)
    {
        if (!resources.MergedDictionaries.OfType<ControlStyles>().Any())
        {
            resources.MergedDictionaries.Insert(0, new ControlStyles());
        }
        var palette = BuildPalette(theme, custom);
        var old = resources.MergedDictionaries.FirstOrDefault(m => m.Contains(AppThemeKeys.PaletteMarker));
        if (old is not null)
        {
            int idx = resources.MergedDictionaries.IndexOf(old);
            resources.MergedDictionaries[idx] = palette;
        }
        else resources.MergedDictionaries.Add(palette);

        CurrentTheme = theme;
        CurrentAppearance = Clone(custom) ?? new AppearanceSettings();
        try { ThemeChanged?.Invoke(); }
        catch (Exception ex) { AppLog.Error("Theme", "ThemeChanged handler failed", ex); }
    }

    /// <summary>Applies the theme named in <paramref name="appearance"/> to the running application.</summary>
    public static void ApplyToApplication(AppearanceSettings appearance)
    {
        if (Application.Current is null) return;
        Apply(Application.Current.Resources, ThemeCatalog.Get(appearance.ThemeId), appearance);
    }

    private static AppearanceSettings? Clone(AppearanceSettings? a) => a is null ? null : new AppearanceSettings
    {
        ThemeId = a.ThemeId, ColorMode = a.ColorMode, SingleColor = a.SingleColor, Background = a.Background, Accent = a.Accent,
        Text = a.Text, CornerRadius = a.CornerRadius, FontFamily = a.FontFamily, FontSize = a.FontSize, BarStyle = a.BarStyle,
    };
}
