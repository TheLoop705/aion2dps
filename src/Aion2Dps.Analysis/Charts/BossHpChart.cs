using System.Windows;
using System.Windows.Media;
using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis;

/// <summary>Boss HP (% of max) over the fight with reset markers (back to full after dropping below 90 %) and the kill.</summary>
public sealed class BossHpChart : ChartBase
{
    private IReadOnlyList<HpSample> _samples = [];
    private long _maxHp;
    private double _duration;
    private double? _killTime;
    private IReadOnlyList<float> _resets = [];

    public IReadOnlyList<float> ResetTimes => _resets;

    protected override double DefaultHeight => 150;

    public void SetEncounter(EncounterRecord record) =>
        SetData(record.BossHpTimeline, record.BossMaxHp, record.DurationSeconds, ChartData.KillTime(record));

    public void SetData(IReadOnlyList<HpSample> samples, long? maxHp, double durationSeconds, double? killTimeSeconds)
    {
        _samples = samples.OrderBy(s => s.T).ToList();
        _maxHp = maxHp is > 0 ? maxHp.Value : (_samples.Count == 0 ? 0 : _samples.Max(s => s.Hp));
        _duration = Math.Max(durationSeconds, _samples.Count == 0 ? 0 : _samples[^1].T);
        _killTime = killTimeSeconds;
        _resets = SeriesMath.DetectResets(_samples, _maxHp);
        InvalidateVisual();
    }

    private Brush LevelBrush(double f) => f > 0.5 ? HpHighBrush : f > 0.2 ? HpMidBrush : HpLowBrush;

    protected override void OnRender(DrawingContext dc)
    {
        DrawHitArea(dc);
        if (ActualWidth < 40 || ActualHeight < 40) return;
        if (_samples.Count < 2 || _maxHp <= 0 || _duration <= 0)
        {
            DrawCentered(dc, "No boss HP data for this fight");
            return;
        }
        double[] ticks = [0, 0.25, 0.5, 0.75, 1.0];
        string Pct(double v) => (v * 100).ToString("0") + "%";
        double labelW = MeasureAxisLabelWidth(ticks, Pct);
        var plot = new Rect(labelW + 10, 8, Math.Max(10, ActualWidth - labelW - 22), Math.Max(10, ActualHeight - 8 - 24));
        DrawYAxis(dc, plot, ticks, 1.0, Pct);
        DrawTimeAxis(dc, plot, _duration);

        Point P(HpSample s) => new(plot.Left + s.T / _duration * plot.Width,
            plot.Bottom - Math.Clamp((double)s.Hp / _maxHp, 0, 1) * plot.Height);

        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(P(_samples[0]).X, plot.Bottom), true, true);
            foreach (var s in _samples) g.LineTo(P(s), true, false);
            g.LineTo(new Point(P(_samples[^1]).X, plot.Bottom), true, false);
        }
        area.Freeze();
        var grad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        if (HpHighBrush is SolidColorBrush hi && HpLowBrush is SolidColorBrush lo)
        {
            grad.GradientStops.Add(new GradientStop(Color.FromArgb(70, hi.Color.R, hi.Color.G, hi.Color.B), 0));
            grad.GradientStops.Add(new GradientStop(Color.FromArgb(30, lo.Color.R, lo.Color.G, lo.Color.B), 1));
            grad.Freeze();
            dc.DrawGeometry(grad, null, area);
        }
        else dc.DrawGeometry(Fade(HpHighBrush, 0.2), null, area);

        for (int i = 1; i < _samples.Count; i++)
        {
            double f = (double)_samples[i - 1].Hp / _maxHp;
            dc.DrawLine(MakePen(LevelBrush(f), 2), P(_samples[i - 1]), P(_samples[i]));
        }

        foreach (var r in _resets)
        {
            double x = plot.Left + r / _duration * plot.Width;
            dc.DrawLine(MakePen(WarningBrush, 1.4, [4, 3]), new Point(x, plot.Top), new Point(x, plot.Bottom));
            var ft = Text("RESET " + Fmt.Duration(r), BaseFontSize * 0.78, WarningBrush, FontWeights.Bold);
            dc.DrawText(ft, new Point(x + 4 + ft.Width <= plot.Right ? x + 4 : x - 4 - ft.Width, plot.Top + 2));
        }
        if (_killTime is { } kt)
        {
            double x = plot.Left + Math.Min(kt, _duration) / _duration * plot.Width;
            dc.DrawLine(MakePen(PositiveBrush, 1.4, [4, 3]), new Point(x, plot.Top), new Point(x, plot.Bottom));
            var ft = Text("KILL", BaseFontSize * 0.78, PositiveBrush, FontWeights.Bold);
            dc.DrawText(ft, new Point(Math.Max(plot.Left, x - ft.Width - 4), plot.Top + 2));
        }

        if (HoverPoint is { } hp && plot.Contains(hp))
        {
            double t = (hp.X - plot.Left) / plot.Width * _duration;
            var s = _samples.MinBy(x => Math.Abs(x.T - t));
            var pt = P(s);
            dc.DrawLine(MakePen(Fade(TextBrush, 0.45), 1, [3, 3]), new Point(pt.X, plot.Top), new Point(pt.X, plot.Bottom));
            dc.DrawEllipse(LevelBrush((double)s.Hp / _maxHp), MakePen(SurfaceBrush, 1.5), pt, 4, 4);
            DrawTooltip(dc, new Point(pt.X, hp.Y), Fmt.PreciseTime(s.T) + " · boss HP",
            [
                new(null, "Remaining", Fmt.Percent((double)s.Hp / _maxHp), true),
                new(null, "HP", Fmt.Exact(s.Hp)),
                new(null, "Max HP", Fmt.Exact(_maxHp)),
            ]);
        }
    }
}
