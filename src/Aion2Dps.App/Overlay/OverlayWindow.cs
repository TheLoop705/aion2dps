using System.Windows.Interop;
using Aion2Dps.App.Infrastructure;
using Aion2Dps.App.Settings;

namespace Aion2Dps.App.Overlay;

/// <summary>
/// Borderless, transparent, topmost, NON-ACTIVATING host for <see cref="OverlayView"/>: WS_EX_NOACTIVATE +
/// WS_EX_TOOLWINDOW (never steals focus from the game, not in Alt+Tab) and WM_MOUSEACTIVATE → MA_NOACTIVATE.
/// Drag the header to move, drag the grip to resize the width (height auto-fits). Optional click-through
/// (WS_EX_TRANSPARENT) while locked.
/// <para>
/// Two presentations, switched in place (<see cref="SetPresentation"/>): the full meter (width = the user's expanded
/// width, height fits the rows) and the compact bar (width and height fit the bar). The user's top-left
/// (<see cref="Anchor"/>) is kept across switches; while the shrink feature is on, a window that would overflow the
/// monitor's work area is shifted up/left temporarily and returns to the anchor when it shrinks again.
/// </para>
/// </summary>
public sealed class OverlayWindow : Window
{
    private const double MinExpandedWidth = 300, MaxExpandedWidth = 1000, DragThreshold = 3;
    private IntPtr _hwnd;
    private bool _dragging;
    private bool _moved;
    private FrameworkElement? _pressHandle;
    private Point _dragStart;
    private bool _locked;
    private bool _clickThrough;
    private bool _compact;
    private bool _keepInWorkArea;
    private Point _anchor;
    private double _expandedWidth;

    public OverlayWindow(OverlaySettings settings)
    {
        Title = "Aion2Dps overlay";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        _expandedWidth = settings.Width;
        Width = settings.Width;
        MinWidth = MinExpandedWidth;
        MaxWidth = MaxExpandedWidth;
        WindowStartupLocation = WindowStartupLocation.Manual;
        (Left, Top) = InitialPosition(settings);
        _anchor = new Point(Left, Top);

        View = new OverlayView();
        Content = View;
        foreach (var handle in new FrameworkElement[] { View.DragHandle, View.CompactBar })
        {
            handle.MouseLeftButtonDown += OnHandleDown;
            handle.MouseMove += OnHandleMove;
            handle.MouseLeftButtonUp += OnHandleUp;
            handle.LostMouseCapture += (_, _) => EndDrag();
        }
        View.ResizeGrip.DragDelta += (_, e) =>
        {
            if (_locked || _compact) return;
            Width = _expandedWidth = Math.Clamp(Width + e.HorizontalChange, MinExpandedWidth, MaxExpandedWidth);
        };
        View.ResizeGrip.DragCompleted += (_, _) => BoundsChanged?.Invoke();
        MouseEnter += (_, _) => UpdateAdjustBar();
        MouseLeave += (_, _) => UpdateAdjustBar();
        SizeChanged += (_, _) => { if (_keepInWorkArea) Place(); };
    }

    public OverlayView View { get; }

    /// <summary>Raised after the user moved or resized the overlay (persist <see cref="Anchor"/> and <see cref="ExpandedWidth"/>).</summary>
    public event Action? BoundsChanged;

    /// <summary>The compact bar was clicked (pressed and released without dragging).</summary>
    public event Action? CompactBarClicked;

    public bool IsLocked => _locked;

    /// <summary>True while the compact bar is shown.</summary>
    public bool IsCompact => _compact;

    /// <summary>The user's top-left (where they dragged the overlay), independent of any temporary work-area shift.</summary>
    public Point Anchor => _anchor;

    /// <summary>The full overlay's width (the user's choice; the compact bar's width is not remembered).</summary>
    public double ExpandedWidth => _expandedWidth;

    private static (double Left, double Top) InitialPosition(OverlaySettings s)
    {
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
        double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
        if (s.Left is { } l && s.Top is { } t && l > vl - s.Width + 60 && l < vl + vw - 60 && t > vt - 10 && t < vt + vh - 60)
            return (l, t);
        var wa = SystemParameters.WorkArea;
        return (wa.Right - s.Width - 40, wa.Top + 120);
    }

    /// <summary>
    /// Shows the compact bar or the full overlay. Switches the content in place and re-sizes the same window (no
    /// re-creation, never activates it). <paramref name="keepInWorkArea"/>: shift the window up/left while it would
    /// overflow the monitor's work area (the shrink feature; off = exactly the classic overlay behaviour).
    /// </summary>
    public void SetPresentation(bool compact, bool keepInWorkArea)
    {
        bool fitChanged = keepInWorkArea != _keepInWorkArea;
        _keepInWorkArea = keepInWorkArea;
        if (compact == _compact && View.IsCompact == compact)
        {
            if (fitChanged) Place();
            return;
        }
        _compact = compact;
        if (compact)
        {
            SizeToContent = SizeToContent.WidthAndHeight;
            MinWidth = 0;
        }
        else
        {
            SizeToContent = SizeToContent.Height;
            MinWidth = MinExpandedWidth;
            Width = _expandedWidth;
        }
        View.SetCompact(compact);
        UpdateAdjustBar();
        // Size the window now (same frame) so the anchor/work-area placement uses the new size: no visible jump.
        if (IsVisible) UpdateLayout();
        Place();
    }

    /// <summary>Puts the window at its anchor, shifted inside the work area when needed (never while dragging).</summary>
    private void Place()
    {
        if (_dragging) return;
        var target = _anchor;
        if (_keepInWorkArea && _hwnd != IntPtr.Zero && ActualWidth > 0 && ActualHeight > 0)
            target = OverlayGeometry.FitInside(_anchor, new Size(ActualWidth, ActualHeight), WorkArea());
        if (Math.Abs(Left - target.X) > 0.5) Left = target.X;
        if (Math.Abs(Top - target.Y) > 0.5) Top = target.Y;
    }

    /// <summary>Work area (taskbar excluded) of the monitor the overlay is on, in device-independent pixels.</summary>
    private Rect WorkArea()
    {
        try
        {
            var monitor = NativeMethods.MonitorFromWindow(_hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                var fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
                var tl = fromDevice.Transform(new Point(info.rcWork.Left, info.rcWork.Top));
                var br = fromDevice.Transform(new Point(info.rcWork.Right, info.rcWork.Bottom));
                return new Rect(tl, br);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Overlay", $"Monitor work area unavailable: {ex.Message}");
        }
        return SystemParameters.WorkArea;
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

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    /// <summary>Lock (no drag/resize, no adjust bar) and optional click-through (mouse goes to the game).</summary>
    public void SetLockState(bool locked, bool clickThrough)
    {
        _locked = locked;
        _clickThrough = locked && clickThrough;
        ApplyClickThrough();
        UpdateAdjustBar();
    }

    private void ApplyClickThrough()
    {
        if (_hwnd == IntPtr.Zero) return;
        long ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        ex = _clickThrough ? ex | NativeMethods.WS_EX_TRANSPARENT : ex & ~(long)NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, ex);
    }

    private void UpdateAdjustBar() => View.AdjustBarVisible = !_locked && !_compact && IsMouseOver;

    // The header (expanded) and the compact bar both drag the window; a press-and-release on the compact bar without
    // movement is a click (expand). While locked nothing moves, but the compact bar still expands on click. With
    // click-through on, WS_EX_TRANSPARENT sends every click to the game instead, so the bar never swallows clicks.

    private void OnHandleDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1) return;
        _pressHandle = (FrameworkElement)sender;
        _moved = false;
        if (_locked) return;
        _dragging = true;
        _dragStart = e.GetPosition(this);
        _pressHandle.CaptureMouse();
        e.Handled = true;
    }

    private void OnHandleMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(this);
        double dx = p.X - _dragStart.X, dy = p.Y - _dragStart.Y;
        if (!_moved && Math.Abs(dx) + Math.Abs(dy) < DragThreshold) return;
        _moved = true;
        Left += dx;
        Top += dy;
    }

    private void OnHandleUp(object sender, MouseButtonEventArgs e)
    {
        bool click = ReferenceEquals(_pressHandle, sender) && !_moved && ReferenceEquals(sender, View.CompactBar);
        _pressHandle = null;
        if (_dragging)
        {
            ((FrameworkElement)sender).ReleaseMouseCapture();
            EndDrag();
        }
        if (click)
        {
            e.Handled = true;
            CompactBarClicked?.Invoke();
        }
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        if (!_moved) return;
        _anchor = new Point(Left, Top);
        BoundsChanged?.Invoke();
    }
}
