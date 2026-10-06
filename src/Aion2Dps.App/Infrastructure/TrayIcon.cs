using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Aion2Dps.App.Infrastructure;

/// <summary>Notification-area icon (WinForms NotifyIcon) with a generated emblem icon and the app menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Drawing.Icon _image;
    private readonly IntPtr _hIcon;
    private readonly Forms.ToolStripMenuItem _overlayItem;
    private readonly Forms.ToolStripMenuItem _lockItem;
    private readonly Forms.ToolStripMenuItem _partyItem;

    public sealed record Actions(Action ToggleOverlay, Action OpenDashboard, Action Reset, Action<TimeSpan> StartTraining, Action ToggleLock, Action Quit)
    {
        /// <summary>Toggle "party members only" (optional so older call sites keep compiling).</summary>
        public Action? TogglePartyOnly { get; init; }
    }

    public TrayIcon(Actions actions)
    {
        (_image, _hIcon) = CreateIcon();
        var menu = new Forms.ContextMenuStrip();
        _overlayItem = new Forms.ToolStripMenuItem("Show overlay", null, (_, _) => actions.ToggleOverlay());
        _lockItem = new Forms.ToolStripMenuItem("Lock + click-through", null, (_, _) => actions.ToggleLock()) { ShortcutKeyDisplayString = "Ctrl+Alt+L" };
        _partyItem = new Forms.ToolStripMenuItem("Party members only", null, (_, _) => actions.TogglePartyOnly?.Invoke()) { ShortcutKeyDisplayString = "Ctrl+Alt+P", Enabled = actions.TogglePartyOnly is not null };
        var training = new Forms.ToolStripMenuItem("Training run");
        foreach (var (label, secs) in new[] { ("30 seconds", 30), ("1 minute", 60), ("2 minutes", 120), ("3 minutes", 180), ("5 minutes", 300) })
            training.DropDownItems.Add(label, null, (_, _) => actions.StartTraining(TimeSpan.FromSeconds(secs)));
        menu.Items.Add(new Forms.ToolStripMenuItem("Open dashboard", null, (_, _) => actions.OpenDashboard()) { Font = new Drawing.Font(menu.Font, Drawing.FontStyle.Bold) });
        menu.Items.Add(_overlayItem);
        menu.Items.Add(_lockItem);
        menu.Items.Add(_partyItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Reset meter", null, (_, _) => actions.Reset()) { ShortcutKeyDisplayString = "Ctrl+Alt+D" });
        menu.Items.Add(training);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Quit Aion2Dps", null, (_, _) => actions.Quit()));

        _icon = new Forms.NotifyIcon
        {
            Icon = _image,
            Text = "Aion2Dps",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => actions.OpenDashboard();
    }

    public void SetOverlayVisible(bool visible) => _overlayItem.Text = visible ? "Hide overlay" : "Show overlay";

    public void SetLocked(bool locked) => _lockItem.Checked = locked;

    public void SetPartyOnly(bool partyOnly) => _partyItem.Checked = partyOnly;

    public void SetTooltip(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    public void ShowBalloon(string title, string text) => _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info);

    private static (Drawing.Icon, IntPtr) CreateIcon()
    {
        var src = Controls.AppIcon.Render(32);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;
        using var bmp = new Drawing.Bitmap(ms);
        IntPtr h = bmp.GetHicon();
        return (Drawing.Icon.FromHandle(h), h);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
        NativeMethods.DestroyIcon(_hIcon);
    }
}
