using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aion2Dps.Contracts;

namespace Aion2Dps.Armory.Ui;

/// <summary>Minimal ICommand.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
}

/// <summary>
/// Item-grade colours. These are game semantics (like rarity colours in-game), not theme colours, so they are fixed
/// mid-tone brushes that stay readable on both dark and light themes.
/// </summary>
public static class GradeBrushes
{
    private static readonly Dictionary<ItemGrade, SolidColorBrush> Brushes = new()
    {
        [ItemGrade.Unknown] = Make(0x9A, 0xA0, 0xA6),
        [ItemGrade.Common] = Make(0x9A, 0x9F, 0xA8),
        [ItemGrade.Rare] = Make(0x3D, 0xB0, 0x4E),
        [ItemGrade.Legend] = Make(0x3A, 0x8C, 0xE6),
        [ItemGrade.Unique] = Make(0xD6, 0x9E, 0x14),
        [ItemGrade.Epic] = Make(0xEB, 0x6C, 0x1E),
        [ItemGrade.Special] = Make(0x1F, 0xAF, 0xA8),
        [ItemGrade.Mythic] = Make(0xD6, 0x45, 0x45),
    };

    private static SolidColorBrush Make(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    public static Brush For(ItemGrade grade) => Brushes.TryGetValue(grade, out var b) ? b : Brushes[ItemGrade.Unknown];

    /// <summary>Display name of a grade (Global site ids → in-game names).</summary>
    public static string DisplayName(ItemGrade grade, string? raw) => grade switch
    {
        ItemGrade.Legend => "Legendary",
        ItemGrade.Unknown => string.IsNullOrWhiteSpace(raw) ? "—" : raw!,
        _ => grade.ToString(),
    };
}

/// <summary>Attached helpers for the armory views.</summary>
public static class ArmoryUi
{
    /// <summary>Sets <c>TextBlock.Foreground</c> to the theme class brush (<see cref="ThemeKeys.ClassBrush"/>) as a dynamic resource.</summary>
    public static readonly DependencyProperty ClassForegroundProperty = DependencyProperty.RegisterAttached(
        "ClassForeground", typeof(CharacterClass?), typeof(ArmoryUi), new PropertyMetadata(null, OnClassForegroundChanged));

    public static CharacterClass? GetClassForeground(DependencyObject d) => (CharacterClass?)d.GetValue(ClassForegroundProperty);
    public static void SetClassForeground(DependencyObject d, CharacterClass? value) => d.SetValue(ClassForegroundProperty, value);

    private static void OnClassForegroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        var dp = fe is TextBlock ? TextBlock.ForegroundProperty : fe is System.Windows.Shapes.Shape ? System.Windows.Shapes.Shape.FillProperty : Control.ForegroundProperty;
        if (e.NewValue is CharacterClass c) fe.SetResourceReference(dp, ThemeKeys.ClassBrush(c));
        else fe.ClearValue(dp);
    }
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value switch
        {
            bool x => x,
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i != 0,
            ICollection c => c.Count > 0,
            _ => true,
        };
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Loads item/profile icons from NCSOFT's asset CDN (only https URLs on *.playnccdn.com / *.plaync.com). Cached.</summary>
public static class ArmoryImages
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.Ordinal);

    public static ImageSource? Get(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(url, out var hit)) return hit;
        }
        ImageSource? img = null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
            (uri.Host.EndsWith(".playnccdn.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".plaync.com", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = uri;
                bi.DecodePixelWidth = 64;
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                img = bi;
            }
            catch (Exception ex)
            {
                AppLog.Debug("Armory", $"icon load failed {url}: {ex.Message}");
            }
        }
        lock (Cache)
        {
            if (Cache.Count > 2000) Cache.Clear();
            Cache[url] = img;
        }
        return img;
    }
}
