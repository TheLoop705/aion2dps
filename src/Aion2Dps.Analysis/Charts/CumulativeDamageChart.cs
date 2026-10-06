using System.Windows;
using System.Windows.Media;
using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis;

/// <summary>
/// Stacked areas of cumulative damage per player over time, a dashed line at the boss's max HP and a kill-time marker.
/// </summary>
public sealed class CumulativeDamageChart : ChartBase
{
    private IReadOnlyList<CumulativeSeries> _series = [];
    private long[][] _stack = [];
    private long? _bossMaxHp;
    private double? _killTime;
    private uint? _highlight;

    public IReadOnlyList<CumulativeSeries> Series => _series;
    public long? BossMaxHp => _bossMaxHp;
    public double? KillTimeSeconds => _killTime;

    public uint? HighlightEntityId
    {
        get => _highlight;
        set { _highlight = value; InvalidateVisual(); }
    }

    protected override double DefaultHeight => 260;

    /// <param name="series">Layers, bottom first.</param>
    public void SetSeries(IReadOnlyList<CumulativeSeries> series, long? bossMaxHp, double? killTimeSeconds)
    {
        _series = series;
        int n = series.Count == 0 ? 0 : series.Max(s => s.Values.Count);
        _stack = SeriesMath.Stack(series.Select(s => s.Values).ToList(), n);
        _bossMaxHp = bossMaxHp is > 0 ? bossMaxHp : null;
        _killTime = killTimeSeconds;
        InvalidateVisual();
    }

    public void SetEncounter(EncounterRecord record)
    {
        // Multi-boss fight: the reference line is the bosses' combined max HP (all of it must go for a full clear).
        _multiBoss = ChartData.IsMultiBoss(record) && record.Bosses.All(b => b.MaxHp is > 0);
        long? max = _multiBoss ? record.Bosses.Sum(b => b.MaxHp!.Value) : record.BossMaxHp;
        SetSeries(ChartData.CumulativeSeries(record), max, ChartData.KillTime(record));
    }

    private bool _multiBoss;

    protected override void OnRender(DrawingContext dc)
    {
        DrawHitArea(dc);
        if (ActualWidth < 40 || ActualHeight < 40) return;
        int n = _stack.Length == 0 ? 0 : _stack[^1].Length;
        if (n < 2)
        {
            DrawCentered(dc, "No damage recorded");
            return;
        }

        double legendH = DrawLegend(dc, 4, 0, ActualWidth - 8,
            _series.Select(s => (s.Name, ClassBrush(s.Class), _highlight is null || _highlight == s.EntityId)));
        long total = _stack[^1].Max();
        double max = Math.Max(total, _bossMaxHp ?? 0) * 1.08;
        var ticks = SeriesMath.NiceTicks(max, Math.Max(2, (int)((ActualHeight - legendH) / 45)), out double niceMax);
        double labelW = MeasureAxisLabelWidth(ticks, Fmt.Axis);
        var plot = new Rect(labelW + 10, legendH + 10, Math.Max(10, ActualWidth - labelW - 22), Math.Max(10, ActualHeight - legendH - 10 - 24));
        double duration = n - 1;
        DrawYAxis(dc, plot, ticks, niceMax, Fmt.Axis);
        DrawTimeAxis(dc, plot, duration);

        double X(double t) => plot.Left + t / duration * plot.Width;
        double Y(double v) => plot.Bottom - v / niceMax * plot.Height;

        for (int k = 0; k < _series.Count; k++)
        {
            var top = _stack[k];
            var bottom = k == 0 ? null : _stack[k - 1];
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(X(0), Y(top[0])), true, true);
                for (int i = 1; i < n; i++) g.LineTo(new Point(X(i), Y(top[i])), true, true);
                for (int i = n - 1; i >= 0; i--) g.LineTo(new Point(X(i), Y(bottom?[i] ?? 0)), true, true);
            }
            geo.Freeze();
            var brush = ClassBrush(_series[k].Class);
            bool strong = _highlight is null || _highlight == _series[k].EntityId;
            dc.DrawGeometry(Fade(brush, strong ? 0.72 : 0.3), null, geo);

            var edge = new StreamGeometry();
            using (var g = edge.Open())
            {
                g.BeginFigure(new Point(X(0), Y(top[0])), false, false);
                for (int i = 1; i < n; i++) g.LineTo(new Point(X(i), Y(top[i])), true, false);
            }
            edge.Freeze();
            dc.DrawGeometry(null, MakePen(strong ? brush : Fade(brush, 0.5), 1.2), edge);
        }

        // Kill marker.
        if (_killTime is { } kt && kt >= 0)
        {
            double x = X(Math.Min(kt, duration));
            dc.DrawLine(MakePen(PositiveBrush, 1.5, [4, 3]), new Point(x, plot.Top), new Point(x, plot.Bottom));
            var ft = Text("KILL " + Fmt.Duration(kt), BaseFontSize * 0.8, PositiveBrush, FontWeights.Bold);
            double lx = Math.Min(x + 4, plot.Right - ft.Width - 2);
            if (lx < x) lx = x - ft.Width - 4;
            dc.DrawText(ft, new Point(lx, plot.Top + 2));
        }

        // Boss max-HP line.
        if (_bossMaxHp is { } hp)
        {
            double y = Math.Round(Y(hp)) + 0.5;
            dc.DrawLine(MakePen(TextBrush, 1.4, [6, 4]), new Point(plot.Left, y), new Point(plot.Right, y));
            string label = (_multiBoss ? "Bosses' HP " : "Boss HP ") + Fmt.Number(hp) + (_killTime is { } k2 ? " · down at " + Fmt.Duration(k2) : "");
            var ft = Text(label, BaseFontSize * 0.85, TextBrush, FontWeights.SemiBold);
            var bg = new Rect(plot.Left + 6, y - ft.Height - 3, ft.Width + 8, ft.Height + 2);
            dc.DrawRoundedRectangle(Fade(SurfaceBrush, 0.85), null, bg, 3, 3);
            dc.DrawText(ft, new Point(bg.Left + 4, bg.Top + 1));
        }

        if (HoverPoint is { } p && plot.Contains(p))
        {
            int idx = Math.Clamp((int)Math.Round((p.X - plot.Left) / plot.Width * duration), 0, n - 1);
            double x = X(idx);
            dc.DrawLine(MakePen(Fade(TextBrush, 0.45), 1, [3, 3]), new Point(x, plot.Top), new Point(x, plot.Bottom));
            long sum = _stack[^1][idx];
            var rows = new List<TooltipLine>();
            for (int k = _series.Count - 1; k >= 0; k--)
            {
                long v = idx < _series[k].Values.Count ? _series[k].Values[idx] : 0;
                rows.Add(new TooltipLine(ClassBrush(_series[k].Class), _series[k].Name,
                    $"{Fmt.Number(v)}  {Fmt.Percent(sum > 0 ? (double)v / sum : null, 0)}"));
            }
            rows.Add(new TooltipLine(null, "Party total", Fmt.Number(sum), true));
            if (_bossMaxHp is { } mh) rows.Add(new TooltipLine(null, _multiBoss ? "Bosses' HP removed" : "Boss HP removed", Fmt.Percent(Math.Min(1, (double)sum / mh)), false));
            DrawTooltip(dc, new Point(x, p.Y), Fmt.Duration(idx) + " · cumulative damage", rows);
        }
    }
}
