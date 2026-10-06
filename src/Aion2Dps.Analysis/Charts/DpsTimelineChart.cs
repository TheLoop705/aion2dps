using System.Windows;
using System.Windows.Media;
using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis;

/// <summary>
/// Party DPS over the fight: one line per combatant (rolling DPS from <see cref="CombatantRecord.DamagePerSecond"/>),
/// class colours, legend, and a hover crosshair with every value at that second.
/// </summary>
public sealed class DpsTimelineChart : ChartBase
{
    private IReadOnlyList<TimelineSeries> _series = [];
    private uint? _highlight;
    private string _valueLabel = "DPS";

    public IReadOnlyList<TimelineSeries> Series => _series;

    /// <summary>Rolling window used by <see cref="SetEncounter"/>.</summary>
    public int WindowSeconds { get; private set; } = 5;

    /// <summary>Combatant drawn on top with a thicker line (others are dimmed).</summary>
    public uint? HighlightEntityId
    {
        get => _highlight;
        set { _highlight = value; InvalidateVisual(); }
    }

    /// <summary>Label for values in the tooltip (default "DPS").</summary>
    public string ValueLabel
    {
        get => _valueLabel;
        set { _valueLabel = value; InvalidateVisual(); }
    }

    /// <summary>Message shown when there is nothing to draw.</summary>
    public string EmptyText { get; set; } = "No damage recorded";

    protected override double DefaultHeight => 220;

    public void SetSeries(IReadOnlyList<TimelineSeries> series)
    {
        _series = series;
        InvalidateVisual();
    }

    /// <summary>Lines for the friendly combatants of <paramref name="record"/> (or those accepted by <paramref name="include"/>).</summary>
    public void SetEncounter(EncounterRecord record, uint? highlight = null, int windowSeconds = 5,
        Func<CombatantRecord, bool>? include = null)
    {
        WindowSeconds = windowSeconds;
        _highlight = highlight;
        SetSeries(ChartData.TimelineSeries(record, windowSeconds, include));
    }

    private Brush BrushOf(TimelineSeries s) => s.Color ?? ClassBrush(s.Class);

    protected override void OnRender(DrawingContext dc)
    {
        DrawHitArea(dc);
        if (ActualWidth < 40 || ActualHeight < 40) return;
        int n = _series.Count == 0 ? 0 : _series.Max(s => s.Values.Count);
        if (n < 2 || _series.All(s => s.Values.All(v => v <= 0)))
        {
            DrawCentered(dc, EmptyText);
            return;
        }

        double legendH = DrawLegend(dc, 4, 0, ActualWidth - 8,
            _series.Select(s => (s.Name, BrushOf(s), _highlight is null || s.EntityId == _highlight)));

        double max = _series.Max(s => s.Values.Count == 0 ? 0 : s.Values.Max());
        var ticks = SeriesMath.NiceTicks(max * 1.05, Math.Max(2, (int)((ActualHeight - legendH) / 45)), out double niceMax);
        double labelW = MeasureAxisLabelWidth(ticks, Fmt.Axis);
        var plot = new Rect(labelW + 10, legendH + 8, Math.Max(10, ActualWidth - labelW - 22), Math.Max(10, ActualHeight - legendH - 8 - 24));
        double duration = n - 1;

        DrawYAxis(dc, plot, ticks, niceMax, Fmt.Axis);
        DrawTimeAxis(dc, plot, duration);

        Point P(int i, double v) => new(plot.Left + i / duration * plot.Width, plot.Bottom - Math.Max(0, v) / niceMax * plot.Height);

        dc.PushClip(new RectangleGeometry(new Rect(plot.Left, plot.Top - 2, plot.Width + 2, plot.Height + 3)));
        var ordered = _series.OrderBy(s => s.EntityId == _highlight ? 1 : 0).ToList();
        foreach (var s in ordered)
        {
            if (s.Values.Count < 2) continue;
            bool strong = _highlight is null || s.EntityId == _highlight;
            var brush = BrushOf(s);
            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                g.BeginFigure(P(0, s.Values[0]), false, false);
                for (int i = 1; i < s.Values.Count; i++) g.LineTo(P(i, s.Values[i]), true, false);
            }
            line.Freeze();
            if (_highlight is not null && s.EntityId == _highlight)
            {
                var area = new StreamGeometry();
                using (var g = area.Open())
                {
                    g.BeginFigure(new Point(plot.Left, plot.Bottom), true, true);
                    for (int i = 0; i < s.Values.Count; i++) g.LineTo(P(i, s.Values[i]), true, false);
                    g.LineTo(new Point(P(s.Values.Count - 1, 0).X, plot.Bottom), true, false);
                }
                area.Freeze();
                dc.DrawGeometry(Fade(brush, 0.14), null, area);
            }
            double thickness = _highlight is null ? 1.8 : strong ? 2.6 : 1.3;
            dc.DrawGeometry(null, MakePen(strong ? brush : Fade(brush, 0.5), thickness), line);
        }
        dc.Pop();

        if (HoverPoint is { } hp && plot.Contains(hp))
        {
            int idx = (int)Math.Round((hp.X - plot.Left) / plot.Width * duration);
            idx = Math.Clamp(idx, 0, n - 1);
            double x = plot.Left + idx / duration * plot.Width;
            dc.DrawLine(MakePen(Fade(TextBrush, 0.45), 1, [3, 3]), new Point(x, plot.Top), new Point(x, plot.Bottom));
            var rows = new List<TooltipLine>();
            foreach (var s in _series.OrderByDescending(s => idx < s.Values.Count ? s.Values[idx] : 0))
            {
                double v = idx < s.Values.Count ? s.Values[idx] : 0;
                var brush = BrushOf(s);
                dc.DrawEllipse(brush, MakePen(SurfaceBrush, 1.5), P(idx, v), 3.5, 3.5);
                rows.Add(new TooltipLine(brush, s.Name, Fmt.Abbrev(v), s.EntityId == _highlight));
            }
            DrawTooltip(dc, new Point(x, hp.Y), $"{Fmt.Duration(idx)} · {ValueLabel} ({WindowSeconds} s avg)", rows);
        }
    }
}
