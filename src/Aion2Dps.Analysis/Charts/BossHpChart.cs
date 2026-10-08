using System.Windows;
using System.Windows.Media;
using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis;

/// <summary>One boss line of a <see cref="BossHpChart"/>.</summary>
public sealed record BossHpSeries(string Name, IReadOnlyList<HpSample> Samples, long? MaxHp, double? KillTimeSeconds);

/// <summary>
/// Boss HP (% of max) over the fight with reset markers (back to full after dropping below 90 %) and the kill. A
/// multi-boss fight (several bosses engaged at once) draws one line per boss with a legend and per-boss kill markers.
/// </summary>
public sealed class BossHpChart : ChartBase
{
    private sealed record Line(string Name, IReadOnlyList<HpSample> Samples, long MaxHp, double? KillTime, IReadOnlyList<float> Resets);

    private List<Line> _lines = new();
    private double _duration;

    /// <summary>Reset times of all lines (seconds), in time order.</summary>
    public IReadOnlyList<float> ResetTimes => _lines.SelectMany(l => l.Resets).OrderBy(t => t).ToList();

    /// <summary>Number of boss lines drawn (2+ = multi-boss fight).</summary>
    public int SeriesCount => _lines.Count;

    /// <summary>Names of the boss lines, in drawing order.</summary>
    public IReadOnlyList<string> SeriesNames => _lines.Select(l => l.Name).ToList();

    protected override double DefaultHeight => 150;

    public void SetEncounter(EncounterRecord record) => SetEncounter(record, null);

    /// <summary>Single boss: the primary boss's timeline. Multi-boss (<see cref="EncounterRecord.Bosses"/> with 2+ timelines and
    /// <paramref name="gameData"/> given): one line per boss.</summary>
    public void SetEncounter(EncounterRecord record, IGameData? gameData)
    {
        var bosses = record.Bosses.Where(b => b.HpTimeline.Count >= 2).ToList();
        if (gameData is not null && bosses.Count >= 2)
        {
            SetSeries(bosses.Select(b => new BossHpSeries(ChartData.BossName(b, gameData, ChartData.IsOpenWorld(record, gameData)), b.HpTimeline, b.MaxHp, b.Killed ? b.KillTimeSeconds : null)).ToList(),
                record.DurationSeconds);
            return;
        }
        SetData(record.BossHpTimeline, record.BossMaxHp, record.DurationSeconds, ChartData.KillTime(record));
    }

    public void SetData(IReadOnlyList<HpSample> samples, long? maxHp, double durationSeconds, double? killTimeSeconds) =>
        SetSeries([new BossHpSeries("", samples, maxHp, killTimeSeconds)], durationSeconds);

    public void SetSeries(IReadOnlyList<BossHpSeries> series, double durationSeconds)
    {
        _lines = new List<Line>(series.Count);
        double end = durationSeconds;
        foreach (var s in series)
        {
            var samples = s.Samples.OrderBy(x => x.T).ToList();
            long max = s.MaxHp is > 0 ? s.MaxHp.Value : (samples.Count == 0 ? 0 : samples.Max(x => x.Hp));
            if (samples.Count > 0) end = Math.Max(end, samples[^1].T);
            _lines.Add(new Line(s.Name, samples, max, s.KillTimeSeconds, SeriesMath.DetectResets(samples, max)));
        }
        _duration = end;
        InvalidateVisual();
    }

    private Brush LevelBrush(double f) => f > 0.5 ? HpHighBrush : f > 0.2 ? HpMidBrush : HpLowBrush;

    /// <summary>Line colour of boss <paramref name="i"/> in a multi-boss chart.</summary>
    private Brush SeriesBrush(int i) => (i % 4) switch
    {
        0 => HpHighBrush,
        1 => AccentBrush,
        2 => DotBrush,
        _ => NegativeBrush,
    };

    protected override void OnRender(DrawingContext dc)
    {
        DrawHitArea(dc);
        if (ActualWidth < 40 || ActualHeight < 40) return;
        var lines = _lines.Where(l => l.Samples.Count >= 2 && l.MaxHp > 0).ToList();
        if (lines.Count == 0 || _duration <= 0)
        {
            DrawCentered(dc, "No boss HP data for this fight");
            return;
        }
        bool multi = lines.Count > 1;
        double[] ticks = [0, 0.25, 0.5, 0.75, 1.0];
        string Pct(double v) => (v * 100).ToString("0") + "%";
        double labelW = MeasureAxisLabelWidth(ticks, Pct);
        double top = multi ? 8 + BaseFontSize * 1.35 : 8;
        var plot = new Rect(labelW + 10, top, Math.Max(10, ActualWidth - labelW - 22), Math.Max(10, ActualHeight - top - 24));
        DrawYAxis(dc, plot, ticks, 1.0, Pct);
        DrawTimeAxis(dc, plot, _duration);

        Point P(Line l, HpSample s) => new(plot.Left + s.T / _duration * plot.Width,
            plot.Bottom - Math.Clamp((double)s.Hp / l.MaxHp, 0, 1) * plot.Height);

        if (!multi) DrawArea(dc, lines[0], plot, P);

        for (int li = 0; li < lines.Count; li++)
        {
            var l = lines[li];
            for (int i = 1; i < l.Samples.Count; i++)
            {
                var brush = multi ? SeriesBrush(li) : LevelBrush((double)l.Samples[i - 1].Hp / l.MaxHp);
                dc.DrawLine(MakePen(brush, 2), P(l, l.Samples[i - 1]), P(l, l.Samples[i]));
            }
        }

        foreach (var l in lines)
        {
            foreach (var r in l.Resets)
            {
                double x = plot.Left + r / _duration * plot.Width;
                dc.DrawLine(MakePen(WarningBrush, 1.4, [4, 3]), new Point(x, plot.Top), new Point(x, plot.Bottom));
                var ft = Text("RESET " + Fmt.Duration(r), BaseFontSize * 0.78, WarningBrush, FontWeights.Bold);
                dc.DrawText(ft, new Point(x + 4 + ft.Width <= plot.Right ? x + 4 : x - 4 - ft.Width, plot.Top + 2));
            }
        }

        for (int li = 0; li < lines.Count; li++)
        {
            if (lines[li].KillTime is not { } kt) continue;
            double x = plot.Left + Math.Min(kt, _duration) / _duration * plot.Width;
            var brush = multi ? SeriesBrush(li) : PositiveBrush;
            dc.DrawLine(MakePen(brush, 1.4, [4, 3]), new Point(x, plot.Top), new Point(x, plot.Bottom));
            var ft = Text("KILL", BaseFontSize * 0.78, brush, FontWeights.Bold);
            dc.DrawText(ft, new Point(Math.Max(plot.Left, x - ft.Width - 4), plot.Top + 2 + (multi ? li * ft.Height : 0)));
        }

        if (multi) DrawLegend(dc, lines, plot);

        if (HoverPoint is { } hp && plot.Contains(hp))
        {
            double t = (hp.X - plot.Left) / plot.Width * _duration;
            var rows = new List<TooltipLine>();
            double x = hp.X;
            for (int li = 0; li < lines.Count; li++)
            {
                var l = lines[li];
                var s = l.Samples.MinBy(v => Math.Abs(v.T - t));
                var pt = P(l, s);
                if (li == 0) x = pt.X;
                double f = (double)s.Hp / l.MaxHp;
                dc.DrawEllipse(multi ? SeriesBrush(li) : LevelBrush(f), MakePen(SurfaceBrush, 1.5), pt, 4, 4);
                if (multi)
                {
                    rows.Add(new(SeriesBrush(li), l.Name, $"{Fmt.Percent(f)} · {Fmt.Exact(s.Hp)}", li == 0));
                }
                else
                {
                    rows.Add(new(null, "Remaining", Fmt.Percent(f), true));
                    rows.Add(new(null, "HP", Fmt.Exact(s.Hp)));
                    rows.Add(new(null, "Max HP", Fmt.Exact(l.MaxHp)));
                }
            }
            dc.DrawLine(MakePen(Fade(TextBrush, 0.45), 1, [3, 3]), new Point(x, plot.Top), new Point(x, plot.Bottom));
            DrawTooltip(dc, new Point(x, hp.Y), Fmt.PreciseTime(t) + (multi ? " · bosses' HP" : " · boss HP"), rows);
        }
    }

    private void DrawArea(DrawingContext dc, Line l, Rect plot, Func<Line, HpSample, Point> p)
    {
        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(p(l, l.Samples[0]).X, plot.Bottom), true, true);
            foreach (var s in l.Samples) g.LineTo(p(l, s), true, false);
            g.LineTo(new Point(p(l, l.Samples[^1]).X, plot.Bottom), true, false);
        }
        area.Freeze();
        if (HpHighBrush is SolidColorBrush hi && HpLowBrush is SolidColorBrush lo)
        {
            var grad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            grad.GradientStops.Add(new GradientStop(Color.FromArgb(70, hi.Color.R, hi.Color.G, hi.Color.B), 0));
            grad.GradientStops.Add(new GradientStop(Color.FromArgb(30, lo.Color.R, lo.Color.G, lo.Color.B), 1));
            grad.Freeze();
            dc.DrawGeometry(grad, null, area);
        }
        else dc.DrawGeometry(Fade(HpHighBrush, 0.2), null, area);
    }

    private void DrawLegend(DrawingContext dc, List<Line> lines, Rect plot)
    {
        double x = plot.Left;
        double y = 4;
        for (int li = 0; li < lines.Count; li++)
        {
            var ft = Text(lines[li].Name, BaseFontSize * 0.85, TextBrush, FontWeights.SemiBold);
            double sw = 14;
            if (x + sw + 6 + ft.Width > plot.Right && li > 0) break;
            dc.DrawRoundedRectangle(SeriesBrush(li), null, new Rect(x, y + ft.Height / 2 - 2, sw, 4), 2, 2);
            dc.DrawText(ft, new Point(x + sw + 5, y));
            x += sw + 5 + ft.Width + 16;
        }
    }
}
