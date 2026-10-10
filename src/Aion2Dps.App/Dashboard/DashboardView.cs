using Aion2Dps.App.Controls;
using Aion2Dps.App.Infrastructure;
using Aion2Dps.App.Theming;

namespace Aion2Dps.App.Dashboard;

/// <summary>The dashboard shell (sidebar + page host). Hosted by <see cref="DashboardWindow"/>; renderable offscreen.</summary>
public sealed class DashboardView : UserControl
{
    private readonly DashboardContext _context;
    private readonly ListBox _nav = new();
    private readonly ContentControl _host = new();
    private readonly List<DashboardPage> _pages;
    private readonly DispatcherTimer _timer;
    private readonly System.Windows.Shapes.Ellipse _statusDot = Ui.Dot(8, ThemeKeys.TextMuted);
    private readonly TextBlock _statusText = Ui.Text("", ThemeKeys.TextMuted, 11.5);

    public DashboardView(DashboardContext context)
    {
        _context = context;
        context.Navigate = Navigate;
        this.Ref(FontFamilyProperty, ThemeKeys.FontFamily);
        this.Ref(ForegroundProperty, ThemeKeys.Text);
        this.Ref(BackgroundProperty, AppThemeKeys.DashboardBackground);
        FontSize = 12.5;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        UseLayoutRounding = true;

        _pages =
        [
            new MeterPage(context),
            new TimersPage(context),
            new HistoryPage(context),
            new TrendsPage(context),
            new CharacterPage(context),
            new AppearancePage(context),
            new SettingsPage(context),
            new AboutPage(context),
        ];

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Sidebar
        var sidebar = new DockPanel { LastChildFill = true };
        var sideBg = new Border { Child = sidebar };
        sideBg.Ref(Border.BackgroundProperty, AppThemeKeys.SidebarBackground);
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 20, 12, 22) };
        brand.Children.Add(new Image { Source = AppIcon.ImageSource, Width = 30, Height = 30 });
        var brandText = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        brandText.Children.Add(Ui.Text("Aion2Dps", ThemeKeys.Text, 16, FontWeights.Bold));
        brandText.Children.Add(Ui.Text("personal combat meter", ThemeKeys.TextMuted, 10.5));
        brand.Children.Add(brandText);
        DockPanel.SetDock(brand, Dock.Top);
        sidebar.Children.Add(brand);

        var status = new StackPanel { Margin = new Thickness(18, 10, 12, 16) };
        var statusLine = new StackPanel { Orientation = Orientation.Horizontal };
        _statusDot.Margin = new Thickness(0, 0, 7, 0);
        statusLine.Children.Add(_statusDot);
        statusLine.Children.Add(_statusText);
        status.Children.Add(statusLine);
        var ver = Ui.Text($"v{AppPaths.Version} · {context.Services.ModeLabel}", ThemeKeys.TextMuted, 10.5);
        ver.Margin = new Thickness(15, 3, 0, 0);
        status.Children.Add(ver);
        DockPanel.SetDock(status, Dock.Bottom);
        sidebar.Children.Add(status);

        foreach (var p in _pages)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = Ui.Icon(p.Icon, ThemeKeys.TextMuted, 14);
            icon.Width = 26;
            icon.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1) });
            sp.Children.Add(icon);
            sp.Children.Add(new TextBlock { Text = p.Title, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            var item = new ListBoxItem { Content = sp, Tag = p };
            item.SetResourceReference(StyleProperty, "Style.NavItem");
            _nav.Items.Add(item);
        }
        _nav.SelectionChanged += (_, _) => ShowSelected();
        sidebar.Children.Add(_nav);
        root.Children.Add(sideBg);

        // Page host
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _host };
        _host.Margin = new Thickness(28, 22, 28, 22);
        Grid.SetColumn(scroll, 1);
        root.Children.Add(scroll);
        Content = root;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshNow();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
        _nav.SelectedIndex = 0;
    }

    public IReadOnlyList<DashboardPage> Pages => _pages;

    public DashboardPage? Current => _host.Content as DashboardPage;

    /// <summary>Selects a page by key ("Meter", "History", "Trends", "Character", "Appearance", "Settings", "About").</summary>
    public void Navigate(string? key)
    {
        if (key is null) return;
        for (int i = 0; i < _pages.Count; i++)
            if (string.Equals(_pages[i].Key, key, StringComparison.OrdinalIgnoreCase)) { _nav.SelectedIndex = i; return; }
    }

    private void ShowSelected()
    {
        if (_nav.SelectedItem is not ListBoxItem { Tag: DashboardPage page }) return;
        _host.Content = page;
        try { page.OnShown(); page.Refresh(); }
        catch (Exception ex) { AppLog.Error("Dashboard", $"Page {page.Key} failed", ex); }
    }

    public void RefreshNow()
    {
        try
        {
            var cs = _context.Services.Capture.Status;
            _statusText.Text = cs.State switch
            {
                CaptureState.Capturing => "Capturing",
                CaptureState.Replaying => "Replaying",
                CaptureState.Detecting => "Detecting game…",
                CaptureState.WaitingForGame => "Waiting for game",
                CaptureState.NpcapMissing => "Npcap missing",
                CaptureState.Error => "Capture error",
                _ => "Stopped",
            };
            _statusDot.Ref(System.Windows.Shapes.Shape.FillProperty, cs.State switch
            {
                CaptureState.Capturing or CaptureState.Replaying => ThemeKeys.Positive,
                CaptureState.Detecting or CaptureState.WaitingForGame => ThemeKeys.Warning,
                CaptureState.NpcapMissing or CaptureState.Error => ThemeKeys.Negative,
                _ => ThemeKeys.TextMuted,
            });
            Current?.Refresh();
        }
        catch (Exception ex)
        {
            AppLog.Error("Dashboard", "Refresh failed", ex);
        }
    }
}
