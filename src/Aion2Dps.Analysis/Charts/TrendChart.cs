using System.Windows;
using System.Windows.Media;
using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis;

/// <summary>DPS per fight over time (oldest left) with best and median guides; kills are filled dots, other outcomes hollow.</summary>
public sealed class TrendChart : ChartBase
{
    private IReadOnlyList<TrendPoint> _points = [];
    private TrendPoint? _personalBest;

    public IReadOnlyList<TrendPoint> Points => _points;
    public double Median { get; private set; } = double.NaN;
    public double Best { get; private set; }

    protected override double DefaultHeight => 240;

    public void SetData(IReadOnlyList<TrendPoint> points, TrendPoint? personalBest = null)
    {
        _points = points.OrderBy(p => p.StartUtc).ToList();
        _personalBest = personalBest;
        Median = SeriesMath.Median(_points.Select(p => p.Dps));
        Best = _points.Count == 0 ? 0 : _points.Max(p => p.Dps);
        if (personalBest is not null) Best = Math.Max(Best, personalBest.Dps);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        DrawHitArea(dc);
        if (ActualWidth < 40 || ActualHeight < 40) return;
        if (_points.Count == 0)
        {
            DrawCentered(dc, "No fights on this boss yet");
            return;
        }
        var ticks = SeriesMath.NiceTicks(Best * 1.1, Math.Max(2, (int)(ActualHeight / 50)), out double niceMax);
        double labelW = MeasureAxisLabelWidth(ticks, Fmt.Axis);
        var plot = new Rect(labelW + 10, 10, Math.Max(10, ActualWidth - labelW - 30), Math.Max(10, ActualHeight - 10 - 26));
        DrawYAxis(dc, plot, ticks, niceMax, Fmt.Axis);

        int n = _points.Count;
        double X(int i) => n == 1 ? plot.Left + plot.Width / 2 : plot.Left + 8 + i * (plot.Width - 16) / (n - 1);
        double Y(double v) => plot.Bottom - v / niceMax * plot.Height;

        // Date labels where the day changes, skipping overlaps.
        double lastRight = double.NegativeInfinity;
        string? lastDate = null;
        for (int i = 0; i < n; i++)
        {
            string d = Fmt.ShortDate(_points[i].StartUtc);
            if (d == lastDate) continue;
            var ft = Text(d, BaseFontSize * 0.82, MutedBrush);
            double x = X(i) - ft.Width / 2;
            if (x < lastRight + 8) continue;
            dc.DrawLine(MakePen(GridBrush, 1), new Point(X(i), plot.Bottom), new Point(X(i), plot.Bottom + 4));
            dc.DrawText(ft, new Point(x, plot.Bottom + 6));
            lastRight = x + ft.Width;
            lastDate = d;
        }

        void Guide(double value, Brush brush, string label, bool below = false)
        {
            double y = Math.Round(Y(value)) + 0.5;
            dc.DrawLine(MakePen(brush, 1.2, [6, 4]), new Point(plot.Left, y), new Point(plot.Right, y));
            var ft = Text(label, BaseFontSize * 0.8, brush, FontWeights.SemiBold);
            var bg = new Rect(plot.Right - ft.Width - 8, below ? y + 2 : y - ft.Height - 2, ft.Width + 6, ft.Height);
            dc.DrawRoundedRectangle(Fade(SurfaceBrush, 0.85), null, bg, 3, 3);
            dc.DrawText(ft, new Point(bg.Left + 3, bg.Top));
        }
        // The median label goes under its line so it never collides with the best label just above it.
        if (!double.IsNaN(Median)) Guide(Median, MutedBrush, "median " + Fmt.Dps(Median), below: Median <= Best);
        Guide(Best, PositiveBrush, "best " + Fmt.Dps(Best));

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(new Point(X(0), Y(_points[0].Dps)), false, false);
            for (int i = 1; i < n; i++) g.LineTo(new Point(X(i), Y(_points[i].Dps)), true, false);
        }
        line.Freeze();
        dc.DrawGeometry(null, MakePen(Fade(AccentBrush, 0.8), 1.8), line);

        int? hoverIdx = null;
        if (HoverPoint is { } hp && Rect.Inflate(plot, 6, 6).Contains(hp))
        {
            double best = double.MaxValue;
            for (int i = 0; i < n; i++)
            {
                double dist = Math.Abs(X(i) - hp.X);
                if (dist < best) { best = dist; hoverIdx = i; }
            }
        }

        for (int i = 0; i < n; i++)
        {
            var p = _points[i];
            var c = new Point(X(i), Y(p.Dps));
            bool kill = p.Outcome == EncounterOutcome.Kill;
            double r = hoverIdx == i ? 5.5 : 4;
            if (kill) dc.DrawEllipse(AccentBrush, MakePen(SurfaceBrush, 1.5), c, r, r);
            else dc.DrawEllipse(SurfaceBrush, MakePen(MutedBrush, 1.6), c, r - 0.5, r - 0.5);
            if (_personalBest is not null && p.FightId == _personalBest.FightId)
            {
                dc.DrawEllipse(null, MakePen(CritBrush, 2), c, r + 4, r + 4);
                var ft = Text("PB", BaseFontSize * 0.75, CritBrush, FontWeights.Bold);
                dc.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - r - 6 - ft.Height));
            }
        }

        if (hoverIdx is { } hi)
        {
            var p = _points[hi];
            DrawTooltip(dc, new Point(X(hi), Y(p.Dps)), Fmt.LocalDateTime(p.StartUtc),
            [
                new(AccentBrush, "DPS", Fmt.Dps(p.Dps), true),
                new(null, "Damage", Fmt.Number(p.Damage)),
                new(null, "Duration", Fmt.Duration(p.DurationSeconds)),
                new(null, "Outcome", Fmt.OutcomeLabel(p.Outcome)),
                new(null, "vs median", double.IsNaN(Median) || Median <= 0 ? Fmt.Dash : (p.Dps >= Median ? "+" : "") + Fmt.Percent(p.Dps / Median - 1)),
            ]);
        }
    }
}
