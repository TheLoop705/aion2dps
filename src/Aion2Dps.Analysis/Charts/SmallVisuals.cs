using System.Windows;
using System.Windows.Media;
using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis;

/// <summary>Inline bar for table cells: a track with a fill of <see cref="Value"/> (0..1).</summary>
public sealed class MiniBar : ChartBase
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double),
        typeof(MiniBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush),
        typeof(MiniBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarHeightProperty = DependencyProperty.Register(nameof(BarHeight), typeof(double),
        typeof(MiniBar), new FrameworkPropertyMetadata(5.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public MiniBar() { IsHitTestVisible = false; }

    /// <summary>Fill fraction 0..1.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <summary>Fill brush; null = the theme's bar fill. Use <c>SetResourceReference(FillProperty, key)</c> for theme brushes.</summary>
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    /// <summary>Optional class colouring (used when <see cref="Fill"/> is null).</summary>
    public CharacterClass? Class { get; set; }

    public double BarHeight { get => (double)GetValue(BarHeightProperty); set => SetValue(BarHeightProperty, value); }

    protected override double DefaultHeight => BarHeight + 2;

    protected override void OnRender(DrawingContext dc)
    {
        double h = Math.Min(BarHeight, ActualHeight);
        if (ActualWidth <= 0 || h <= 0) return;
        double y = (ActualHeight - h) / 2;
        double r = h / 2;
        dc.DrawRoundedRectangle(BarTrackBrush, null, new Rect(0, y, ActualWidth, h), r, r);
        double v = double.IsNaN(Value) ? 0 : Math.Clamp(Value, 0, 1);
        if (v <= 0) return;
        var fill = Fill ?? (Class is { } c ? ClassBrush(c) : BarFillBrush);
        dc.DrawRoundedRectangle(fill, null, new Rect(0, y, Math.Max(h, ActualWidth * v), h), r, r);
    }
}

/// <summary>Two-way PvP trade bar: damage taken grows left from the centre, damage dealt grows right.</summary>
public sealed class TwoWayBar : ChartBase
{
    private double _dealt, _taken, _max = 1;

    public TwoWayBar() { IsHitTestVisible = false; }

    public void SetValues(double dealt, double taken, double max)
    {
        _dealt = Math.Max(0, dealt);
        _taken = Math.Max(0, taken);
        _max = Math.Max(1, max);
        InvalidateVisual();
    }

    public double Dealt => _dealt;
    public double Taken => _taken;

    protected override double DefaultHeight => 10;

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 4) return;
        double h = Math.Min(8, ActualHeight), y = (ActualHeight - h) / 2, mid = ActualWidth / 2, half = mid - 1;
        dc.DrawRoundedRectangle(BarTrackBrush, null, new Rect(0, y, ActualWidth, h), h / 2, h / 2);
        double tw = half * Math.Min(1, _taken / _max), dw = half * Math.Min(1, _dealt / _max);
        if (tw > 0) dc.DrawRoundedRectangle(NegativeBrush, null, new Rect(mid - tw, y, tw, h), h / 2, h / 2);
        if (dw > 0) dc.DrawRoundedRectangle(PositiveBrush, null, new Rect(mid, y, dw, h), h / 2, h / 2);
        dc.DrawLine(MakePen(TextBrush, 1.2), new Point(mid, y - 2), new Point(mid, y + h + 2));
    }
}

/// <summary>Class emblem: a rounded square in the class colour with the class's short code.</summary>
public sealed class ClassEmblem : ChartBase
{
    public static readonly DependencyProperty ClassProperty = DependencyProperty.Register(nameof(Class), typeof(CharacterClass),
        typeof(ClassEmblem), new FrameworkPropertyMetadata(CharacterClass.Unknown, FrameworkPropertyMetadataOptions.AffectsRender));

    public ClassEmblem() { Width = 28; Height = 28; }

    public CharacterClass Class { get => (CharacterClass)GetValue(ClassProperty); set => SetValue(ClassProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double s = Math.Min(ActualWidth, ActualHeight);
        if (s <= 0) return;
        var rect = new Rect((ActualWidth - s) / 2, (ActualHeight - s) / 2, s, s);
        var fill = ClassBrush(Class);
        dc.DrawRoundedRectangle(fill, null, rect, s * 0.26, s * 0.26);
        dc.DrawRoundedRectangle(Fade(Brushes.White, 0.12), null, new Rect(rect.X + 1, rect.Y + 1, s - 2, s * 0.45), s * 0.2, s * 0.2);
        if (s < 14) return;
        var ft = Text(ClassInfo.ShortName(Class), s * 0.34, SkillVisuals.ContrastText(fill), FontWeights.Bold);
        dc.DrawText(ft, new Point(rect.X + (s - ft.Width) / 2, rect.Y + (s - ft.Height) / 2));
    }
}

/// <summary>Skill icon substitute: rounded square coloured by the owning class with the skill's initials.</summary>
public sealed class SkillTile : ChartBase
{
    private string _initials = "";
    private CharacterClass _class;

    public SkillTile() { Width = 22; Height = 22; IsHitTestVisible = false; }

    public void SetSkill(string skillName, uint skillId, CharacterClass tileClass)
    {
        _initials = SkillVisuals.Initials(skillName, skillId);
        _class = tileClass;
        InvalidateVisual();
    }

    public string Initials => _initials;

    protected override void OnRender(DrawingContext dc)
    {
        double s = Math.Min(ActualWidth, ActualHeight);
        if (s <= 0) return;
        var rect = new Rect((ActualWidth - s) / 2, (ActualHeight - s) / 2, s, s);
        var fill = ClassBrush(_class);
        dc.DrawRoundedRectangle(fill, null, rect, s * 0.24, s * 0.24);
        dc.DrawRoundedRectangle(Fade(Brushes.White, 0.10), null, new Rect(rect.X + 1, rect.Y + 1, s - 2, s * 0.45), s * 0.2, s * 0.2);
        var ft = Text(_initials, s * 0.42, SkillVisuals.ContrastText(fill), FontWeights.Bold);
        dc.DrawText(ft, new Point(rect.X + (s - ft.Width) / 2, rect.Y + (s - ft.Height) / 2));
    }
}
