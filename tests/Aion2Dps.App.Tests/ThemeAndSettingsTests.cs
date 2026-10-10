using System.Windows;
using System.Windows.Media;
using Aion2Dps.App.Settings;
using Aion2Dps.App.Theming;
using Aion2Dps.Contracts;

namespace Aion2Dps.App.Tests;

public class ThemeManagerTests
{
    private static readonly string[] BrushKeys =
    [
        ThemeKeys.WindowBackground, ThemeKeys.Surface, ThemeKeys.SurfaceAlt, ThemeKeys.Border, ThemeKeys.Text, ThemeKeys.TextMuted,
        ThemeKeys.Accent, ThemeKeys.AccentText, ThemeKeys.Positive, ThemeKeys.Negative, ThemeKeys.Warning, ThemeKeys.Crit, ThemeKeys.Dot,
        ThemeKeys.Heal, ThemeKeys.BarTrack, ThemeKeys.BarFill, ThemeKeys.HpHigh, ThemeKeys.HpMid, ThemeKeys.HpLow,
    ];

    public static TheoryData<string> ThemeIds()
    {
        var d = new TheoryData<string>();
        foreach (var t in ThemeCatalog.All) d.Add(t.Id);
        return d;
    }

    [Fact]
    public void Ships_a_default_plus_six_themes_with_unique_ids()
    {
        Assert.Equal(7, ThemeCatalog.All.Count);
        Assert.Equal(ThemeCatalog.All.Count, ThemeCatalog.All.Select(t => t.Id).Distinct().Count());
        Assert.Equal(ThemeCatalog.DefaultId, ThemeCatalog.All[0].Id);
        Assert.Same(ThemeCatalog.Obsidian, ThemeCatalog.Get("nope"));
    }

    [Fact]
    public void Required_keys_cover_every_ThemeKeys_constant_and_class()
    {
        var keys = ThemeManager.RequiredKeys().ToList();
        foreach (var k in BrushKeys) Assert.Contains(k, keys);
        Assert.Contains(ThemeKeys.FontFamily, keys);
        Assert.Contains(ThemeKeys.MonoFontFamily, keys);
        Assert.Contains(ThemeKeys.FontSize, keys);
        Assert.Contains(ThemeKeys.CornerRadius, keys);
        Assert.Contains(ThemeKeys.BarCornerRadius, keys);
        foreach (var c in Enum.GetValues<CharacterClass>()) Assert.Contains(ThemeKeys.ClassBrush(c), keys);
        Assert.DoesNotContain(ThemeKeys.ClassBrushPrefix, keys);
    }

    [Theory]
    [MemberData(nameof(ThemeIds))]
    public void Every_theme_defines_every_key_with_the_right_type(string id) => Sta.Run(() =>
    {
        var d = ThemeManager.BuildPalette(ThemeCatalog.Get(id));
        foreach (var key in ThemeManager.RequiredKeys()) Assert.True(d.Contains(key), $"{id} is missing {key}");
        foreach (var key in BrushKeys) Assert.IsAssignableFrom<Brush>(d[key]);
        foreach (var c in Enum.GetValues<CharacterClass>()) Assert.IsAssignableFrom<SolidColorBrush>(d[ThemeKeys.ClassBrush(c)]);
        Assert.IsType<FontFamily>(d[ThemeKeys.FontFamily]);
        Assert.IsType<FontFamily>(d[ThemeKeys.MonoFontFamily]);
        Assert.IsType<double>(d[ThemeKeys.FontSize]);
        Assert.IsType<CornerRadius>(d[ThemeKeys.CornerRadius]);
        Assert.IsType<CornerRadius>(d[ThemeKeys.BarCornerRadius]);
        foreach (var k in new[] { AppThemeKeys.HeaderBackground, AppThemeKeys.DashboardBackground, AppThemeKeys.PopupBackground, AppThemeKeys.SidebarBackground, AppThemeKeys.AccentSoft, AppThemeKeys.Divider })
            Assert.IsAssignableFrom<Brush>(d[k]);
        // Dashboard background must be opaque (windows cannot be translucent).
        Assert.Equal(255, ((SolidColorBrush)d[AppThemeKeys.DashboardBackground]).Color.A);
    });

    [Fact]
    public void Text_contrasts_with_background_in_every_theme() => Sta.Run(() =>
    {
        foreach (var t in ThemeCatalog.All)
        {
            double lt = ColorUtil.Luminance(t.Text), lb = ColorUtil.Luminance(t.DashboardBackground);
            double ratio = (Math.Max(lt, lb) + 0.05) / (Math.Min(lt, lb) + 0.05);
            Assert.True(ratio >= 7, $"{t.Id}: contrast {ratio:0.0}");
        }
    });

    [Fact]
    public void Custom_overrides_apply() => Sta.Run(() =>
    {
        var custom = new AppearanceSettings { Accent = "#FF0000", Text = "#00FF00", CornerRadius = 12, FontFamily = "Consolas", ColorMode = RowColorMode.SingleColor, SingleColor = "#123456" };
        var d = ThemeManager.BuildPalette(ThemeCatalog.Obsidian, custom);
        Assert.Equal(Colors.Red, ((SolidColorBrush)d[ThemeKeys.Accent]).Color);
        Assert.Equal(Color.FromRgb(0, 255, 0), ((SolidColorBrush)d[ThemeKeys.Text]).Color);
        Assert.Equal(new CornerRadius(12), d[ThemeKeys.CornerRadius]);
        Assert.StartsWith("Consolas", ((FontFamily)d[ThemeKeys.FontFamily]).Source);
        foreach (var c in Enum.GetValues<CharacterClass>())
            Assert.Equal(Color.FromRgb(0x12, 0x34, 0x56), ((SolidColorBrush)d[ThemeKeys.ClassBrush(c)]).Color);
    });

    [Fact]
    public void Background_override_keeps_theme_translucency() => Sta.Run(() =>
    {
        var d = ThemeManager.BuildPalette(ThemeCatalog.Glacier, new AppearanceSettings { Background = "#202020" });
        var bg = ((SolidColorBrush)d[ThemeKeys.WindowBackground]).Color;
        Assert.Equal(ThemeCatalog.Glacier.WindowBackground.A, bg.A);
        Assert.Equal(0x20, bg.R);
        var explicitAlpha = ThemeManager.BuildPalette(ThemeCatalog.Glacier, new AppearanceSettings { Background = "#80202020" });
        Assert.Equal(0x80, ((SolidColorBrush)explicitAlpha[ThemeKeys.WindowBackground]).Color.A);
    });

    [Fact]
    public void Class_colours_default_to_ClassInfo_palette() => Sta.Run(() =>
    {
        var d = ThemeManager.BuildPalette(ThemeCatalog.Obsidian);
        Assert.Equal(ColorUtil.Parse(ClassInfo.DefaultColor(CharacterClass.Gladiator)), ((SolidColorBrush)d[ThemeKeys.ClassBrush(CharacterClass.Gladiator)]).Color);
    });

    [Fact]
    public void Apply_swaps_the_palette_in_place() => Sta.Run(() =>
    {
        var res = new ResourceDictionary();
        ThemeManager.Apply(res, ThemeCatalog.Obsidian, null);
        ThemeManager.Apply(res, ThemeCatalog.Daybreak, null);
        ThemeManager.Apply(res, ThemeCatalog.Arcade, new AppearanceSettings { ThemeId = "arcade" });
        Assert.Single(res.MergedDictionaries, m => m.Contains(AppThemeKeys.PaletteMarker));
        Assert.Single(res.MergedDictionaries.OfType<ControlStyles>());
        Assert.Equal("arcade", res[AppThemeKeys.ThemeId]);
        Assert.Equal(ThemeCatalog.Arcade.Accent, ((SolidColorBrush)res[ThemeKeys.Accent]).Color);
        Assert.Same(ThemeCatalog.Arcade, ThemeManager.CurrentTheme);
    });

    [Fact]
    public void Control_styles_load_without_an_application() => Sta.Run(() =>
    {
        var styles = new ControlStyles();
        Assert.True(styles.Contains("Style.IconButton"));
        Assert.True(styles.Contains(typeof(System.Windows.Controls.Button)));
    });

    [Theory]
    [InlineData("#FFF", 255, 255, 255, 255)]
    [InlineData("#102030", 255, 0x10, 0x20, 0x30)]
    [InlineData("80102030", 0x80, 0x10, 0x20, 0x30)]
    public void Colour_parsing(string text, byte a, byte r, byte g, byte b)
    {
        Assert.True(ColorUtil.TryParse(text, out var c));
        Assert.Equal(Color.FromArgb(a, r, g, b), c);
    }

    [Fact]
    public void Colour_parsing_rejects_garbage()
    {
        Assert.False(ColorUtil.TryParse("#GG0000", out _));
        Assert.False(ColorUtil.TryParse("", out _));
        Assert.False(ColorUtil.TryParse(null, out _));
    }
}

public class SettingsTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), "aion2dps-tests", Guid.NewGuid().ToString("N"), "settings.json");

    [Fact]
    public void Round_trips_every_section()
    {
        var path = TempFile();
        var s = new AppSettings();
        s.Overlay.Left = 123;
        s.Overlay.Top = 45;
        s.Overlay.Width = 512;
        s.Overlay.BackgroundOpacity = 0.6;
        s.Overlay.Locked = true;
        s.Overlay.ClickThrough = true;
        s.Overlay.RowSize = RowSize.Micro;
        s.Overlay.View = MeterView.Taken;
        s.Overlay.BarMode = BarMode.ShareOfParty;
        s.Overlay.DefaultMode = MeterMode.AllTargets;
        s.Overlay.ShowCritRate = true;
        s.Appearance.ThemeId = "ember";
        s.Appearance.Accent = "#FF00FF";
        s.Appearance.ColorMode = RowColorMode.SingleColor;
        s.Appearance.BarStyle = BarStyle.Slim;
        s.Appearance.CornerRadius = 3;
        s.General.Language = GameLanguage.ChineseTraditional;
        s.General.AdapterOverride = "\\Device\\NPF_{X}";
        s.General.IdleTimeoutSeconds = 20;
        s.General.PartyOnly = true;
        s.Dashboard = new WindowBounds { Left = 10, Top = 20, Width = 900, Height = 600 };

        SettingsStore.SaveTo(path, s);
        var back = SettingsStore.LoadFrom(path);

        Assert.Equal(123, back.Overlay.Left);
        Assert.Equal(45, back.Overlay.Top);
        Assert.Equal(512, back.Overlay.Width);
        Assert.Equal(0.6, back.Overlay.BackgroundOpacity);
        Assert.True(back.Overlay.Locked && back.Overlay.ClickThrough);
        Assert.Equal(RowSize.Micro, back.Overlay.RowSize);
        Assert.Equal(MeterView.Taken, back.Overlay.View);
        Assert.Equal(BarMode.ShareOfParty, back.Overlay.BarMode);
        Assert.Equal(MeterMode.AllTargets, back.Overlay.DefaultMode);
        Assert.True(back.Overlay.ShowCritRate);
        Assert.Equal("ember", back.Appearance.ThemeId);
        Assert.Equal("#FF00FF", back.Appearance.Accent);
        Assert.Equal(RowColorMode.SingleColor, back.Appearance.ColorMode);
        Assert.Equal(BarStyle.Slim, back.Appearance.BarStyle);
        Assert.Equal(3, back.Appearance.CornerRadius);
        Assert.Equal(GameLanguage.ChineseTraditional, back.General.Language);
        Assert.Equal("\\Device\\NPF_{X}", back.General.AdapterOverride);
        Assert.Equal(20, back.General.IdleTimeoutSeconds);
        Assert.True(back.General.PartyOnly);
        Assert.Equal(900, back.Dashboard!.Width);
        // Enums are stored as names so the file stays readable/editable.
        Assert.Contains("\"Micro\"", File.ReadAllText(path));
    }

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var s = SettingsStore.LoadFrom(TempFile());
        Assert.Equal(ThemeCatalog.DefaultId, s.Appearance.ThemeId);
        Assert.Equal(RowSize.Compact, s.Overlay.RowSize);
    }

    [Fact]
    public void Corrupt_file_gives_defaults_and_keeps_a_backup()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not json");
        var s = SettingsStore.LoadFrom(path);
        Assert.Equal(new AppSettings().Overlay.Width, s.Overlay.Width);
        Assert.True(File.Exists(path + ".bad"));
    }

    [Fact]
    public void Normalize_clamps_hand_edited_values()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            { "Overlay": { "Width": 5, "BackgroundOpacity": 7, "MaxRows": 999 },
              "Appearance": { "ThemeId": "does-not-exist", "Accent": "bogus", "CornerRadius": 99 },
              "General": { "IdleTimeoutSeconds": 0 } }
            """);
        var s = SettingsStore.LoadFrom(path);
        Assert.Equal(300, s.Overlay.Width);
        Assert.Equal(1, s.Overlay.BackgroundOpacity);
        Assert.Equal(24, s.Overlay.MaxRows);
        Assert.Equal(ThemeCatalog.DefaultId, s.Appearance.ThemeId);
        Assert.Null(s.Appearance.Accent);
        Assert.Equal(16, s.Appearance.CornerRadius);
        Assert.Equal(3, s.General.IdleTimeoutSeconds);
    }

    [Fact]
    public void Version_1_files_move_from_the_old_10_row_default_to_the_top_5()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            { "Version": 1, "Overlay": { "MaxRows": 10 } }
            """);
        var s = SettingsStore.LoadFrom(path);
        Assert.Equal(5, s.Overlay.MaxRows);
        Assert.Equal(AppSettings.CurrentVersion, s.Version);

        // A value the user picked (not the old default) stays, and a current file is never migrated again.
        File.WriteAllText(path, """
            { "Version": 1, "Overlay": { "MaxRows": 8 } }
            """);
        Assert.Equal(8, SettingsStore.LoadFrom(path).Overlay.MaxRows);
        File.WriteAllText(path, """
            { "Version": 2, "Overlay": { "MaxRows": 10 } }
            """);
        Assert.Equal(10, SettingsStore.LoadFrom(path).Overlay.MaxRows);
        Assert.Equal(5, new AppSettings().Overlay.MaxRows);
    }

    [Fact]
    public void Store_save_and_load()
    {
        var path = TempFile();
        var store = new SettingsStore(path);
        store.Load();
        store.Current.Overlay.Width = 444;
        store.NotifyChanged(saveImmediately: true);
        var again = new SettingsStore(path);
        Assert.Equal(444, again.Load().Overlay.Width);
    }
}
