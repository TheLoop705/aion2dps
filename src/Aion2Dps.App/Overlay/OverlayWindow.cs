using System.Windows.Interop;
using Aion2Dps.App.Infrastructure;
using Aion2Dps.App.Settings;

namespace Aion2Dps.App.Overlay;

/// <summary>
/// Borderless, transparent, topmost, NON-ACTIVATING host for <see cref="OverlayView"/>: WS_EX_NOACTIVATE +
/// WS_EX_TOOLWINDOW (never steals focus from the game, not in Alt+Tab) and WM_MOUSEACTIVATE → MA_NOACTIVATE.
/// Drag the header to move, drag the grip to resize the width (height auto-fits). Optional click-through
/// (WS_EX_TRANSPARENT) while locked.
/// </summary>
public sealed class OverlayWindow : Window
{
    private IntPtr _hwnd;
    private bool _dragging;
    private Point _dragStart;
    private bool _locked;
    private bool _clickThrough;

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
        Width = settings.Width;
        MinWidth = 300;
        MaxWidth = 1000;
        WindowStartupLocation = WindowStartupLocation.Manual;
        (Left, Top) = InitialPosition(settings);

        View = new OverlayView();
        Content = View;
        View.DragHandle.MouseLeftButtonDown += OnDragStart;
        View.DragHandle.MouseMove += OnDragMove;
        View.DragHandle.MouseLeftButtonUp += OnDragEnd;
        View.DragHandle.LostMouseCapture += (_, _) => EndDrag();
        View.ResizeGrip.DragDelta += (_, e) =>
        {
            if (_locked) return;
            Width = Math.Clamp(Width + e.HorizontalChange, MinWidth, MaxWidth);
        };
        View.ResizeGrip.DragCompleted += (_, _) => BoundsChanged?.Invoke();
        MouseEnter += (_, _) => UpdateAdjustBar();
        MouseLeave += (_, _) => UpdateAdjustBar();
    }

    public OverlayView View { get; }

    /// <summary>Raised after the user moved or resized the overlay (persist Left/Top/Width).</summary>
    public event Action? BoundsChanged;

    public bool IsLocked => _locked;

    private static (double Left, double Top) InitialPosition(OverlaySettings s)
    {
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
        double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
        if (s.Left is { } l && s.Top is { } t && l > vl - s.Width + 60 && l < vl + vw - 60 && t > vt - 10 && t < vt + vh - 60)
            return (l, t);
        var wa = SystemParameters.WorkArea;
        return (wa.Right - s.Width - 40, wa.Top + 120);
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

    private void UpdateAdjustBar() => View.AdjustBarVisible = !_locked && IsMouseOver;

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (_locked || e.ClickCount > 1) return;
        _dragging = true;
        _dragStart = e.GetPosition(this);
        View.DragHandle.CaptureMouse();
        e.Handled = true;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(this);
        Left += p.X - _dragStart.X;
        Top += p.Y - _dragStart.Y;
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        View.DragHandle.ReleaseMouseCapture();
        EndDrag();
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        BoundsChanged?.Invoke();
    }
}
