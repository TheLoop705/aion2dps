using System.Runtime.InteropServices;
using System.Windows.Interop;
using Aion2Dps.App.Theming;

namespace Aion2Dps.App.Controls;

/// <summary>A normal window that follows the theme: themed background/fonts and a dark title bar on dark themes.</summary>
public class ThemedWindow : Window
{
    public ThemedWindow()
    {
        this.Ref(BackgroundProperty, AppThemeKeys.DashboardBackground);
        this.Ref(ForegroundProperty, ThemeKeys.Text);
        this.Ref(FontFamilyProperty, ThemeKeys.FontFamily);
        FontSize = 12.5;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Icon = AppIcon.ImageSource;
        SourceInitialized += (_, _) => ApplyTitleBar();
        ThemeManager.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged() => Dispatcher.BeginInvoke(ApplyTitleBar);

    private void ApplyTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dark = TryFindResource(AppThemeKeys.IsDark) is bool b && b ? 1 : 0;
            // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (Windows 10 20H1+ / 11)
            _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        }
        catch { /* cosmetic only */ }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
