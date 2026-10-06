using Aion2Dps.App.Controls;
using Aion2Dps.App.Settings;

namespace Aion2Dps.App.Dashboard;

/// <summary>The main window. Closing it hides it to the tray (Quit is in the tray menu).</summary>
public sealed class DashboardWindow : ThemedWindow
{
    private readonly SettingsStore _settings;

    public DashboardWindow(DashboardContext context)
    {
        _settings = context.Settings;
        Title = "Aion2Dps";
        View = new DashboardView(context);
        Content = View;
        MinWidth = 860;
        MinHeight = 560;
        var b = context.Settings.Current.Dashboard;
        if (b is { IsValid: true } && IsOnScreen(b))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = b.Left;
            Top = b.Top;
            Width = b.Width;
            Height = b.Height;
        }
        else
        {
            Width = 1080;
            Height = 720;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    public DashboardView View { get; }

    /// <summary>When false, closing hides the window instead (normal tray behaviour).</summary>
    public bool AllowClose { get; set; }

    private static bool IsOnScreen(WindowBounds b) =>
        b.Left > SystemParameters.VirtualScreenLeft - b.Width + 80 &&
        b.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80 &&
        b.Top > SystemParameters.VirtualScreenTop - 20 &&
        b.Top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SaveBounds();
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private void SaveBounds()
    {
        if (WindowState != WindowState.Normal) return;
        _settings.Current.Dashboard = new WindowBounds { Left = Left, Top = Top, Width = Width, Height = Height };
        _settings.NotifyChanged();
    }

    /// <param name="activate">False at start-up so a running game keeps keyboard focus.</param>
    public void ShowPage(string? key, bool activate = true)
    {
        View.Navigate(key);
        if (!IsVisible)
        {
            ShowActivated = activate;
            Show();
        }
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (activate) Activate();
    }
}
