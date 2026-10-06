using Aion2Dps.App.Controls;
using Aion2Dps.App.Demo;
using Aion2Dps.App.Infrastructure;
using Aion2Dps.App.Overlay;
using Aion2Dps.App.Settings;
using Aion2Dps.App.Theming;

namespace Aion2Dps.App.Dashboard;

/// <summary>Theme gallery with live previews, colour/shape/type customisation, row and column options.</summary>
public sealed class AppearancePage : DashboardPage
{
    private static readonly string[] Fonts = ["Segoe UI", "Bahnschrift", "Calibri", "Trebuchet MS", "Verdana", "Tahoma", "Consolas", "Georgia"];

    private readonly OverlayView _preview = new() { AnimationsEnabled = false, Width = 360, IsHitTestVisible = false };
    private readonly WrapPanel _gallery = new();
    private readonly Dictionary<string, Border> _themeCards = new();
    private readonly MeterSnapshot _sample = PreviewData.LiveBoss();
    private readonly StackPanel _controls = new();

    public AppearancePage(DashboardContext context) : base(context)
    {
        var root = new StackPanel();
        root.Children.Add(Ui.PageTitle("Appearance"));
        root.Children.Add(Ui.PageSubtitle("Pick a theme and fine-tune it. Changes apply instantly to the overlay and every window."));

        // Gallery spans the full width; below it the controls and a sticky live preview.
        BuildGallery();
        root.Children.Add(Ui.Card("Theme", _gallery));

        var cols = new Grid();
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        BuildControls();
        cols.Children.Add(_controls);

        var previewHost = new StackPanel();
        var previewFrame = new Border { Padding = new Thickness(14), CornerRadius = new CornerRadius(6), Child = _preview };
        // A neutral "game scene" backdrop so translucency is visible.
        previewFrame.Background = Rendering.OffscreenRenderer.SceneBackdrop();
        previewHost.Children.Add(previewFrame);
        var hint = Ui.Text("Preview over a sample game scene", ThemeKeys.TextMuted, 11);
        hint.Margin = new Thickness(2, 6, 0, 0);
        previewHost.Children.Add(hint);
        var previewCard = Ui.Card("Live preview", previewHost);
        previewCard.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(previewCard, 2);
        cols.Children.Add(previewCard);
        root.Children.Add(cols);
        Content = root;

        context.Settings.Changed += _ => UpdatePreview();
        ThemeManager.ThemeChanged += () => Dispatcher.BeginInvoke(UpdatePreview);
        UpdatePreview();
    }

    public override string Key => "Appearance";
    public override string Title => "Appearance";
    public override string Icon => Ui.IconAppearance;

    private AppearanceSettings A => Context.Settings.Current.Appearance;
    private OverlaySettings O => Context.Settings.Current.Overlay;

    private void Changed(bool retheme = true)
    {
        if (retheme) ThemeManager.ApplyToApplication(A);
        Context.Settings.NotifyChanged();
        UpdatePreview();
        HighlightSelected();
    }

    private void UpdatePreview()
    {
        var opts = OverlayViewOptions.From(Context.Settings.Current, AppPaths.Version);
        _preview.Update(_sample, PreviewData.Status(), opts);
    }

    private void BuildGallery()
    {
        foreach (var theme in ThemeCatalog.All)
        {
            var mini = new OverlayView { AnimationsEnabled = false, Width = 360, IsHitTestVisible = false };
            var scene = new Border
            {
                Padding = new Thickness(10),
                Background = new LinearGradientBrush(Color.FromRgb(0x4A, 0x5E, 0x6E), Color.FromRgb(0x2B, 0x2A, 0x33), 60),
                CornerRadius = new CornerRadius(4),
                Child = new Viewbox { Child = mini, Stretch = Stretch.Uniform, Width = 160 },
            };
            var inner = new StackPanel();
            inner.Children.Add(scene);
            inner.Resources = ThemeManager.BuildFull(theme);
            // Update after the palette is in scope so the HP colour comes from this theme.
            mini.Update(_sample, PreviewData.Status(), new OverlayViewOptions { RowSize = RowSize.Compact, BarStyle = theme.DefaultBarStyle, ShowColumnHeader = false, MaxRows = 5, Version = AppPaths.Version });
            var text = new StackPanel { Margin = new Thickness(2, 8, 2, 2) };
            text.Children.Add(Ui.Text(theme.Name, ThemeKeys.Text, 13.5, FontWeights.SemiBold));
            var d = Ui.Text(theme.Description, ThemeKeys.TextMuted, 11);
            d.TextWrapping = TextWrapping.Wrap;
            d.TextTrimming = TextTrimming.None;
            d.Height = 30;
            text.Children.Add(d);
            var outer = new Border
            {
                Width = 200, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(9), CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(2), Cursor = Cursors.Hand, Child = new StackPanel { Children = { inner, text } },
                ToolTip = $"Use the {theme.Name} theme",
            };
            outer.Ref(Border.BackgroundProperty, ThemeKeys.SurfaceAlt);
            var id = theme.Id;
            outer.MouseLeftButtonUp += (_, _) =>
            {
                A.ThemeId = id;
                A.BarStyle = ThemeCatalog.Get(id).DefaultBarStyle;
                Changed();
                RebuildControls();
            };
            _themeCards[id] = outer;
            _gallery.Children.Add(outer);
        }
        HighlightSelected();
    }

    private void HighlightSelected()
    {
        foreach (var (id, card) in _themeCards)
        {
            if (string.Equals(id, A.ThemeId, StringComparison.OrdinalIgnoreCase)) card.Ref(Border.BorderBrushProperty, ThemeKeys.Accent);
            else card.BorderBrush = Brushes.Transparent;
        }
    }

    private void RebuildControls()
    {
        _controls.Children.Clear();
        BuildControls();
    }

    private void BuildControls()
    {
        // Colours
        var colours = new StackPanel();
        var modes = new WrapPanel();
        foreach (var (mode, label) in new[] { (RowColorMode.ClassColors, "Class colours"), (RowColorMode.SingleColor, "Single colour"), (RowColorMode.ThemeColor, "Theme accent") })
        {
            var rb = new RadioButton { Content = label, GroupName = "rowcolor", IsChecked = A.ColorMode == mode };
            var m = mode;
            rb.Checked += (_, _) => { A.ColorMode = m; Changed(); };
            modes.Children.Add(rb);
        }
        colours.Children.Add(Ui.Field("Row colours", modes));
        colours.Children.Add(Ui.Field("Single colour", new ColorField(A.SingleColor, ThemeKeys.BarFill, v => { A.SingleColor = v ?? "#D9A441"; if (A.ColorMode == RowColorMode.SingleColor) Changed(); else Context.Settings.NotifyChanged(); })));
        colours.Children.Add(Ui.Field("Background", new ColorField(A.Background, ThemeKeys.WindowBackground, v => { A.Background = v; Changed(); }), "Keeps the theme's transparency unless you enter an #AARRGGBB value."));
        colours.Children.Add(Ui.Field("Accent", new ColorField(A.Accent, ThemeKeys.Accent, v => { A.Accent = v; Changed(); })));
        colours.Children.Add(Ui.Field("Text", new ColorField(A.Text, ThemeKeys.Text, v => { A.Text = v; Changed(); })));
        _controls.Children.Add(Ui.Card("Colours", colours));

        // Shape & type
        var shape = new StackPanel();
        var theme = ThemeCatalog.Get(A.ThemeId);
        var radiusLabel = Ui.Text($"{A.CornerRadius ?? theme.CornerRadius:0} px", ThemeKeys.TextMuted, 12);
        var radius = Ui.Slider(0, 16, A.CornerRadius ?? theme.CornerRadius, v => { A.CornerRadius = Math.Round(v); radiusLabel.Text = $"{Math.Round(v):0} px"; Changed(); }, 220, 1);
        shape.Children.Add(Ui.Field("Corner roundness", Row(radius, radiusLabel)));
        var fontItems = new List<(string?, string)> { (null, $"Theme default ({theme.FontFamily})") };
        fontItems.AddRange(Fonts.Select(f => ((string?)f, f)));
        shape.Children.Add(Ui.Field("Font", Ui.Combo(fontItems, A.FontFamily, v => { A.FontFamily = v; Changed(); }, 240)));
        var sizeLabel = Ui.Text($"{A.FontSize ?? theme.FontSize:0.#} pt", ThemeKeys.TextMuted, 12);
        var size = Ui.Slider(10, 15, A.FontSize ?? theme.FontSize, v => { A.FontSize = Math.Round(v * 2) / 2; sizeLabel.Text = $"{A.FontSize:0.#} pt"; Changed(); }, 220, 0.5);
        shape.Children.Add(Ui.Field("Base font size (dashboard)", Row(size, sizeLabel)));
        var bars = new WrapPanel();
        foreach (var (style, label) in new[] { (BarStyle.Gradient, "Gradient"), (BarStyle.Solid, "Solid"), (BarStyle.Slim, "Slim underline") })
        {
            var rb = new RadioButton { Content = label, GroupName = "barstyle", IsChecked = A.BarStyle == style };
            var s = style;
            rb.Checked += (_, _) => { A.BarStyle = s; Changed(retheme: false); };
            bars.Children.Add(rb);
        }
        shape.Children.Add(Ui.Field("Bar style", bars));
        _controls.Children.Add(Ui.Card("Shape & type", shape));

        // Rows & columns
        var rows = new StackPanel();
        var sizes = new WrapPanel();
        foreach (var (rs, label) in new[] { (RowSize.Normal, "Normal"), (RowSize.Compact, "Compact"), (RowSize.Micro, "Micro (name + DPS)") })
        {
            var rb = new RadioButton { Content = label, GroupName = "rowsize", IsChecked = O.RowSize == rs };
            var r = rs;
            rb.Checked += (_, _) => { O.RowSize = r; Changed(retheme: false); };
            sizes.Children.Add(rb);
        }
        rows.Children.Add(Ui.Field("Row size", sizes));
        var colsPanel = new WrapPanel();
        void Col(string label, Func<bool> get, Action<bool> set)
        {
            var c = Ui.Check(label, get(), v => { set(v); Changed(retheme: false); });
            c.Margin = new Thickness(0, 3, 18, 3);
            colsPanel.Children.Add(c);
        }
        Col("Total damage", () => O.ShowTotal, v => O.ShowTotal = v);
        Col("Contribution %", () => O.ShowContribution, v => O.ShowContribution = v);
        Col("Crit %", () => O.ShowCritRate, v => O.ShowCritRate = v);
        Col("Max hit", () => O.ShowMaxHit, v => O.ShowMaxHit = v);
        Col("Rank", () => O.ShowRank, v => O.ShowRank = v);
        Col("Class emblem", () => O.ShowClassEmblem, v => O.ShowClassEmblem = v);
        Col("Column header", () => O.ShowColumnHeader, v => O.ShowColumnHeader = v);
        rows.Children.Add(Ui.Field("Columns", colsPanel, "DPS is always shown; Micro rows show name, DPS and contribution."));
        var opLabel = Ui.Text(Formatting.Fmt.Percent(O.BackgroundOpacity, 0), ThemeKeys.TextMuted, 12);
        var op = Ui.Slider(0.15, 1, O.BackgroundOpacity, v => { O.BackgroundOpacity = Math.Round(v, 2); opLabel.Text = Formatting.Fmt.Percent(v, 0); Changed(retheme: false); }, 200);
        rows.Children.Add(Ui.Field("Overlay background opacity", Row(op, opLabel)));
        _controls.Children.Add(Ui.Card("Rows & columns", rows));

        var reset = Ui.Button("Reset appearance to defaults", () =>
        {
            Context.Settings.Current.Appearance = new AppearanceSettings();
            var fresh = new OverlaySettings();
            O.RowSize = fresh.RowSize;
            O.ShowTotal = fresh.ShowTotal;
            O.ShowContribution = fresh.ShowContribution;
            O.ShowCritRate = fresh.ShowCritRate;
            O.ShowMaxHit = fresh.ShowMaxHit;
            O.ShowRank = fresh.ShowRank;
            O.ShowClassEmblem = fresh.ShowClassEmblem;
            O.ShowColumnHeader = fresh.ShowColumnHeader;
            O.BackgroundOpacity = fresh.BackgroundOpacity;
            Changed();
            RebuildControls();
        }, icon: Ui.IconReset);
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        _controls.Children.Add(reset);
    }

    private static StackPanel Row(FrameworkElement a, FrameworkElement b)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(a);
        b.Margin = new Thickness(12, 0, 0, 0);
        sp.Children.Add(b);
        return sp;
    }
}
