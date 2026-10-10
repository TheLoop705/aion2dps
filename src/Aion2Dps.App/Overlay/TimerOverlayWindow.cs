using System.Windows.Interop;
using Aion2Dps.App.Controls;
using Aion2Dps.App.Infrastructure;
using Aion2Dps.App.Settings;

namespace Aion2Dps.App.Overlay;

/// <summary>
/// The timers window's content: a small "TIMERS" header and every timer due soon (name · time left), soonest first, on
/// the overlay's own background. Shown next to the DPS overlay at all times, in and out of combat.
/// </summary>
public sealed class TimerOverlayView : Grid
{
    public const int MaxLines = 8;

    private readonly Border _bg = new();
    private readonly Border _frame = new() { BorderThickness = new Thickness(1), IsHitTestVisible = false };
    private readonly TextBlock _title = Ui.Text("TIMERS", ThemeKeys.TextMuted, 9.5, FontWeights.Bold);
    private readonly TextBlock _window = Ui.Text("", ThemeKeys.TextMuted, 9.5);
    private readonly StackPanel _lines = new();
    private IReadOnlyList<UpcomingTimer>? _shown;

    public TimerOverlayView()
    {
        Background = Brushes.Transparent; // hit-testable everywhere (drag to move, click to open the Timers page)
        Cursor = Cursors.Hand;
        _bg.Ref(Border.BackgroundProperty, ThemeKeys.WindowBackground).Ref(Border.CornerRadiusProperty, ThemeKeys.CornerRadius);
        _frame.Ref(Border.BorderBrushProperty, ThemeKeys.Border).Ref(Border.CornerRadiusProperty, ThemeKeys.CornerRadius);
        Children.Add(_bg);

        var body = new StackPanel { Margin = new Thickness(10, 6, 10, 8) };
        var head = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 3) };
        DockPanel.SetDock(_window, Dock.Right);
        head.Children.Add(_window);
        var icon = Ui.Icon("", ThemeKeys.Accent, 9.5);
        icon.Margin = new Thickness(0, 1, 5, 0);
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        _title.Ref(TextBlock.ForegroundProperty, ThemeKeys.Accent);
        head.Children.Add(_title);
        body.Children.Add(head);
        body.Children.Add(_lines);
        Children.Add(body);
        Children.Add(_frame);
        ToolTip = "Timers due soon. Click to open Dashboard → Timers · drag to move (while unlocked).";
    }

    public double BackgroundOpacity
    {
        get => _bg.Opacity;
        set => _bg.Opacity = value;
    }

    /// <summary>The texts of the lines (tests).</summary>
    public IEnumerable<string> LineTexts => _lines.Children.OfType<FrameworkElement>()
        .SelectMany(e => e is Panel p ? p.Children.OfType<TextBlock>() : e is TextBlock t ? [t] : [])
        .Select(t => t.Text);

    public void Update(IReadOnlyList<UpcomingTimer> upcoming, int lookAheadMinutes)
    {
        _window.Text = lookAheadMinutes % 60 == 0 ? $"next {lookAheadMinutes / 60} h" : $"next {lookAheadMinutes} min";
        if (_shown is not null && _shown.SequenceEqual(upcoming)) return;
        _shown = upcoming.ToList();
        _lines.Children.Clear();
        if (upcoming.Count == 0)
        {
            var none = Ui.Text("Nothing due", ThemeKeys.TextMuted, 11.5);
            none.Margin = new Thickness(0, 1, 0, 0);
            _lines.Children.Add(none);
            return;
        }
        foreach (var u in upcoming.Take(MaxLines))
        {
            var line = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 1, 0, 0) };
            var when = Ui.Text(u.When, u.Live ? ThemeKeys.Positive : ThemeKeys.Text, 11, FontWeights.SemiBold, mono: true);
            when.Margin = new Thickness(10, 0, 0, 0);
            DockPanel.SetDock(when, Dock.Right);
            line.Children.Add(when);
            var star = Ui.Icon(u.Starred ? "" : "", u.Starred ? ThemeKeys.Warning : ThemeKeys.TextMuted, 9);
            star.Width = 15;
            star.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(star, Dock.Left);
            line.Children.Add(star);
            var name = Ui.Text(u.Name, u.Live ? ThemeKeys.Text : ThemeKeys.TextMuted, 11.5);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            line.Children.Add(name);
            _lines.Children.Add(line);
        }
        if (upcoming.Count > MaxLines)
        {
            var more = Ui.Text($"+{upcoming.Count - MaxLines} more", ThemeKeys.TextMuted, 10);
            more.Margin = new Thickness(15, 1, 0, 0);
            _lines.Children.Add(more);
        }
    }
}

/// <summary>
/// The always-on timers window: borderless, transparent, topmost and non-activating like the DPS overlay
/// (WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW, click-through while the overlay is locked with click-through). Drag to move
/// while unlocked (position kept in <see cref="TimerSettings"/>); a click without moving raises <see cref="Clicked"/>.
/// </summary>
public sealed class TimerOverlayWindow : Window
{
    public const double DefaultWidth = 230;
    private const double DragThreshold = 3;
    private IntPtr _hwnd;
    private bool _locked, _clickThrough, _pressed, _moved;
    private Point _pressAt;

    public TimerOverlayWindow(TimerSettings settings, OverlaySettings overlay)
    {
        Title = "Aion2Dps timers";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = DefaultWidth;
        WindowStartupLocation = WindowStartupLocation.Manual;
        (Left, Top) = InitialPosition(settings, overlay);
        View = new TimerOverlayView { BackgroundOpacity = overlay.BackgroundOpacity };
        Content = View;
        View.MouseLeftButtonDown += OnDown;
        View.MouseMove += OnMove;
        View.MouseLeftButtonUp += OnUp;
        View.LostMouseCapture += (_, _) => _pressed = false;
    }

    public TimerOverlayView View { get; }

    /// <summary>Pressed and released without dragging.</summary>
    public event Action? Clicked;

    /// <summary>The user dragged the window to a new place (persist <see cref="Window.Left"/>/<see cref="Window.Top"/>).</summary>
    public event Action? Moved;

    private static (double Left, double Top) InitialPosition(TimerSettings s, OverlaySettings o)
    {
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
        double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
        if (s.WindowLeft is { } l && s.WindowTop is { } t && double.IsFinite(l) && double.IsFinite(t)
            && l > vl - DefaultWidth + 60 && l < vl + vw - 60 && t > vt - 10 && t < vt + vh - 60)
            return (l, t);
        // Default: just left of the DPS overlay's default spot (top right of the work area).
        var wa = SystemParameters.WorkArea;
        double overlayLeft = o.Left ?? wa.Right - o.Width - 40;
        double overlayTop = o.Top ?? wa.Top + 120;
        double left = overlayLeft - DefaultWidth - 12;
        return (left < wa.Left ? overlayLeft : left, overlayTop);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        long ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        ex |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, ex);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        ApplyClickThrough();
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_MOUSEACTIVATE) return IntPtr.Zero;
        handled = true;
        return new IntPtr(NativeMethods.MA_NOACTIVATE);
    }

    /// <summary>Follows the DPS overlay: locked = no drag; click-through = the mouse goes to the game.</summary>
    public void SetLockState(bool locked, bool clickThrough)
    {
        _locked = locked;
        _clickThrough = locked && clickThrough;
        ApplyClickThrough();
    }

    private void ApplyClickThrough()
    {
        if (_hwnd == IntPtr.Zero) return;
        long ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        ex = _clickThrough ? ex | NativeMethods.WS_EX_TRANSPARENT : ex & ~(long)NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, ex);
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1) return;
        _pressed = true;
        _moved = false;
        _pressAt = e.GetPosition(this);
        View.CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_pressed || _locked || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        var d = p - _pressAt;
        if (!_moved && Math.Abs(d.X) < DragThreshold && Math.Abs(d.Y) < DragThreshold) return;
        _moved = true;
        Left += d.X;
        Top += d.Y;
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        View.ReleaseMouseCapture();
        e.Handled = true;
        if (_moved) Moved?.Invoke();
        else Clicked?.Invoke();
    }
}
