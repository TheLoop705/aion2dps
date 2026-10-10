using Aion2Dps.App.Controls;
using Aion2Dps.App.Formatting;
using Aion2Dps.App.Settings;

namespace Aion2Dps.App.Overlay;

/// <summary>What the compact (out-of-combat) bar shows. Pure projection of the snapshot + overlay status.</summary>
public sealed record CompactBarModel
{
    /// <summary>Short status ("Waiting for combat", "Npcap missing", or the transient flash text).</summary>
    public string Status { get; init; } = "";
    /// <summary>Theme brush key of the status dot (Positive = capturing fine, Warning/Negative = attention).</summary>
    public string DotKey { get; init; } = ThemeKeys.Positive;
    /// <summary>The status is a transient flash message (drawn in the accent colour).</summary>
    public bool IsFlash { get; init; }
    /// <summary>Zone / map name, when known.</summary>
    public string? Zone { get; init; }
    /// <summary>Optional small hint (your DPS in the last fight, the training countdown); dropped when it does not fit.</summary>
    public string? Hint { get; init; }
    /// <summary>Tiny caption in front of the hint ("LAST" = last fight, "DPS" = live); null = none.</summary>
    public string? HintLabel { get; init; }
    public string? HintTooltip { get; init; }
    /// <summary>The hint matters more than the status (your DPS in the finished fight): trim the status, keep the hint.</summary>
    public bool KeepHint { get; init; }
    public string Tooltip { get; init; } = "";
    /// <summary>Timers due soon (shown under the bar, at most <see cref="MaxUpcoming"/>).</summary>
    public IReadOnlyList<UpcomingTimer> Upcoming { get; init; } = [];
    /// <summary>Timers due soon that did not fit the list.</summary>
    public int MoreUpcoming { get; init; }

    public const int MaxUpcoming = 6;

    public bool Equals(CompactBarModel? other) =>
        other is not null && Status == other.Status && DotKey == other.DotKey && IsFlash == other.IsFlash && Zone == other.Zone
        && Hint == other.Hint && HintLabel == other.HintLabel && HintTooltip == other.HintTooltip && KeepHint == other.KeepHint
        && Tooltip == other.Tooltip && MoreUpcoming == other.MoreUpcoming && Upcoming.SequenceEqual(other.Upcoming);

    public override int GetHashCode() => HashCode.Combine(Status, Zone, Hint, Upcoming.Count);

    public static CompactBarModel Build(MeterSnapshot s, OverlayStatus status)
    {
        var phase = OverlayPhaseLogic.Compute(status.Capture, s);
        string text, dot = ThemeKeys.Positive, detail;
        string? hint = null, hintLabel = null, hintTip = null;
        switch (phase)
        {
            case OverlayPhase.NpcapMissing:
                (text, dot, detail) = ("Npcap missing", ThemeKeys.Negative, "Npcap is not installed, so nothing is captured. Expand for the download link.");
                break;
            case OverlayPhase.CaptureError:
                (text, dot, detail) = ("Capture error", ThemeKeys.Negative,
                    string.IsNullOrWhiteSpace(status.Capture.Message) ? "The capture stopped unexpectedly." : status.Capture.Message);
                break;
            case OverlayPhase.CaptureStopped:
                (text, dot, detail) = ("Capture stopped", ThemeKeys.TextMuted, "Start capturing from the dashboard (Meter page).");
                break;
            case OverlayPhase.WaitingForGame:
                (text, dot, detail) = ("Waiting for game", ThemeKeys.TextMuted, "Start AION 2 and log in; the meter attaches automatically.");
                break;
            case OverlayPhase.Detecting:
                (text, dot, detail) = ("Detecting game", ThemeKeys.Warning, "Checking network flows for the game connection.");
                break;
            case OverlayPhase.WaitingForData:
                (text, dot, detail) = ("Waiting for data", ThemeKeys.Warning, "Connected to the game; waiting for the first combat packets.");
                break;
            case OverlayPhase.Live:
                (text, dot, detail) = ($"In combat · {Fmt.Duration(s.Elapsed)}", ThemeKeys.Positive, "A fight is running (collapsed by you).");
                if (LocalDps(s) is { } live)
                {
                    (hint, hintLabel, hintTip) = (Fmt.Abbrev(live), "DPS", "Your live DPS");
                }
                break;
            case OverlayPhase.Ended:
            {
                string result = s.Outcome switch
                {
                    EncounterOutcome.Kill => "Kill · " + Fmt.Duration(s.Elapsed),
                    EncounterOutcome.Wipe => "Wipe · " + Fmt.Duration(s.Elapsed),
                    _ => Fmt.Duration(s.Elapsed),
                };
                string target = s.Target?.Name?.Trim() ?? "";
                text = target.Length > 0 ? $"{target} · {result}" : s.Outcome is EncounterOutcome.Kill or EncounterOutcome.Wipe ? result : "Fight ended";
                detail = "The last fight is kept until the next one starts; click to see its bars.";
                if (LocalDps(s) is { } mine) (hint, hintLabel, hintTip) = (Fmt.Abbrev(mine), "YOU", $"Your DPS in this fight: {Fmt.Exact(mine)}");
                break;
            }
            default:
                // Only the engine knows whether a run is armed (a reset disarms it); the overlay's countdown is just a hint.
                if (IsTrainingArmed(s))
                {
                    (text, dot, detail) = ("Training ready", ThemeKeys.Accent, "Hit a target to start the training run.");
                    if (status.TrainingRemaining is { } rem)
                    {
                        hint = Fmt.Duration(rem);
                        hintTip = "Training time left";
                    }
                }
                else
                {
                    (text, dot, detail) = ("Waiting for combat", ThemeKeys.Positive,
                        s.LocalPlayer is { } lp ? $"Capturing. Tracking {lp.Name}." : "Capturing. Hit something to start an encounter.");
                }
                break;
        }

        if (hint is null && phase is not OverlayPhase.Live && status.LastFightDps is { } last && double.IsFinite(last) && last > 0)
        {
            (hint, hintLabel, hintTip) = (Fmt.Abbrev(last), "LAST", $"Your DPS in the last fight: {Fmt.Exact(last)}");
        }

        bool flash = status.Flash is { Length: > 0 };
        string zone = s.MapName?.Trim() ?? "";
        return new CompactBarModel
        {
            Status = flash ? status.Flash! : text,
            DotKey = dot,
            IsFlash = flash,
            Zone = zone.Length > 0 ? zone : null,
            Hint = hint,
            HintLabel = hint is null ? null : hintLabel,
            HintTooltip = hintTip,
            KeepHint = hint is not null && phase == OverlayPhase.Ended,
            Tooltip = $"{detail}\nClick to expand the overlay · drag to move.",
            Upcoming = status.Upcoming.Take(MaxUpcoming).ToList(),
            MoreUpcoming = Math.Max(0, status.Upcoming.Count - MaxUpcoming),
        };
    }

    /// <summary>The engine reports an armed training run (SnapshotBuilder: "Training ready (60 s): hit a target to start").</summary>
    internal static bool IsTrainingArmed(MeterSnapshot s) =>
        s.State == MeterState.WaitingForCombat && s.StatusText.StartsWith("Training ready", StringComparison.OrdinalIgnoreCase);

    private static double? LocalDps(MeterSnapshot s) =>
        s.Rows.FirstOrDefault(r => r.IsLocal && r.Damage > 0) is { } me ? me.Dps : null;
}

/// <summary>Per-row-size metrics of the compact bar.</summary>
internal readonly record struct CompactMetrics(double Height, double StatusSize, double ZoneSize, double HintSize)
{
    public static CompactMetrics For(RowSize size) => size switch
    {
        RowSize.Normal => new CompactMetrics(28, 12, 11.5, 10.5),
        RowSize.Compact => new CompactMetrics(25, 11.5, 11, 10),
        _ => new CompactMetrics(22, 10.5, 10, 9.5),
    };
}

/// <summary>
/// The slim out-of-combat bar: status dot · status · zone · last-fight hint · expand chevron. Width fits its content
/// between <see cref="MinBarWidth"/> and <see cref="MaxBarWidth"/>: the hint is dropped first, then the zone trims.
/// Drawn on the overlay's own background (same corner radius and opacity), so it matches every theme.
/// </summary>
public sealed class CompactBarView : Border
{
    public const double MinBarWidth = 150;
    public const double MaxBarWidth = 280;

    private readonly System.Windows.Shapes.Ellipse _halo = Ui.Dot(13, ThemeKeys.Positive);
    private readonly System.Windows.Shapes.Ellipse _dot = Ui.Dot(7, ThemeKeys.Positive);
    private readonly TextBlock _status = Ui.Text("", ThemeKeys.Text, 12, FontWeights.SemiBold);
    private readonly Border _divider = new() { Width = 1, Height = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _zone = Ui.Text("", ThemeKeys.TextMuted, 11.5);
    private readonly Border _hint = new() { Padding = new Thickness(5, 0, 5, 1), VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(3) };
    private readonly TextBlock _hintLabel = Ui.Text("", ThemeKeys.TextMuted, 8, FontWeights.Bold);
    private readonly TextBlock _hintText = Ui.Text("", ThemeKeys.Text, 10.5, FontWeights.SemiBold, mono: true);
    private readonly Bar _panel;
    private readonly StackPanel _upcoming = new() { Margin = new Thickness(2, 0, 8, 6) };

    public CompactBarView()
    {
        Padding = new Thickness(9, 0, 3, 0);
        MinWidth = MinBarWidth;
        MaxWidth = MaxBarWidth;
        HorizontalAlignment = HorizontalAlignment.Left;
        Background = Brushes.Transparent; // hit-testable everywhere (click to expand, drag to move)
        Cursor = Cursors.Hand;

        _halo.Opacity = 0.28;
        var dotHost = new Grid { Width = 13, Height = 13, VerticalAlignment = VerticalAlignment.Center };
        _dot.HorizontalAlignment = HorizontalAlignment.Center;
        _halo.HorizontalAlignment = HorizontalAlignment.Center;
        dotHost.Children.Add(_halo);
        dotHost.Children.Add(_dot);
        _divider.Ref(BackgroundProperty, Theming.AppThemeKeys.Divider);
        _hint.Ref(BackgroundProperty, ThemeKeys.BarTrack);
        _hintLabel.Opacity = 0.85;
        _hintLabel.Margin = new Thickness(0, 1, 4, 0);
        _hintText.Opacity = 0.9;
        var hintRow = new StackPanel { Orientation = Orientation.Horizontal };
        hintRow.Children.Add(_hintLabel);
        hintRow.Children.Add(_hintText);
        _hint.Child = hintRow;
        Chevron = Ui.IconButton("", "Expand the overlay", () => ExpandRequested?.Invoke(), 9);
        Chevron.Width = 20;
        Chevron.Height = 18;
        Chevron.VerticalAlignment = VerticalAlignment.Center;

        _panel = new Bar(dotHost, _status, _divider, _zone, _hint, Chevron);
        var stack = new StackPanel();
        stack.Children.Add(_panel);
        stack.Children.Add(_upcoming);
        Child = stack;
        Configure(RowSize.Compact);
    }

    /// <summary>The chevron (also: clicking anywhere on the bar expands, handled by the window so drags still move it).</summary>
    public Button Chevron { get; }

    public event Action? ExpandRequested;

    public CompactBarModel? Model { get; private set; }

    public void Configure(RowSize size)
    {
        var m = CompactMetrics.For(size);
        _panel.Height = m.Height;
        _upcomingSize = m.ZoneSize;
        _status.FontSize = m.StatusSize;
        _zone.FontSize = m.ZoneSize;
        _hintText.FontSize = m.HintSize;
        _hintLabel.FontSize = Math.Max(7.5, m.HintSize - 2.5);
        _divider.Height = Math.Round(m.Height * 0.45);
    }

    public void Update(CompactBarModel model, RowSize size)
    {
        Configure(size);
        if (model == Model) return;
        Model = model;
        _dot.Ref(System.Windows.Shapes.Shape.FillProperty, model.DotKey);
        _halo.Ref(System.Windows.Shapes.Shape.FillProperty, model.DotKey);
        _status.Text = model.Status;
        _status.Ref(TextBlock.ForegroundProperty, model.IsFlash ? ThemeKeys.Accent : ThemeKeys.Text);
        _zone.Text = model.Zone ?? "";
        _zone.ToolTip = model.Zone;
        _hintText.Text = model.Hint ?? "";
        _hintLabel.Text = model.HintLabel ?? "";
        _hintLabel.Visibility = model.HintLabel is null ? Visibility.Collapsed : Visibility.Visible;
        _hint.ToolTip = model.HintTooltip;
        _panel.HasZone = model.Zone is not null;
        _panel.HasHint = model.Hint is not null;
        _panel.KeepHint = model.KeepHint;
        ToolTip = model.Tooltip;
        UpdateUpcoming(model);
        _panel.InvalidateMeasure();
    }

    private double _upcomingSize = 11.5;

    /// <summary>The upcoming timers under the bar: name (trimmed) · time left, one line each.</summary>
    private void UpdateUpcoming(CompactBarModel model)
    {
        _upcoming.Children.Clear();
        _upcoming.Visibility = model.Upcoming.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var u in model.Upcoming)
        {
            var line = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 1, 0, 0) };
            var when = Ui.Text(u.When, u.Live ? ThemeKeys.Positive : ThemeKeys.Text, _upcomingSize - 0.5, FontWeights.SemiBold, mono: true);
            when.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(when, Dock.Right);
            line.Children.Add(when);
            var star = Ui.Icon(u.Starred ? "\uE735" : "\uE823", u.Starred ? ThemeKeys.Warning : ThemeKeys.TextMuted, _upcomingSize - 2.5);
            star.Width = 15;
            star.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(star, Dock.Left);
            line.Children.Add(star);
            var name = Ui.Text(u.Name, ThemeKeys.TextMuted, _upcomingSize);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            line.Children.Add(name);
            _upcoming.Children.Add(line);
        }
        if (model.MoreUpcoming > 0)
        {
            var more = Ui.Text($"+{model.MoreUpcoming} more (Dashboard → Timers)", ThemeKeys.TextMuted, _upcomingSize - 1.5);
            more.Margin = new Thickness(15, 1, 0, 0);
            _upcoming.Children.Add(more);
        }
    }

    /// <summary>Lays the parts out in one line; drops the hint, then trims the zone, then the status, to stay within the max width.</summary>
    private sealed class Bar : Panel
    {
        private const double DotGap = 6, DividerGap = 7, HintGap = 7, ChevronGap = 4, MinZone = 36;
        private readonly UIElement _dot, _status, _divider, _zone, _hint, _chevron;
        private bool _showZone, _showHint;
        private double _zoneWidth, _statusWidth;

        public Bar(UIElement dot, UIElement status, UIElement divider, UIElement zone, UIElement hint, UIElement chevron)
        {
            (_dot, _status, _divider, _zone, _hint, _chevron) = (dot, status, divider, zone, hint, chevron);
            ClipToBounds = true;
            foreach (var c in new[] { dot, status, divider, zone, hint, chevron }) Children.Add(c);
        }

        public bool HasZone { get; set; }
        public bool HasHint { get; set; }
        public bool KeepHint { get; set; }

        protected override Size MeasureOverride(Size available)
        {
            var inf = new Size(double.PositiveInfinity, available.Height);
            foreach (UIElement c in Children) c.Measure(inf);
            // A little slack: a re-measure at exactly the desired width (layout rounding) must not drop the hint.
            double avail = double.IsInfinity(available.Width) ? double.PositiveInfinity : available.Width + 1.5;
            double fixedW = _dot.DesiredSize.Width + DotGap + ChevronGap + _chevron.DesiredSize.Width;
            double status = _status.DesiredSize.Width;
            double zoneBlock = HasZone ? _divider.DesiredSize.Width + 2 * DividerGap : 0;
            double zone = HasZone ? _zone.DesiredSize.Width : 0;
            double hint = HasHint ? HintGap + _hint.DesiredSize.Width : 0;
            _showZone = HasZone;
            _showHint = HasHint;

            if (KeepHint) fixedW += hint; // kept: the zone and then the status give way instead
            if (fixedW + status + zoneBlock + zone + (KeepHint ? 0 : hint) > avail && _showHint && !KeepHint)
            {
                _showHint = false;
                hint = 0;
            }
            if (fixedW + status + zoneBlock + zone > avail && _showZone)
            {
                zone = avail - fixedW - status - zoneBlock;
                if (zone < MinZone)
                {
                    _showZone = false;
                    zone = zoneBlock = 0;
                }
            }
            if (fixedW + status + zoneBlock + zone > avail) status = Math.Max(0, avail - fixedW);
            _zoneWidth = zone;
            _statusWidth = status;
            _status.Measure(new Size(status, available.Height));
            if (_showZone) _zone.Measure(new Size(zone, available.Height));
            double height = 0;
            foreach (UIElement c in Children) height = Math.Max(height, c.DesiredSize.Height);
            double width = fixedW + status + zoneBlock + zone + (KeepHint ? 0 : hint);
            if (!double.IsInfinity(available.Width)) width = Math.Min(width, available.Width);
            return new Size(width, double.IsInfinity(available.Height) ? height : Math.Min(height, available.Height));
        }

        protected override Size ArrangeOverride(Size final)
        {
            double h = final.Height;
            double x = 0;
            void Place(UIElement e, double w)
            {
                e.Arrange(new Rect(x, Math.Max(0, (h - e.DesiredSize.Height) / 2), w, Math.Min(h, e.DesiredSize.Height)));
                x += w;
            }
            // Dropped parts are parked outside the (clipped) panel: toggling Visibility here would feed back into Measure.
            void Hide(UIElement e) => e.Arrange(new Rect(final.Width + 1000, 0, e.DesiredSize.Width, e.DesiredSize.Height));

            // Absorb a sub-pixel shortfall (see the measure slack) in the zone, else the status.
            double zoneW = _zoneWidth, statusW = _statusWidth;
            double needed = _dot.DesiredSize.Width + DotGap + statusW + ChevronGap + _chevron.DesiredSize.Width
                            + (_showZone ? 2 * DividerGap + _divider.DesiredSize.Width + zoneW : 0)
                            + (_showHint ? HintGap + _hint.DesiredSize.Width : 0);
            double over = needed - final.Width;
            if (over > 0 && _showZone) { double cut = Math.Min(over, zoneW); zoneW -= cut; over -= cut; }
            if (over > 0) statusW = Math.Max(0, statusW - over);

            Place(_dot, _dot.DesiredSize.Width);
            x += DotGap;
            Place(_status, statusW);
            if (_showZone)
            {
                x += DividerGap;
                Place(_divider, _divider.DesiredSize.Width);
                x += DividerGap;
                Place(_zone, zoneW);
            }
            else
            {
                Hide(_divider);
                Hide(_zone);
            }
            if (_showHint)
            {
                x += HintGap;
                Place(_hint, _hint.DesiredSize.Width);
            }
            else Hide(_hint);
            // The chevron hugs the right edge (the bar may be arranged wider than needed, e.g. its minimum width).
            x = Math.Max(x + ChevronGap, final.Width - _chevron.DesiredSize.Width);
            Place(_chevron, _chevron.DesiredSize.Width);
            return final;
        }
    }
}
