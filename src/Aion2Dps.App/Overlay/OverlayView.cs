using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Aion2Dps.App.Controls;
using Aion2Dps.App.Formatting;
using Aion2Dps.App.Settings;
using Aion2Dps.App.Theming;

namespace Aion2Dps.App.Overlay;

/// <summary>
/// The overlay content (no window): header band with target crest, name, timer, HP bar and toolbar; party or PvP
/// rows updated in place; empty-state panels; toast; footer with status, ping and min·avg·max. Pure view: it renders a
/// <see cref="MeterSnapshot"/> + <see cref="OverlayStatus"/> and raises events for every button. Can be rendered offscreen.
/// </summary>
public sealed class OverlayView : UserControl
{
    // ── Layout parts ──
    private readonly Border _bg = new();
    private readonly Border _frame = new() { BorderThickness = new Thickness(1), IsHitTestVisible = false };
    private readonly Border _header = new() { Padding = new Thickness(8, 7, 6, 5) };
    private readonly TargetEmblem _emblem = new(38);
    private readonly TextBlock _title = Ui.Text("", ThemeKeys.Text, 13.5, FontWeights.SemiBold);
    private readonly StackPanel _badges = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _endBadge = Ui.Pill("KILL", ThemeKeys.Positive, ThemeKeys.AccentText);
    private readonly Border _hpCheckBadge = Ui.Pill("HP ?", ThemeKeys.Warning, ThemeKeys.AccentText);
    private readonly Border _gapsBadge = Ui.Pill("GAPS", ThemeKeys.Warning, ThemeKeys.AccentText);
    private readonly Border _partialBadge = Ui.Pill("PARTIAL", ThemeKeys.BarTrack, ThemeKeys.TextMuted);
    private readonly TextBlock _timer = Ui.Text("0:00", ThemeKeys.Text, 15, FontWeights.SemiBold, mono: true);
    private readonly Button _prevTarget;
    private readonly Button _nextTarget;
    private readonly Button _modeChip;
    private readonly Grid _hpRow = new() { Height = 16, Margin = new Thickness(0, 4, 0, 0) };
    private readonly ColumnDefinition _hpFill = new() { Width = new GridLength(1, GridUnitType.Star) };
    private readonly ColumnDefinition _hpEmpty = new() { Width = new GridLength(0, GridUnitType.Star) };
    private readonly Border _hpFillBorder = new();
    private readonly Border _hpTradeTaken = new();
    private readonly TextBlock _hpText = Ui.Text("", ThemeKeys.Text, 10.5, FontWeights.SemiBold, mono: true);
    /// <summary>Slim HP bars of the other engaged bosses in a multi-boss fight (at most 2, under the main bar).</summary>
    private readonly StackPanel _bossBars = new() { Visibility = Visibility.Collapsed };
    private readonly SlimBossBar[] _bossBarViews = [new(), new()];
    private readonly TextBlock _subtitle = Ui.Text("", ThemeKeys.TextMuted, 10.5);
    private readonly Button _partyChip;
    private readonly Button _viewChip;
    private readonly Button _sizeChip;
    private readonly Button _trainingButton;
    private readonly Button _lockButton;
    private readonly Button _collapseButton;
    private readonly DockPanel _dock = new() { LastChildFill = true };
    private readonly CompactBarView _compactBar = new() { Visibility = Visibility.Collapsed };
    private Grid _root = null!;
    private bool _toastShowing;
    private readonly Border _toast = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(6, 5, 6, 1), Padding = new Thickness(9, 6, 9, 6) };
    private readonly TextBlock _toastTitle = Ui.Text("", ThemeKeys.AccentText, 11.5, FontWeights.Bold);
    private readonly TextBlock _toastBody = Ui.Text("", ThemeKeys.AccentText, 11);
    private readonly Grid _columnHeader = new() { Height = 15, Margin = new Thickness(0, 3, 0, 0) };
    private readonly Grid _rowsHost = new() { Margin = new Thickness(4, 2, 4, 2), ClipToBounds = true };
    private readonly StackPanel _statePanel = new() { Margin = new Thickness(14, 10, 14, 12), Visibility = Visibility.Collapsed };
    private readonly System.Windows.Shapes.Path _stateIcon = new() { Width = 22, Height = 22, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _stateTitle = Ui.Text("", ThemeKeys.Text, 13, FontWeights.SemiBold);
    private readonly TextBlock _stateBody = Ui.Text("", ThemeKeys.TextMuted, 11.5);
    private readonly ContentControl _stateExtra = new();
    private readonly System.Windows.Shapes.Ellipse _dot = Ui.Dot(7, ThemeKeys.Positive);
    private readonly TextBlock _footerState = Ui.Text("", ThemeKeys.TextMuted, 10.5);
    private readonly TextBlock _footerPing = Ui.Text("", ThemeKeys.TextMuted, 10.5, mono: true);
    private readonly TextBlock _footerStats = Ui.Text("", ThemeKeys.TextMuted, 10.5, mono: true);
    private readonly Grid _adjustBar = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(8, 0, 2, 3) };
    private readonly Slider _opacity = new() { Minimum = 0.15, Maximum = 1, Width = 120, SmallChange = 0.05, LargeChange = 0.1 };
    private readonly TextBlock _version = Ui.Text("", ThemeKeys.TextMuted, 10);
    private readonly Border _footerDivider = new() { Height = 1, Margin = new Thickness(6, 0, 6, 0) };
    private readonly DockPanel _footerLine = new() { Margin = new Thickness(8, 4, 8, 5), LastChildFill = true };
    private bool _hasRows;
    private readonly DispatcherTimer _toastTimer;

    // ── State ──
    private readonly Dictionary<uint, PlayerRowView> _rowViews = new();
    private readonly Dictionary<uint, PvpRowView> _pvpViews = new();
    private OverlayViewOptions _options = new();
    private MeterSnapshot _snapshot = MeterSnapshot.Empty;
    private Color _lastHpColor;
    private Brush? _lastHpBrush;
    private bool _suppressOpacityEvent;
    private bool? _lastWasPvp;

    public OverlayView()
    {
        _prevTarget = Ui.IconButton(Ui.IconLeft, "Previous target", () => CycleTargetRequested?.Invoke(-1), 9);
        _nextTarget = Ui.IconButton(Ui.IconRight, "Next target", () => CycleTargetRequested?.Invoke(+1), 9);
        _prevTarget.Width = _nextTarget.Width = 18;
        _modeChip = Ui.Chip("BOSS", "Mode: boss only / all targets / PvP (click to cycle)", () => CycleModeRequested?.Invoke());
        _viewChip = Ui.Chip("DPS", "View: DPS / total damage / damage taken / healing (click to cycle)", () =>
        {
            if (_snapshot.Mode == MeterMode.Pvp) CyclePvpSortRequested?.Invoke(); else CycleViewRequested?.Invoke();
        });
        _sizeChip = Ui.Chip("C", "Row size: Normal / Compact / Micro (click to cycle)", () => CycleRowSizeRequested?.Invoke());
        _partyChip = Ui.Chip("PARTY", "", () => PartyFilterToggled?.Invoke());
        _trainingButton = Ui.IconButton(Ui.IconStopwatch, "Training stopwatch", OpenTrainingMenu);
        _lockButton = Ui.IconButton(Ui.IconUnlock, "Lock overlay position (Ctrl+Alt+L also enables click-through)", () => LockToggled?.Invoke());
        _collapseButton = Ui.IconButton(IconCollapse, "Shrink to the compact bar (expands again for the next fight)", () => CollapseRequested?.Invoke(), 10);
        _compactBar.ExpandRequested += () => ExpandRequested?.Invoke();
        _toastTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromSeconds(8) };
        _toastTimer.Tick += (_, _) => HideToast();

        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        this.Ref(FontFamilyProperty, ThemeKeys.FontFamily);
        this.Ref(ForegroundProperty, ThemeKeys.Text);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        Content = BuildLayout();
        _opacity.ValueChanged += (_, e) =>
        {
            if (_suppressOpacityEvent) return;
            _bg.Opacity = e.NewValue;
            OpacityChanged?.Invoke(e.NewValue);
        };
    }

    // ── Events ──
    public event Action? ResetRequested;
    public event Action? CopyRequested;
    public event Action? SettingsRequested;
    public event Action? HideRequested;
    public event Action? LockToggled;
    public event Action? CycleModeRequested;
    public event Action? CycleRowSizeRequested;
    public event Action? CycleViewRequested;
    public event Action? CyclePvpSortRequested;
    /// <summary>The PARTY chip was clicked (toggle "party members only").</summary>
    public event Action? PartyFilterToggled;
    public event Action<int>? CycleTargetRequested;
    public event Action<TimeSpan>? TrainingRequested;
    public event Action? TrainingCancelRequested;
    /// <summary>Row clicked (row, ctrl held).</summary>
    public event Action<PlayerRow, bool>? RowClicked;
    public event Action<double>? OpacityChanged;
    /// <summary>The compact bar's chevron was clicked (the window raises the same for a click on the bar itself).</summary>
    public event Action? ExpandRequested;
    /// <summary>The toolbar's collapse button was clicked.</summary>
    public event Action? CollapseRequested;

    /// <summary>Segoe MDL2 "ChevronUp" (collapse to the compact bar).</summary>
    public const string IconCollapse = "";

    /// <summary>The slim out-of-combat bar (drag handle and click-to-expand target while compact).</summary>
    public CompactBarView CompactBar => _compactBar;

    /// <summary>True while the compact bar is shown instead of the full meter.</summary>
    public bool IsCompact { get; private set; }

    /// <summary>A toast is showing (or fading in); the full overlay is needed to read it.</summary>
    public bool ToastVisible => _toastShowing;

    /// <summary>
    /// A menu of the overlay (the training stopwatch menu) is open. Its popup is a separate window, so the overlay's
    /// own IsMouseOver is false while the user picks an item; the controller treats it as interaction.
    /// </summary>
    public bool MenuOpen { get; private set; }

    /// <summary>
    /// Switches between the full meter and the compact bar in place (same element tree, no re-creation; the window
    /// background, corner radius and frame are shared, so both look like the same overlay).
    /// </summary>
    public void SetCompact(bool compact)
    {
        if (compact == IsCompact) return;
        IsCompact = compact;
        ApplyChrome();
        if (compact) AdjustBarVisible = false;
    }

    /// <summary>The bars-only look is active: the full overlay is just the rows (no header/footer/box).</summary>
    public bool BarsOnlyActive => _options.BarsOnly && !IsCompact;

    /// <summary>The rows' host (in bars-only mode the window drags the overlay by it; a click opens the breakdown).</summary>
    public FrameworkElement RowsHost => _rowsHost;

    /// <summary>
    /// Shows the full meter, the compact bar or (bars only) just the rows. Bars only without rows to show falls back to
    /// the compact bar, so the overlay never turns into an invisible, unclickable window.
    /// </summary>
    private void ApplyChrome()
    {
        bool bars = _options.BarsOnly && !IsCompact;
        bool showBar = IsCompact || (bars && !_hasRows);
        _dock.Visibility = showBar ? Visibility.Collapsed : Visibility.Visible;
        _compactBar.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;
        _root.HorizontalAlignment = showBar ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        var chrome = bars ? Visibility.Collapsed : Visibility.Visible;
        _header.Visibility = chrome;
        _footerDivider.Visibility = chrome;
        _footerLine.Visibility = chrome;
        // Bars only: no window box behind the rows (each row draws its own track); the bar keeps the background.
        var box = bars && !showBar ? Visibility.Hidden : Visibility.Visible;
        _bg.Visibility = box;
        _frame.Visibility = box;
    }

    /// <summary>Bars only: a click on the rows host at <paramref name="p"/> (window drags capture the mouse there).</summary>
    public void ClickRowAt(Point p, bool ctrl)
    {
        foreach (var v in _rowViews.Values)
        {
            if (p.Y >= v.Y && p.Y < v.Y + v.ActualHeight)
            {
                RowClicked?.Invoke(v.Row, ctrl);
                return;
            }
        }
    }

    /// <summary>Element that starts a window drag (the header band).</summary>
    public FrameworkElement DragHandle => _header;

    /// <summary>Bottom-right resize grip (width only; height auto-fits).</summary>
    public Thumb ResizeGrip { get; private set; } = null!;

    /// <summary>Animations (row moves, bar growth, toast fade). Disable for offscreen renders.</summary>
    public bool AnimationsEnabled { get; set; } = true;

    /// <summary>True while a training run is active (the stopwatch menu then offers "Stop").</summary>
    public bool TrainingActive { get; private set; }

    /// <summary>Shows the opacity slider/version/grip strip (the window shows it on hover while unlocked).</summary>
    public bool AdjustBarVisible
    {
        get => _adjustBar.Visibility == Visibility.Visible;
        set => _adjustBar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public MeterSnapshot LastSnapshot => _snapshot;

    // ─────────────────────────────── Layout ───────────────────────────────

    private UIElement BuildLayout()
    {
        var root = _root = new Grid();
        _bg.Ref(Border.BackgroundProperty, ThemeKeys.WindowBackground).Ref(Border.CornerRadiusProperty, ThemeKeys.CornerRadius);
        _frame.Ref(Border.BorderBrushProperty, ThemeKeys.Border).Ref(Border.CornerRadiusProperty, ThemeKeys.CornerRadius);
        root.Children.Add(_bg);

        var dock = _dock;
        // Header
        _header.Ref(Border.BackgroundProperty, AppThemeKeys.HeaderBackground);
        _header.Cursor = Cursors.SizeAll;
        _header.Child = BuildHeader();
        DockPanel.SetDock(_header, Dock.Top);
        dock.Children.Add(_header);

        // Toast
        _toast.Ref(Border.BackgroundProperty, ThemeKeys.Accent);
        _toast.CornerRadius = new CornerRadius(4);
        var toastStack = new StackPanel { Orientation = Orientation.Horizontal };
        toastStack.Children.Add(Ui.Icon(Ui.IconStar, ThemeKeys.AccentText, 14));
        var toastText = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        _toastBody.TextWrapping = TextWrapping.Wrap;
        toastText.Children.Add(_toastTitle);
        toastText.Children.Add(_toastBody);
        toastStack.Children.Add(toastText);
        _toast.Child = toastStack;
        _toast.MouseLeftButtonUp += (_, _) => HideToast();
        DockPanel.SetDock(_toast, Dock.Top);
        dock.Children.Add(_toast);

        // Footer
        var footer = BuildFooter();
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);

        // Column header + rows + state panel
        var body = new StackPanel();
        _columnHeader.Margin = new Thickness(4, 3, 4, 0);
        body.Children.Add(_columnHeader);
        body.Children.Add(_rowsHost);
        body.Children.Add(_statePanel);
        BuildStatePanel();
        dock.Children.Add(body);

        root.Children.Add(dock);
        root.Children.Add(_compactBar);
        root.Children.Add(_frame);
        return root;
    }

    private UIElement BuildHeader()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _emblem.Margin = new Thickness(0, 0, 8, 0);
        _emblem.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRowSpan(_emblem, 2);
        g.Children.Add(_emblem);

        // Line 1: title + badges ............ timer ◀ ▶ MODE
        var line1 = new DockPanel { LastChildFill = true };
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _timer.Margin = new Thickness(6, 0, 2, 0);
        right.Children.Add(_timer);
        right.Children.Add(_prevTarget);
        right.Children.Add(_nextTarget);
        right.Children.Add(_modeChip);
        DockPanel.SetDock(right, Dock.Right);
        line1.Children.Add(right);
        _endBadge.Margin = _hpCheckBadge.Margin = _gapsBadge.Margin = _partialBadge.Margin = new Thickness(5, 1, 0, 0);
        _badges.Children.Add(_endBadge);
        _badges.Children.Add(_hpCheckBadge);
        _badges.Children.Add(_partialBadge);
        _badges.Children.Add(_gapsBadge);
        _hpCheckBadge.ToolTip = "HP check: the decoded damage does not match the boss's HP loss. Numbers may be incomplete.";
        _gapsBadge.ToolTip = "The capture had gaps during this fight; numbers may be incomplete.";
        var titleRow = new TrimPanel { VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(_title);
        titleRow.Children.Add(_badges);
        line1.Children.Add(titleRow);
        Grid.SetColumn(line1, 1);
        g.Children.Add(line1);

        // Line 2: HP bar
        var track = new Border();
        track.Ref(Border.BackgroundProperty, ThemeKeys.BarTrack).Ref(Border.CornerRadiusProperty, ThemeKeys.BarCornerRadius);
        Grid.SetColumnSpan(track, 2);
        _hpRow.ColumnDefinitions.Add(_hpFill);
        _hpRow.ColumnDefinitions.Add(_hpEmpty);
        _hpFillBorder.Ref(Border.CornerRadiusProperty, ThemeKeys.BarCornerRadius);
        _hpTradeTaken.Ref(Border.CornerRadiusProperty, ThemeKeys.BarCornerRadius).Ref(Border.BackgroundProperty, ThemeKeys.Negative);
        _hpTradeTaken.Visibility = Visibility.Collapsed;
        Grid.SetColumn(_hpTradeTaken, 1);
        _hpText.HorizontalAlignment = HorizontalAlignment.Right;
        _hpText.Margin = new Thickness(0, 0, 6, 0);
        _hpText.Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.9, Color = Colors.Black };
        Grid.SetColumnSpan(_hpText, 2);
        _hpRow.Children.Add(track);
        _hpRow.Children.Add(_hpFillBorder);
        _hpRow.Children.Add(_hpTradeTaken);
        _hpRow.Children.Add(_hpText);
        Grid.SetColumn(_hpRow, 1);
        Grid.SetRow(_hpRow, 1);
        g.Children.Add(_hpRow);

        // Line 2b: slim bars of the other bosses (multi-boss fights only)
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var bar in _bossBarViews) _bossBars.Children.Add(bar);
        Grid.SetColumn(_bossBars, 1);
        Grid.SetRow(_bossBars, 2);
        g.Children.Add(_bossBars);

        // Line 3: subtitle ........ toolbar
        var line3 = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 5, 0, 0) };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(_partyChip);
        tools.Children.Add(_viewChip);
        tools.Children.Add(_sizeChip);
        tools.Children.Add(_trainingButton);
        tools.Children.Add(Ui.IconButton(Ui.IconReset, "Reset meter (Ctrl+Alt+D)", () => ResetRequested?.Invoke()));
        tools.Children.Add(Ui.IconButton(Ui.IconCopy, "Copy summary to clipboard for chat (Ctrl+Alt+C)", () => CopyRequested?.Invoke()));
        tools.Children.Add(_lockButton);
        tools.Children.Add(Ui.IconButton(Ui.IconSettings, "Open dashboard settings", () => SettingsRequested?.Invoke()));
        tools.Children.Add(_collapseButton);
        tools.Children.Add(Ui.IconButton(Ui.IconHide, "Hide overlay (Ctrl+Alt+O)", () => HideRequested?.Invoke()));
        DockPanel.SetDock(tools, Dock.Right);
        line3.Children.Add(tools);
        _subtitle.Margin = new Thickness(1, 0, 6, 0);
        line3.Children.Add(_subtitle);
        Grid.SetRow(line3, 3);
        Grid.SetColumnSpan(line3, 2);
        g.Children.Add(line3);
        return g;
    }

    private UIElement BuildFooter()
    {
        var sp = new StackPanel();
        var divider = _footerDivider;
        divider.Ref(Border.BackgroundProperty, AppThemeKeys.Divider);
        sp.Children.Add(divider);

        var line = _footerLine;
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        _dot.Margin = new Thickness(0, 1, 6, 0);
        left.Children.Add(_dot);
        left.Children.Add(_footerState);
        _footerPing.Margin = new Thickness(8, 0, 0, 0);
        left.Children.Add(_footerPing);
        DockPanel.SetDock(left, Dock.Left);
        line.Children.Add(left);
        _footerStats.HorizontalAlignment = HorizontalAlignment.Right;
        _footerStats.Margin = new Thickness(8, 0, 0, 0);
        line.Children.Add(_footerStats);
        sp.Children.Add(line);

        // Adjust bar: opacity slider · version · resize grip
        _adjustBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _adjustBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _adjustBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _adjustBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var opIcon = Ui.Icon("", ThemeKeys.TextMuted, 10);
        opIcon.Margin = new Thickness(0, 0, 6, 0);
        opIcon.ToolTip = "Background opacity";
        _adjustBar.Children.Add(opIcon);
        _opacity.ToolTip = "Background opacity";
        Grid.SetColumn(_opacity, 1);
        _adjustBar.Children.Add(_opacity);
        _version.HorizontalAlignment = HorizontalAlignment.Right;
        _version.Margin = new Thickness(0, 0, 6, 0);
        Grid.SetColumn(_version, 2);
        _adjustBar.Children.Add(_version);
        ResizeGrip = BuildGrip();
        Grid.SetColumn(ResizeGrip, 3);
        _adjustBar.Children.Add(ResizeGrip);
        sp.Children.Add(_adjustBar);
        return sp;
    }

    private static Thumb BuildGrip()
    {
        const string xaml = """
            <ControlTemplate TargetType="Thumb" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Grid Background="Transparent" Width="14" Height="14">
                <Path Data="M12,2 L12,12 L2,12 Z M12,2" Fill="{DynamicResource Theme.TextMuted}" Opacity="0.55" />
              </Grid>
            </ControlTemplate>
            """;
        var t = new Thumb { Cursor = Cursors.SizeWE, ToolTip = "Drag to resize width", VerticalAlignment = VerticalAlignment.Bottom };
        t.Template = (ControlTemplate)XamlReader.Parse(xaml);
        return t;
    }

    private void BuildStatePanel()
    {
        _stateIcon.Ref(System.Windows.Shapes.Shape.FillProperty, ThemeKeys.Accent);
        _stateIcon.Margin = new Thickness(0, 0, 0, 8);
        _stateTitle.TextWrapping = TextWrapping.Wrap;
        _stateBody.TextWrapping = TextWrapping.Wrap;
        _stateBody.TextTrimming = TextTrimming.None;
        _stateBody.Margin = new Thickness(0, 3, 0, 0);
        _stateExtra.Margin = new Thickness(0, 8, 0, 0);
        _statePanel.Children.Add(_stateIcon);
        _statePanel.Children.Add(_stateTitle);
        _statePanel.Children.Add(_stateBody);
        _statePanel.Children.Add(_stateExtra);
    }

    // ─────────────────────────────── Update ───────────────────────────────

    public void Update(MeterSnapshot snapshot, OverlayStatus status, OverlayViewOptions options)
    {
        _snapshot = snapshot;
        _options = options;
        var phase = OverlayPhaseLogic.Compute(status.Capture, snapshot);
        bool pvp = snapshot.Mode == MeterMode.Pvp;
        TrainingActive = status.TrainingRemaining is not null;

        _suppressOpacityEvent = true;
        if (Math.Abs(_opacity.Value - options.BackgroundOpacity) > 0.001) _opacity.Value = options.BackgroundOpacity;
        _suppressOpacityEvent = false;
        _bg.Opacity = options.BackgroundOpacity;
        _version.Text = string.IsNullOrEmpty(options.Version) ? "" : "v" + options.Version;
        _lockButton.Content = options.Locked ? Ui.IconLock : Ui.IconUnlock;
        _lockButton.ToolTip = options.Locked
            ? options.ClickThrough ? "Locked + click-through (Ctrl+Alt+L to release)" : "Unlock overlay"
            : "Lock overlay position (Ctrl+Alt+L also enables click-through)";
        _header.Cursor = options.Locked ? Cursors.Arrow : Cursors.SizeAll;
        _collapseButton.Visibility = options.ShrinkWhenIdle ? Visibility.Visible : Visibility.Collapsed;
        _sizeChip.Content = options.RowSize switch { RowSize.Normal => "N", RowSize.Compact => "C", _ => "M" };
        // Lit (accent text) while only party members are shown, dimmed while everyone hitting the same enemies is listed.
        _partyChip.Visibility = pvp ? Visibility.Collapsed : Visibility.Visible;
        _partyChip.SetResourceReference(ForegroundProperty, options.PartyOnly ? ThemeKeys.Accent : ThemeKeys.TextMuted);
        _partyChip.Opacity = options.PartyOnly ? 1.0 : 0.75;
        _partyChip.ToolTip = options.PartyOnly
            ? "Showing party members only (you + party roster; solo = just you). Click to show everyone (Ctrl+Alt+P)"
            : "Showing everyone hitting the same enemies. Click to show party members only (Ctrl+Alt+P)";
        _modeChip.Content = snapshot.Mode switch { MeterMode.AllTargets => "ALL", MeterMode.Pvp => "PVP", _ => "BOSS" };
        _viewChip.Content = pvp
            ? options.PvpSort == PvpSort.Threat ? "THREAT" : "DMG"
            : options.View switch { MeterView.Total => "TOTAL", MeterView.Taken => "TAKEN", MeterView.Heal => "HEAL", _ => "DPS" };
        _viewChip.ToolTip = pvp ? "Sort opponents by threat (damage they deal you) or by damage you dealt" : "View: DPS / total damage / damage taken / healing (click to cycle)";

        bool dark = TryFindResource(AppThemeKeys.IsDark) is not bool d || d;
        if (_hpText.Effect is DropShadowEffect fx) fx.Color = dark ? Colors.Black : Colors.White;
        foreach (var bar in _bossBarViews) bar.SetShadow(dark ? Colors.Black : Colors.White);
        UpdateHeader(snapshot, status, phase, pvp);

        if (_lastWasPvp != pvp)
        {
            // Switching layouts: drop the other layout's row views.
            foreach (var v in _rowViews.Values) _rowsHost.Children.Remove(v);
            foreach (var v in _pvpViews.Values) _rowsHost.Children.Remove(v);
            _rowViews.Clear();
            _pvpViews.Clear();
            _lastWasPvp = pvp;
        }

        bool hasRows = pvp ? UpdatePvpRows(snapshot, options) : UpdatePlayerRows(snapshot, status, options);
        _hasRows = hasRows;
        ApplyChrome();
        bool showState = !hasRows && !options.BarsOnly;
        _rowsHost.Visibility = hasRows ? Visibility.Visible : Visibility.Collapsed;
        _columnHeader.Visibility = hasRows && !pvp && options.ShowColumnHeader && options.RowSize != RowSize.Micro ? Visibility.Visible : Visibility.Collapsed;
        if (_columnHeader.Visibility == Visibility.Visible) UpdateColumnHeader(options);
        _statePanel.Visibility = showState ? Visibility.Visible : Visibility.Collapsed;
        if (showState) UpdateStatePanel(phase, snapshot, status, pvp);

        UpdateFooter(snapshot, status, phase, pvp);
        _compactBar.Update(CompactBarModel.Build(snapshot, status), options.RowSize);
    }

    private void UpdateHeader(MeterSnapshot s, OverlayStatus status, OverlayPhase phase, bool pvp)
    {
        bool live = phase is OverlayPhase.Live or OverlayPhase.Ended;
        var target = s.Target;
        string subtitle;

        _endBadge.Visibility = Visibility.Collapsed;
        _hpCheckBadge.Visibility = Visibility.Collapsed;
        _partialBadge.Visibility = Visibility.Collapsed;
        _gapsBadge.Visibility = Visibility.Collapsed;
        _hpTradeTaken.Visibility = Visibility.Collapsed;
        _bossBars.Visibility = Visibility.Collapsed;

        if (pvp)
        {
            _emblem.Glyph = Glyphs.Pvp;
            int active = s.PvpRows.Count(r => !r.Killed && (s.TimeUtc - r.LastActivityUtc).TotalSeconds < 30);
            _title.Text = "PvP";
            subtitle = $"{active} active · {s.PvpKills} kill{(s.PvpKills == 1 ? "" : "s")}";
            long dealt = s.PvpRows.Sum(r => r.DamageDealt), taken = s.PvpRows.Sum(r => r.DamageTaken);
            double share = dealt + taken > 0 ? (double)dealt / (dealt + taken) : 0.5;
            _hpRow.Visibility = s.PvpRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            SetHpColumns(share);
            _hpFillBorder.Ref(Border.BackgroundProperty, ThemeKeys.Positive);
            _hpTradeTaken.Visibility = Visibility.Visible;
            _hpText.Text = $"↑ {Fmt.Abbrev(dealt)}   ↓ {Fmt.Abbrev(taken)}";
            _hpText.HorizontalAlignment = HorizontalAlignment.Center;
            _hpRow.ToolTip = $"Dealt {Fmt.Exact(dealt)} · taken {Fmt.Exact(taken)}";
            _emblem.FillBrush = (Brush?)TryFindResource(ThemeKeys.Accent);
            _emblem.Fraction = 1;
        }
        else if (live && target is not null)
        {
            _emblem.Glyph = target.IsDummy ? Glyphs.Dummy : target.IsPlayer ? Glyphs.Pvp : Glyphs.Boss;
            _title.Text = target.Name.Length > 0 ? target.Name : "Unknown target";
            double? frac = target.HpFraction ?? (target.Hp is { } hp && target.MaxHp is > 0 ? (double)hp / target.MaxHp.Value : null);
            if (target.IsDead) frac = 0;
            var hpBrush = HpBrush(frac ?? 1);
            _hpRow.Visibility = Visibility.Visible;
            SetHpColumns(frac ?? 1);
            _hpFillBorder.Background = hpBrush;
            _hpFillBorder.Opacity = frac is null ? 0.35 : 1;
            _hpText.HorizontalAlignment = HorizontalAlignment.Right;
            _hpText.Text = frac is null && target.Hp is null
                ? $"{Fmt.Abbrev(target.DamageTaken)} dmg · HP unknown"
                : Fmt.HpText(target.IsDead ? 0 : target.Hp, target.MaxHp, frac);
            _hpRow.ToolTip = target.MaxHp is { } max
                ? $"{Fmt.Exact(target.IsDead ? 0 : target.Hp ?? 0)} / {Fmt.Exact(max)} HP · damage taken {Fmt.Exact(target.DamageTaken)}"
                : $"Damage taken {Fmt.Exact(target.DamageTaken)}";
            _emblem.FillBrush = hpBrush;
            _emblem.Fraction = frac;
            subtitle = s.MapName ?? KindLabel(s);
            if (s.Targets.Count > 1) subtitle += $" · {IndexOf(s.Targets, target) + 1}/{s.Targets.Count}";
            UpdateBossBars(s, target);
        }
        else
        {
            _emblem.Glyph = live ? Glyphs.Dummy : Glyphs.Waiting;
            _emblem.FillBrush = (Brush?)TryFindResource(ThemeKeys.BarTrack);
            _emblem.Fraction = null;
            _hpRow.Visibility = Visibility.Collapsed;
            _title.Text = live ? (s.Mode == MeterMode.AllTargets ? "All targets" : "Encounter") : PhaseTitle(phase);
            subtitle = !string.IsNullOrEmpty(s.MapName) ? s.MapName! : !string.IsNullOrEmpty(s.StatusText) ? s.StatusText : PhaseShort(phase);
        }

        if (phase == OverlayPhase.Ended && !pvp)
        {
            (string text, string key) = s.Outcome switch
            {
                EncounterOutcome.Kill => ("KILL", ThemeKeys.Positive),
                EncounterOutcome.Wipe => ("WIPE", ThemeKeys.Negative),
                EncounterOutcome.Timeout => ("ENDED", ThemeKeys.TextMuted),
                EncounterOutcome.ManualReset => ("RESET", ThemeKeys.TextMuted),
                EncounterOutcome.ZoneChange => ("LEFT ZONE", ThemeKeys.TextMuted),
                _ => ("ENDED", ThemeKeys.TextMuted),
            };
            ((TextBlock)_endBadge.Child).Text = text;
            _endBadge.Ref(Border.BackgroundProperty, key);
            _endBadge.Visibility = Visibility.Visible;
        }
        if (live && !pvp && s.PartialView)
        {
            // Open world: the server sends only your own direct hits. Not a decoding problem, so no HP-check warning.
            _partialBadge.ToolTip = (s.PartialViewText ?? "Partial view: only your/party damage is visible") +
                                    ".\n% = damage / boss max HP. Players seen only through DoT ticks or heals are grouped as \"Others\".";
            _partialBadge.Visibility = Visibility.Visible;
        }
        else if (live && !pvp && s.HpCheckRatio is { } ratio && Math.Abs(ratio - 1) > 0.03)
        {
            ((TextBlock)_hpCheckBadge.Child).Text = "HP " + Fmt.Percent(ratio, 0);
            _hpCheckBadge.ToolTip = $"HP check: decoded damage explains {Fmt.Percent(ratio, 1)} of the boss's HP loss. " +
                                    "Numbers may be incomplete (capture gaps, a game patch, or heals/shields).";
            _hpCheckBadge.Visibility = Visibility.Visible;
        }

        // Timer
        _timer.Text = live || pvp ? Fmt.Duration(s.Elapsed) : "0:00";
        _timer.Opacity = live || pvp ? 1 : 0.45;
        _timer.Ref(TextBlock.ForegroundProperty, phase == OverlayPhase.Ended ? ThemeKeys.TextMuted : ThemeKeys.Text);
        bool canCycle = !pvp && s.Targets.Count > 1;
        _prevTarget.Visibility = _nextTarget.Visibility = canCycle ? Visibility.Visible : Visibility.Collapsed;

        if (status.TrainingRemaining is { } rem)
        {
            _subtitle.Text = $"TRAINING · {Fmt.Duration(rem)} left";
            _subtitle.Ref(TextBlock.ForegroundProperty, ThemeKeys.Accent);
            _trainingButton.Ref(ForegroundProperty, ThemeKeys.Accent);
        }
        else
        {
            _subtitle.Text = subtitle.ToUpperInvariant();
            _subtitle.Ref(TextBlock.ForegroundProperty, ThemeKeys.TextMuted);
            _trainingButton.ClearValue(ForegroundProperty);
        }
        _subtitle.ToolTip = _subtitle.Text;
    }

    /// <summary>
    /// Multi-boss fight: the main bar shows the cycled target, and up to two slim bars (name + %) show the other engaged
    /// bosses. A single-boss fight keeps the classic one-bar header.
    /// </summary>
    private void UpdateBossBars(MeterSnapshot s, TargetInfo target)
    {
        if (s.Bosses.Count < 2) return;
        int shown = 0;
        foreach (var boss in s.Bosses)
        {
            if (boss.EntityId == target.EntityId) continue;
            if (shown == _bossBarViews.Length) break;
            double? frac = boss.IsDead ? 0 : boss.HpFraction ?? (boss.Hp is { } hp && boss.MaxHp is > 0 ? (double)hp / boss.MaxHp.Value : null);
            _bossBarViews[shown].Update(boss, frac, HpBrush(frac ?? 1));
            _bossBarViews[shown].Visibility = Visibility.Visible;
            shown++;
        }
        for (int i = shown; i < _bossBarViews.Length; i++) _bossBarViews[i].Visibility = Visibility.Collapsed;
        _bossBars.Visibility = shown > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>One slim secondary boss HP bar: "Black Smoke Murute ········ 42 %".</summary>
    private sealed class SlimBossBar : Grid
    {
        private readonly ColumnDefinition _fill = new() { Width = new GridLength(1, GridUnitType.Star) };
        private readonly ColumnDefinition _empty = new() { Width = new GridLength(0, GridUnitType.Star) };
        private readonly Border _fillBorder = new();
        private readonly TextBlock _name = Ui.Text("", ThemeKeys.Text, 9.5, FontWeights.SemiBold);
        private readonly TextBlock _pct = Ui.Text("", ThemeKeys.Text, 9.5, FontWeights.SemiBold, mono: true);

        public SlimBossBar()
        {
            Height = 12;
            Margin = new Thickness(0, 3, 0, 0);
            ColumnDefinitions.Add(_fill);
            ColumnDefinitions.Add(_empty);
            var track = new Border();
            track.Ref(Border.BackgroundProperty, ThemeKeys.BarTrack).Ref(Border.CornerRadiusProperty, ThemeKeys.BarCornerRadius);
            SetColumnSpan(track, 2);
            _fillBorder.Ref(Border.CornerRadiusProperty, ThemeKeys.BarCornerRadius);
            var shadow = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.9, Color = Colors.Black };
            _name.Margin = new Thickness(5, -1, 40, 0);
            _name.TextTrimming = TextTrimming.CharacterEllipsis;
            _name.VerticalAlignment = VerticalAlignment.Center;
            _name.Effect = shadow;
            SetColumnSpan(_name, 2);
            _pct.HorizontalAlignment = HorizontalAlignment.Right;
            _pct.VerticalAlignment = VerticalAlignment.Center;
            _pct.Margin = new Thickness(0, -1, 6, 0);
            _pct.Effect = shadow;
            SetColumnSpan(_pct, 2);
            Children.Add(track);
            Children.Add(_fillBorder);
            Children.Add(_name);
            Children.Add(_pct);
        }

        public void SetShadow(Color c)
        {
            if (_name.Effect is DropShadowEffect fx && fx.Color != c) fx.Color = c;
        }

        public void Update(TargetInfo boss, double? fraction, Brush brush)
        {
            double f = Math.Clamp(fraction ?? 1, 0, 1);
            _fill.Width = new GridLength(Math.Max(f, 0.00001), GridUnitType.Star);
            _empty.Width = new GridLength(Math.Max(1 - f, 0.00001), GridUnitType.Star);
            _fillBorder.Background = brush;
            _fillBorder.Opacity = fraction is null ? 0.35 : 1;
            _name.Text = boss.Name.Length > 0 ? boss.Name : "Boss";
            _pct.Text = boss.IsDead ? "DEAD" : fraction is { } fr ? Fmt.Percent(fr, fr < 0.1 ? 1 : 0) : "—";
            ToolTip = boss.MaxHp is { } max
                ? $"{boss.Name}: {Fmt.Exact(boss.IsDead ? 0 : boss.Hp ?? 0)} / {Fmt.Exact(max)} HP · damage taken {Fmt.Exact(boss.DamageTaken)}"
                : $"{boss.Name}: damage taken {Fmt.Exact(boss.DamageTaken)}";
        }
    }

    private static int IndexOf(IReadOnlyList<TargetInfo> list, TargetInfo t)
    {
        for (int i = 0; i < list.Count; i++) if (list[i].EntityId == t.EntityId) return i;
        return 0;
    }

    private static string KindLabel(MeterSnapshot s) => s.EncounterKind switch
    {
        Contracts.EncounterKind.Boss => "Boss encounter",
        Contracts.EncounterKind.Dummy => "Training dummy",
        Contracts.EncounterKind.Training => "Training",
        Contracts.EncounterKind.Trash => "Open world",
        _ => s.Mode == MeterMode.AllTargets ? "All targets" : "Encounter",
    };

    private void SetHpColumns(double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        _hpFill.Width = new GridLength(Math.Max(fraction, 0.00001), GridUnitType.Star);
        _hpEmpty.Width = new GridLength(Math.Max(1 - fraction, 0.00001), GridUnitType.Star);
    }

    /// <summary>HP colour: low → mid → high as the fraction rises (interpolated, from the active palette).</summary>
    private Brush HpBrush(double fraction)
    {
        Color Get(string key, string fallback) => TryFindResource(key) is SolidColorBrush b ? b.Color : ColorUtil.Parse(fallback);
        var high = Get(ThemeKeys.HpHigh, "#C23E3A");
        var mid = Get(ThemeKeys.HpMid, "#DD8530");
        var low = Get(ThemeKeys.HpLow, "#F2C94C");
        var c = fraction >= 0.5 ? ColorUtil.Blend(mid, high, (fraction - 0.5) / 0.5) : ColorUtil.Blend(low, mid, fraction / 0.5);
        if (c == _lastHpColor && _lastHpBrush is not null) return _lastHpBrush;
        _lastHpColor = c;
        var brush = new LinearGradientBrush(ColorUtil.Blend(c, Colors.White, 0.18), c, 90);
        brush.Freeze();
        _lastHpBrush = brush;
        return brush;
    }

    private static string PhaseTitle(OverlayPhase p) => p switch
    {
        OverlayPhase.NpcapMissing => "Npcap required",
        OverlayPhase.CaptureError => "Capture error",
        OverlayPhase.CaptureStopped => "Capture stopped",
        OverlayPhase.WaitingForGame => "Waiting for AION 2",
        OverlayPhase.Detecting => "Detecting connection",
        OverlayPhase.WaitingForData => "Connected",
        OverlayPhase.WaitingForCombat => "Ready",
        _ => "Aion2Dps",
    };

    private static string PhaseShort(OverlayPhase p) => p switch
    {
        OverlayPhase.NpcapMissing => "Not capturing",
        OverlayPhase.CaptureError => "Not capturing",
        OverlayPhase.CaptureStopped => "Not capturing",
        OverlayPhase.WaitingForGame => "Game not found",
        OverlayPhase.Detecting => "Checking network flows",
        OverlayPhase.WaitingForData => "Waiting for game data",
        OverlayPhase.WaitingForCombat => "Waiting for combat",
        _ => "",
    };

    // ── Party rows ──

    private bool UpdatePlayerRows(MeterSnapshot s, OverlayStatus status, OverlayViewOptions o)
    {
        var displays = ComputeRows(s, o);
        var m = RowMetrics.For(o.RowSize);
        var seen = new HashSet<uint>();
        bool animate = AnimationsEnabled && IsLoaded;
        for (int i = 0; i < displays.Count; i++)
        {
            var d = displays[i];
            seen.Add(d.Row.EntityId);
            bool isNew = false;
            if (!_rowViews.TryGetValue(d.Row.EntityId, out var view))
            {
                view = new PlayerRowView();
                var captured = view;
                view.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    RowClicked?.Invoke(captured.Row, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
                };
                _rowViews[d.Row.EntityId] = view;
                _rowsHost.Children.Add(view);
                isNew = true;
            }
            view.Configure(o);
            view.Update(d, status.PinnedEntityId == d.Row.EntityId, animate && !isNew);
            view.MoveTo(i * m.Height, animate && !isNew);
            if (isNew && animate)
            {
                view.Opacity = 0;
                view.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(250)));
            }
        }
        foreach (var id in _rowViews.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _rowsHost.Children.Remove(_rowViews[id]);
            _rowViews.Remove(id);
        }
        _rowsHost.Height = displays.Count * m.Height;
        return displays.Count > 0;
    }

    /// <summary>Sorts/filters snapshot rows for the active view and computes the shown values (pure; unit-testable).</summary>
    internal static List<RowDisplay> ComputeRows(MeterSnapshot s, OverlayViewOptions o)
    {
        var src = s.Rows.Where(r => r.Kind != CombatantKind.EnemyPlayer).ToList();
        double elapsed = Math.Max(1, s.Elapsed.TotalSeconds);
        IEnumerable<PlayerRow> filtered;
        Func<PlayerRow, double> primary, secondary;
        Func<PlayerRow, double?> pct;
        Func<PlayerRow, double> share;
        switch (o.View)
        {
            case MeterView.Total:
                filtered = src.Where(r => r.Damage > 0 || r.IsLocal).OrderByDescending(r => r.Damage).ThenByDescending(r => r.Dps);
                primary = r => r.Damage;
                secondary = r => r.Dps;
                pct = r => r.Contribution > 0 ? r.Contribution : r.DamageShare;
                share = r => r.DamageShare;
                break;
            case MeterView.Taken:
            {
                double total = src.Sum(r => (double)r.DamageTaken);
                filtered = src.Where(r => r.DamageTaken > 0).OrderByDescending(r => r.DamageTaken);
                primary = r => r.DamageTaken;
                secondary = r => r.DamageTaken / elapsed;
                pct = r => total > 0 ? r.DamageTaken / total : null;
                share = r => total > 0 ? r.DamageTaken / total : 0;
                break;
            }
            case MeterView.Heal:
            {
                double total = src.Sum(r => (double)r.Healing);
                filtered = src.Where(r => r.Healing > 0).OrderByDescending(r => r.Healing);
                primary = r => r.Healing;
                secondary = r => r.Healing / elapsed;
                pct = r => total > 0 ? r.Healing / total : null;
                share = r => total > 0 ? r.Healing / total : 0;
                break;
            }
            default:
                filtered = src.Where(r => r.Damage > 0 || r.IsLocal).OrderByDescending(r => r.Dps).ThenByDescending(r => r.Damage);
                primary = r => r.Dps;
                secondary = r => r.Damage;
                pct = r => r.Contribution > 0 ? r.Contribution : r.DamageShare;
                share = r => r.DamageShare;
                break;
        }
        var list = filtered.ToList();
        if (s.PartialView && o.View is not (MeterView.Taken or MeterView.Heal))
        {
            // Partial view: you and your party first, then other players with direct hits, the "Others" row last; the
            // percentage is always the share of the boss's max HP (a share of the visible damage means nothing here).
            list = list.OrderBy(r => r.AggregateCount > 0 ? 2 : r.IsLocal || r.IsPartyMember ? 0 : 1).ToList();
            pct = r => r.Contribution;
        }
        int max = Math.Max(1, o.MaxRows);
        if (list.Count > max)
        {
            var local = list.FindIndex(r => r.IsLocal);
            var top = list.Take(max).ToList();
            if (local >= max) top[max - 1] = list[local]; // your own row never disappears
            list = top;
        }
        double topValue = list.Count > 0 ? list.Max(primary) : 0;
        var result = new List<RowDisplay>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            var r = list[i];
            double bar = o.BarMode == BarMode.ShareOfParty && !s.PartialView ? share(r) : topValue > 0 ? primary(r) / topValue : 0;
            result.Add(new RowDisplay(r, i + 1, primary(r), secondary(r), pct(r), bar));
        }
        return result;
    }

    private void UpdateColumnHeader(OverlayViewOptions o)
    {
        var m = RowMetrics.For(o.RowSize);
        (string p, string sec, string pc) = o.View switch
        {
            MeterView.Total => ("TOTAL", "DPS", "CONTR"),
            MeterView.Taken => ("TAKEN", "DTPS", "SHARE"),
            MeterView.Heal => ("HEAL", "HPS", "SHARE"),
            _ => ("DPS", "TOTAL", "CONTR"),
        };
        _columnHeader.Children.Clear();
        _columnHeader.ColumnDefinitions.Clear();
        void Col(double width, string text, TextAlignment align = TextAlignment.Right)
        {
            _columnHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = width < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(width) });
            if (text.Length == 0) return;
            var t = Ui.Text(text, ThemeKeys.TextMuted, 8.5, FontWeights.Bold);
            t.TextAlignment = align;
            t.Opacity = 0.8;
            Grid.SetColumn(t, _columnHeader.ColumnDefinitions.Count - 1);
            _columnHeader.Children.Add(t);
        }
        _columnHeader.Margin = new Thickness(4 + 3, 3, 4 + 6, 0);
        if (o.ShowRank) Col(m.RankWidth, "#", TextAlignment.Center);
        if (o.ShowClassEmblem) Col(m.Emblem + 7, "");
        else Col(4, "");
        Col(-1, "NAME", TextAlignment.Left);
        double numW = o.RowSize == RowSize.Normal ? 56 : 52;
        if (o.ShowGearScore) Col(numW - 8, "GS");
        if (o.ShowCritRate) Col(numW - 10, "CRIT");
        if (o.ShowMaxHit) Col(numW, "MAX");
        Col(numW, p);
        if (o.ShowTotal) Col(numW, sec);
        if (o.ShowContribution) Col(46, pc);
    }

    // ── PvP rows ──

    private bool UpdatePvpRows(MeterSnapshot s, OverlayViewOptions o)
    {
        var rows = SortPvp(s.PvpRows, o.PvpSort).Take(Math.Max(1, o.MaxRows)).ToList();
        double h = RowMetrics.PvpHeight(o.RowSize);
        var seen = new HashSet<uint>();
        bool animate = AnimationsEnabled && IsLoaded;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            seen.Add(r.EntityId);
            bool isNew = false;
            if (!_pvpViews.TryGetValue(r.EntityId, out var view))
            {
                view = new PvpRowView();
                _pvpViews[r.EntityId] = view;
                _rowsHost.Children.Add(view);
                isNew = true;
            }
            view.Configure(o.RowSize);
            view.Update(r, HpBrush(r.HpFraction ?? 1));
            view.MoveTo(i * h, animate && !isNew);
        }
        foreach (var id in _pvpViews.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _rowsHost.Children.Remove(_pvpViews[id]);
            _pvpViews.Remove(id);
        }
        _rowsHost.Height = rows.Count * h;
        return rows.Count > 0;
    }

    internal static IEnumerable<PvpRow> SortPvp(IEnumerable<PvpRow> rows, PvpSort sort) => sort == PvpSort.Damage
        ? rows.OrderByDescending(r => r.DamageDealt).ThenByDescending(r => r.LastActivityUtc)
        : rows.OrderBy(r => r.Killed).ThenByDescending(r => r.DamageTaken).ThenByDescending(r => r.LastActivityUtc);

    // ── Empty states ──

    private void UpdateStatePanel(OverlayPhase phase, MeterSnapshot s, OverlayStatus status, bool pvp)
    {
        _stateExtra.Content = null;
        string iconKey = ThemeKeys.Accent;
        Glyphs.Glyph glyph = Glyphs.Waiting;
        string title, body;
        switch (phase)
        {
            case OverlayPhase.NpcapMissing:
                glyph = WarningGlyph;
                iconKey = ThemeKeys.Negative;
                title = "Npcap is not installed";
                body = "Aion2Dps reads the game's own network traffic through the free Npcap driver (read-only). " +
                       "Install it with \"WinPcap API-compatible mode\" ticked, then restart Aion2Dps.";
                var link = Ui.LinkText("Install Npcap  →  npcap.com/#download", "https://npcap.com/#download", 12);
                link.FontWeight = FontWeights.SemiBold;
                _stateExtra.Content = link;
                break;
            case OverlayPhase.CaptureError:
                glyph = WarningGlyph;
                iconKey = ThemeKeys.Negative;
                title = "Capture error";
                body = string.IsNullOrWhiteSpace(status.Capture.Message) ? "The capture stopped unexpectedly. See the log in the dashboard." : status.Capture.Message;
                break;
            case OverlayPhase.CaptureStopped:
                title = "Capture stopped";
                body = "Start capturing from the dashboard (Meter page).";
                break;
            case OverlayPhase.WaitingForGame:
                title = "Waiting for AION 2";
                body = "Start the game and log in. The meter attaches automatically once it sees the game connection.";
                break;
            case OverlayPhase.Detecting:
                iconKey = ThemeKeys.Warning;
                title = "Detecting the game connection…";
                body = "Checking network flows for the game's heartbeat. This takes a few seconds after login or a zone change.";
                break;
            case OverlayPhase.WaitingForData:
                title = "Waiting for game data";
                body = "Connected to the game. Waiting for the first combat-relevant packets.";
                break;
            case OverlayPhase.WaitingForCombat:
                glyph = Glyphs.Dummy;
                title = "Waiting for combat";
                body = "Hit something to start an encounter." + (s.LocalPlayer is { } lp ? $"  Tracking {lp.Name}." : "");
                break;
            default:
                glyph = pvp ? Glyphs.Pvp : Glyphs.Dummy;
                title = pvp ? "No opponents yet" : _options.View switch
                {
                    MeterView.Taken => "No damage taken yet",
                    MeterView.Heal => "No healing yet",
                    _ => "Waiting for damage…",
                };
                body = pvp ? "Enemy players you trade damage with appear here, with a KILLED tag when they fall." : "Rows appear with the first counted hit.";
                break;
        }
        _stateIcon.Data = glyph.Fill;
        _stateIcon.Ref(System.Windows.Shapes.Shape.FillProperty, iconKey);
        _stateTitle.Text = title;
        _stateBody.Text = body;
    }

    private static readonly Glyphs.Glyph WarningGlyph = new(FrozenGeometry("F0 M8,1 L15.5,14.5 L0.5,14.5 Z M7.1,5.5 L8.9,5.5 L8.6,10.5 L7.4,10.5 Z M7.1,11.6 L8.9,11.6 L8.9,13.2 L7.1,13.2 Z"), null);

    private static Geometry FrozenGeometry(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    // ── Footer ──

    private void UpdateFooter(MeterSnapshot s, OverlayStatus status, OverlayPhase phase, bool pvp)
    {
        (string text, string key) = phase switch
        {
            OverlayPhase.NpcapMissing => ("Npcap missing", ThemeKeys.Negative),
            OverlayPhase.CaptureError => ("Capture error", ThemeKeys.Negative),
            OverlayPhase.CaptureStopped => ("Stopped", ThemeKeys.TextMuted),
            OverlayPhase.WaitingForGame => ("Waiting for game", ThemeKeys.TextMuted),
            OverlayPhase.Detecting => ("Detecting", ThemeKeys.Warning),
            OverlayPhase.WaitingForData => ("Connected", ThemeKeys.Warning),
            OverlayPhase.WaitingForCombat => ("Idle", ThemeKeys.Accent),
            OverlayPhase.Ended => ("Ended", ThemeKeys.Accent),
            _ => (status.Capture.State == CaptureState.Replaying ? "Replay" : "Live", ThemeKeys.Positive),
        };
        _dot.Ref(System.Windows.Shapes.Shape.FillProperty, key);
        if (status.Flash is { Length: > 0 } flash)
        {
            _footerState.Text = flash;
            _footerState.Ref(TextBlock.ForegroundProperty, ThemeKeys.Accent);
        }
        else
        {
            _footerState.Text = phase is OverlayPhase.Live or OverlayPhase.Ended ? $"{text} · {Fmt.Duration(s.Elapsed)}" : text;
            _footerState.Ref(TextBlock.ForegroundProperty, ThemeKeys.TextMuted);
        }
        _footerState.ToolTip = $"Capture: {status.Capture.State}" +
                               (string.IsNullOrEmpty(status.Capture.ServerEndpoint) ? "" : $" · server {status.Capture.ServerEndpoint}") +
                               (status.Capture.GapCount > 0 ? $" · {status.Capture.GapCount} TCP gaps" : "");
        _footerPing.Text = s.PingMs is not null ? "· " + Fmt.Ping(s.PingMs) : "";
        _footerPing.ToolTip = "Latency to the game server";

        if (pvp)
        {
            long dealt = s.PvpRows.Sum(r => r.DamageDealt), taken = s.PvpRows.Sum(r => r.DamageTaken);
            _footerStats.Text = s.PvpRows.Count == 0 ? "" : $"dealt {Fmt.Abbrev(dealt)} · taken {Fmt.Abbrev(taken)}";
            _footerStats.ToolTip = $"Dealt {Fmt.Exact(dealt)} · taken {Fmt.Exact(taken)}";
            return;
        }
        var dpsRows = s.Rows.Where(r => r.Kind != CombatantKind.EnemyPlayer && r.Damage > 0).ToList();
        if (dpsRows.Count == 0)
        {
            _footerStats.Text = "";
            _footerStats.ToolTip = null;
            return;
        }
        if (s.PartialView)
        {
            var me = dpsRows.FirstOrDefault(r => r.IsLocal);
            _footerStats.Text = me is null ? "partial view" : $"partial view · you {Fmt.Percent(me.Contribution, 1)} of boss HP";
            _footerStats.ToolTip = (s.PartialViewText ?? "Partial view") + $"\nVisible damage {Fmt.Exact(s.TotalDamage)}";
            return;
        }
        double min = s.MinDps > 0 ? s.MinDps : dpsRows.Min(r => r.Dps);
        double avg = s.AvgDps > 0 ? s.AvgDps : dpsRows.Average(r => r.Dps);
        double max = s.MaxDps > 0 ? s.MaxDps : dpsRows.Max(r => r.Dps);
        _footerStats.Text = $"min {Fmt.Abbrev(min)} · avg {Fmt.Abbrev(avg)} · max {Fmt.Abbrev(max)}";
        _footerStats.ToolTip = $"Party DPS {Fmt.Exact(s.PartyDps)} · total {Fmt.Exact(s.TotalDamage)}\nmin {Fmt.Exact(min)} · avg {Fmt.Exact(avg)} · max {Fmt.Exact(max)}";
    }

    // ── Toast & training menu ──

    /// <summary>Shows a dismissable banner under the header (personal best, training notices).</summary>
    public void ShowToast(string title, string message, TimeSpan? duration = null)
    {
        _toastTitle.Text = title;
        _toastBody.Text = message;
        _toastShowing = true;
        _toast.Visibility = Visibility.Visible;
        _toast.BeginAnimation(OpacityProperty, null);
        if (AnimationsEnabled)
        {
            _toast.Opacity = 0;
            _toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
        }
        else _toast.Opacity = 1;
        _toastTimer.Stop();
        _toastTimer.Interval = duration ?? TimeSpan.FromSeconds(8);
        if (AnimationsEnabled) _toastTimer.Start();
    }

    public void HideToast()
    {
        _toastTimer.Stop();
        _toastShowing = false;
        if (!AnimationsEnabled) { _toast.Visibility = Visibility.Collapsed; return; }
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300));
        fade.Completed += (_, _) => { if (!_toastShowing) _toast.Visibility = Visibility.Collapsed; };
        _toast.BeginAnimation(OpacityProperty, fade);
    }

    private void OpenTrainingMenu()
    {
        var menu = new ContextMenu { PlacementTarget = _trainingButton, Placement = PlacementMode.Bottom };
        if (TrainingActive)
        {
            var stop = new MenuItem { Header = "Stop training" };
            stop.Click += (_, _) => TrainingCancelRequested?.Invoke();
            menu.Items.Add(stop);
        }
        else
        {
            var header = new MenuItem { Header = "Start a training run", IsEnabled = false };
            menu.Items.Add(header);
            foreach (var (label, secs) in new[] { ("30 seconds", 30), ("1 minute", 60), ("2 minutes", 120), ("3 minutes", 180), ("5 minutes", 300) })
            {
                var item = new MenuItem { Header = label };
                item.Click += (_, _) => TrainingRequested?.Invoke(TimeSpan.FromSeconds(secs));
                menu.Items.Add(item);
            }
        }
        menu.Opened += (_, _) => MenuOpen = true;
        menu.Closed += (_, _) => MenuOpen = false;
        menu.IsOpen = true;
    }
}
