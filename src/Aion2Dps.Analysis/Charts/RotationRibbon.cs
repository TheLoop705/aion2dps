using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis;

/// <summary>
/// Horizontally pannable lane of skill tiles in cast order. Each tile is a class-coloured rounded square with the
/// skill's initials, sized by the cast's damage, with the damage under it and a leader line down to the landing time on
/// a seconds axis. Crits get a ring, summon casts a corner dot, DoT ticks are small marks above the axis.
/// Drag or scroll the wheel to pan; Ctrl+wheel zooms the time scale.
/// </summary>
public sealed class RotationRibbon : ChartBase
{
    private const double TileMin = 20, TileMax = 40, Gap = 5, LeftPad = 14, RightPad = 24;

    public static readonly DependencyProperty PixelsPerSecondProperty = DependencyProperty.Register(
        nameof(PixelsPerSecond), typeof(double), typeof(RotationRibbon),
        new FrameworkPropertyMetadata(46.0, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((RotationRibbon)d)._layoutDirty = true),
        v => v is double d && d >= 4 && d <= 400);

    private RotationData _data = RotationData.Empty;
    private IGameData? _gameData;
    private CharacterClass _ownerClass;
    private double _duration;
    private double _offset;
    private bool _layoutDirty = true;
    private readonly List<TileLayout> _tiles = new();
    private double _contentWidth;
    private uint? _highlightGroup;
    private readonly Dictionary<uint, (string Name, string Initials, CharacterClass Class)> _skillCache = new();

    private bool _dragging;
    private double _dragStartX, _dragStartOffset;

    private sealed record TileLayout(CastInfo Cast, double Center, double Size, double LandX, string Label, double SlotWidth);

    public RotationRibbon()
    {
        Cursor = Cursors.Hand;
        Focusable = false;
    }

    /// <summary>Minimum time scale in pixels per second (denser rotations are widened automatically).</summary>
    public double PixelsPerSecond
    {
        get => (double)GetValue(PixelsPerSecondProperty);
        set => SetValue(PixelsPerSecondProperty, value);
    }

    public RotationData Rotation => _data;

    /// <summary>Pan position in pixels from the start of the content.</summary>
    public double HorizontalOffset
    {
        get => _offset;
        set
        {
            EnsureLayout();
            double clamped = Math.Clamp(value, 0, Math.Max(0, _contentWidth - ActualWidth));
            if (Math.Abs(clamped - _offset) < 0.01) return;
            _offset = clamped;
            InvalidateVisual();
        }
    }

    /// <summary>Total width of the laid-out ribbon in pixels.</summary>
    public double ContentWidth
    {
        get { EnsureLayout(); return _contentWidth; }
    }

    /// <summary>Dims every tile whose skill group differs (null = no highlight).</summary>
    public uint? HighlightSkillGroup
    {
        get => _highlightGroup;
        set { _highlightGroup = value; InvalidateVisual(); }
    }

    protected override double DefaultHeight => 148;

    /// <summary>Rotation of <paramref name="actor"/> in <paramref name="record"/>.</summary>
    public void SetData(EncounterRecord record, uint actor, IGameData? gameData)
    {
        var combatant = ChartData.Find(record, actor);
        var rotation = CastGrouper.Build(record.Hits, actor, includeIncoming: combatant?.Kind == CombatantKind.EnemyPlayer);
        SetRotation(rotation, gameData, record.DurationSeconds, combatant?.Class ?? CharacterClass.Unknown);
    }

    public void SetRotation(RotationData rotation, IGameData? gameData, double durationSeconds, CharacterClass ownerClass)
    {
        _data = rotation;
        _gameData = gameData;
        _duration = Math.Max(durationSeconds, rotation.Casts.Count == 0 ? 0 : rotation.Casts[^1].EndT);
        _ownerClass = ownerClass;
        _offset = 0;
        _skillCache.Clear();
        _layoutDirty = true;
        InvalidateVisual();
    }

    /// <summary>Pans so <paramref name="seconds"/> is near the left edge.</summary>
    public void ScrollToTime(double seconds)
    {
        EnsureLayout();
        var first = _tiles.FirstOrDefault(t => t.Cast.StartT >= seconds);
        HorizontalOffset = (first?.Center ?? LeftPad + seconds * _pps) - 40;
    }

    private (string Name, string Initials, CharacterClass Class) SkillInfo(uint skill)
    {
        if (!_skillCache.TryGetValue(skill, out var info))
        {
            string name = _gameData?.GetSkillName(skill) ?? "Skill " + skill;
            info = (name, SkillVisuals.Initials(name, skill), SkillVisuals.TileClass(_gameData, skill, _ownerClass));
            _skillCache[skill] = info;
        }
        return info;
    }

    private uint GroupOf(uint skill) => _gameData?.GetSkillGroupKey(skill) ?? SkillIds.BaseId(skill);

    private double LabelFont => BaseFontSize * 0.78;

    private void EnsureLayout()
    {
        if (!_layoutDirty) return;
        _layoutDirty = false;
        _tiles.Clear();
        long maxAmount = _data.Casts.Count == 0 ? 1 : Math.Max(1, _data.Casts.Max(c => c.Amount));
        var measured = new List<(CastInfo Cast, double Size, string Label, double Slot)>(_data.Casts.Count);
        foreach (var c in _data.Casts)
        {
            double size = TileMin + (TileMax - TileMin) * Math.Sqrt(Math.Max(0, (double)c.Amount) / maxAmount);
            string label = c.Amount <= 0 ? (c.Avoided > 0 ? "miss" : "0") : (c.IsHeal ? "+" : "") + Fmt.Number(c.Amount);
            double labelW = Text(label, LabelFont, MutedBrush, mono: true).Width;
            measured.Add((c, size, label, Math.Max(size, labelW) + Gap));
        }

        // Auto scale: wide enough that the casts fit along the axis on average, so leader lines stay short.
        double density = _duration > 0 ? measured.Sum(m => m.Slot) / _duration * 1.08 : 0;
        _pps = Math.Max(PixelsPerSecond, density) * _zoom;
        double prevRight = 0;
        foreach (var (c, size, label, slot) in measured)
        {
            double land = LeftPad + c.StartT * _pps;
            double center = Math.Max(land, prevRight + slot / 2);
            prevRight = center + slot / 2;
            _tiles.Add(new TileLayout(c, center, size, land, label, slot));
        }
        _contentWidth = Math.Max(prevRight, LeftPad + _duration * _pps) + RightPad;
    }

    private double _pps = 46;
    private double _zoom = 1;

    /// <summary>Zoom factor on top of the automatic time scale (Ctrl+wheel changes it; 0.2 … 5).</summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            _zoom = Math.Clamp(value, 0.2, 5);
            _layoutDirty = true;
            InvalidateVisual();
        }
    }

    /// <summary>The time scale in use (pixels per second) after auto-fit and zoom.</summary>
    public double EffectivePixelsPerSecond
    {
        get { EnsureLayout(); return _pps; }
    }

    private (double laneCenterY, double labelY, double leaderTop, double axisY) Rows()
    {
        double headline = BaseFontSize * 1.6;
        double laneCenter = headline + 4 + TileMax / 2;
        double labelY = laneCenter + TileMax / 2 + 3;
        double leaderTop = labelY + LabelFont * 1.35 + 2;
        double axisY = Math.Max(leaderTop + 14, ActualHeight - 30);
        return (laneCenter, labelY, leaderTop, axisY);
    }

    protected override void OnRender(DrawingContext dc)
    {
        DrawHitArea(dc);
        if (ActualWidth < 40 || ActualHeight < 60) return;
        if (_data.Casts.Count == 0 && _data.DotTicks.Count == 0)
        {
            DrawCentered(dc, "No casts recorded");
            return;
        }
        EnsureLayout();
        _offset = Math.Clamp(_offset, 0, Math.Max(0, _contentWidth - ActualWidth));
        double pps = _pps;
        var (laneY, labelY, leaderTop, axisY) = Rows();
        double viewL = _offset, viewR = _offset + ActualWidth;

        // Headline: what is visible.
        double tFrom = Math.Max(0, (viewL - LeftPad) / pps), tTo = Math.Max(0, (viewR - LeftPad) / pps);
        var visible = _tiles.Where(t => t.Center >= viewL && t.Center <= viewR).ToList();
        DrawHeadline(dc, visible, tFrom, Math.Min(tTo, _duration));

        dc.PushClip(new RectangleGeometry(new Rect(0, BaseFontSize * 1.6, ActualWidth, ActualHeight)));
        dc.PushTransform(new TranslateTransform(-_offset, 0));

        // Axis.
        var axisPen = MakePen(GridBrush, 1);
        dc.DrawLine(axisPen, new Point(Math.Max(LeftPad, viewL), axisY + 0.5), new Point(Math.Min(_contentWidth - RightPad + 6, viewR), axisY + 0.5));
        double labelStep = pps >= 40 ? 5 : pps >= 15 ? 10 : pps >= 6 ? 30 : 60;
        double minorStep = pps >= 20 ? 1 : labelStep / 5;
        int firstTick = (int)Math.Max(0, Math.Floor(tFrom / minorStep));
        for (int i = firstTick; i * minorStep <= Math.Min(_duration, tTo + minorStep); i++)
        {
            double t = i * minorStep;
            double x = Math.Round(LeftPad + t * pps) + 0.5;
            bool major = Math.Abs(t / labelStep - Math.Round(t / labelStep)) < 1e-6;
            dc.DrawLine(axisPen, new Point(x, axisY), new Point(x, axisY + (major ? 6 : 3)));
            if (major)
            {
                var ft = Text(Fmt.Duration(t), BaseFontSize * 0.8, MutedBrush, mono: true);
                dc.DrawText(ft, new Point(x - ft.Width / 2, axisY + 7));
            }
        }

        // DoT ticks above the axis.
        var dotPen = MakePen(DotBrush, 2);
        foreach (var d in _data.DotTicks)
        {
            double x = LeftPad + d.T * pps;
            if (x < viewL - 2 || x > viewR + 2) continue;
            bool dim = _highlightGroup is { } hg && GroupOf(d.Skill) != hg;
            dc.DrawLine(dim ? MakePen(Fade(DotBrush, 0.3), 2) : dotPen, new Point(x, axisY - 2), new Point(x, axisY - 9));
        }

        // Leader lines first (under tiles), then tiles.
        var leaderPen = MakePen(Fade(MutedBrush, 0.55), 1);
        foreach (var t in visible.Concat(_tiles.Where(t => (t.LandX >= viewL && t.LandX <= viewR) && !(t.Center >= viewL && t.Center <= viewR))))
        {
            dc.DrawLine(leaderPen, new Point(t.Center, leaderTop), new Point(t.LandX, axisY - 1));
            dc.DrawEllipse(MutedBrush, null, new Point(t.LandX, axisY), 1.6, 1.6);
        }

        CastInfo? hovered = null;
        Point? hoverContent = HoverPoint is { } hp ? new Point(hp.X + _offset, hp.Y) : null;
        foreach (var t in _tiles)
        {
            if (t.Center + t.SlotWidth < viewL || t.Center - t.SlotWidth > viewR) continue;
            var info = SkillInfo(t.Cast.Skill);
            bool dim = _highlightGroup is { } hg && GroupOf(t.Cast.Skill) != hg;
            var rect = new Rect(t.Center - t.Size / 2, laneY + TileMax / 2 - t.Size, t.Size, t.Size);
            var fill = ClassBrush(info.Class);
            dc.PushOpacity(dim ? 0.28 : 1);
            double r = t.Size * 0.22;
            dc.DrawRoundedRectangle(fill, MakePen(Fade(TextBrush, 0.12), 1), rect, r, r);
            // Subtle top highlight for depth.
            dc.DrawRoundedRectangle(Fade(Brushes.White, 0.10), null, new Rect(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height * 0.45), r * 0.8, r * 0.8);
            var ini = Text(info.Initials, Math.Max(8, t.Size * 0.4), SkillVisuals.ContrastText(fill), FontWeights.Bold);
            dc.DrawText(ini, new Point(t.Center - ini.Width / 2, rect.Top + (t.Size - ini.Height) / 2));
            if (t.Cast.MostlyCrit)
                dc.DrawRoundedRectangle(null, MakePen(CritBrush, 2), Rect.Inflate(rect, 2.5, 2.5), r + 2, r + 2);
            if (t.Cast.FromSummon)
                dc.DrawEllipse(SurfaceBrush, MakePen(fill, 1.5), new Point(rect.Right - 1, rect.Top + 1), 3.5, 3.5);
            var lbl = Text(t.Label, LabelFont, t.Cast.IsHeal ? HealBrush : t.Cast.MostlyCrit ? CritBrush : MutedBrush,
                t.Cast.MostlyCrit ? FontWeights.SemiBold : FontWeights.Normal, mono: true);
            dc.DrawText(lbl, new Point(t.Center - lbl.Width / 2, labelY));
            dc.Pop();
            if (hoverContent is { } hc && Rect.Inflate(rect, 3, 3).Contains(hc)) hovered = t.Cast;
        }

        // Hovered DoT tick?
        DotTickMark? hoveredDot = null;
        if (hovered is null && hoverContent is { } hc2 && hc2.Y >= axisY - 12 && hc2.Y <= axisY + 2)
        {
            double best = 4;
            foreach (var d in _data.DotTicks)
            {
                double dist = Math.Abs(LeftPad + d.T * pps - hc2.X);
                if (dist < best) { best = dist; hoveredDot = d; }
            }
        }
        dc.Pop(); // transform
        dc.Pop(); // clip

        // Scroll indicator.
        if (_contentWidth > ActualWidth + 1)
        {
            double trackY = ActualHeight - 3;
            dc.DrawRoundedRectangle(BarTrackBrush, null, new Rect(0, trackY, ActualWidth, 3), 1.5, 1.5);
            double thumbW = Math.Max(24, ActualWidth * ActualWidth / _contentWidth);
            double thumbX = (ActualWidth - thumbW) * (_offset / (_contentWidth - ActualWidth));
            dc.DrawRoundedRectangle(MutedBrush, null, new Rect(thumbX, trackY, thumbW, 3), 1.5, 1.5);
        }

        if (HoverPoint is { } p)
        {
            if (hovered is not null)
            {
                var info = SkillInfo(hovered.Skill);
                var rows = new List<TooltipLine>
                {
                    new(null, "Landed", Fmt.PreciseTime(hovered.StartT)),
                    new(null, hovered.IsHeal ? "Healing" : "Damage", Fmt.Exact(hovered.Amount), true),
                    new(null, "Hits", hovered.Hits.ToString()),
                    new(null, "Crits", hovered.Crits == 0 ? "none" : hovered.Crits.ToString()),
                };
                if (hovered.Hits > 1) rows.Add(new(null, "Biggest hit", Fmt.Exact(hovered.MaxHit)));
                if (hovered.Avoided > 0) rows.Add(new(null, "Dodged/parried", hovered.Avoided.ToString()));
                if (hovered.FromSummon) rows.Add(new(null, "Source", "summon"));
                DrawTooltip(dc, p, info.Name, rows);
            }
            else if (hoveredDot is { } d)
            {
                DrawTooltip(dc, p, SkillInfo(d.Skill).Name, [new(DotBrush, "DoT tick", Fmt.Exact(d.Amount), true), new(null, "At", Fmt.PreciseTime(d.T))]);
            }
        }
    }

    private void DrawHeadline(DrawingContext dc, List<TileLayout> visible, double from, double to)
    {
        double fs = BaseFontSize * 0.88;
        var dmgCasts = visible.Where(v => !v.Cast.IsHeal).Select(v => v.Cast).ToList();
        long dmg = dmgCasts.Sum(c => c.Amount) + _data.DotTicks.Where(d => d.T >= from && d.T <= to).Sum(d => d.Amount);
        int hits = dmgCasts.Sum(c => c.Hits), crits = dmgCasts.Sum(c => c.Crits);
        var biggest = dmgCasts.OrderByDescending(c => c.MaxHit).FirstOrDefault();
        double span = Math.Max(0.1, to - from);
        string text = $"{Fmt.Duration(from)}–{Fmt.Duration(to)}  ·  {visible.Count} casts  ·  {Fmt.Number(dmg)} dmg  ·  " +
                      $"{Fmt.Dps(dmg / span)} DPS  ·  crit {Fmt.Rate(crits, hits, 0)}";
        if (biggest is not null) text += $"  ·  biggest {Fmt.Number(biggest.MaxHit)} ({SkillInfo(biggest.Skill).Name})";
        var ft = Text(text, fs, MutedBrush);
        ft.MaxTextWidth = Math.Max(10, ActualWidth - 140);
        ft.MaxLineCount = 1;
        ft.Trimming = TextTrimming.CharacterEllipsis;
        dc.DrawText(ft, new Point(4, 2));
        if (_contentWidth > ActualWidth + 1)
        {
            string hint = _offset <= 0.5 ? "drag or scroll →" : _offset >= _contentWidth - ActualWidth - 0.5 ? "← drag or scroll" : "← drag or scroll →";
            var h = Text(hint, fs, Fade(MutedBrush, 0.8));
            dc.DrawText(h, new Point(ActualWidth - h.Width - 4, 2));
        }
    }

    // ───────────── Interaction ─────────────

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (ContentWidth <= ActualWidth) return;
        _dragging = CaptureMouse();
        _dragStartX = e.GetPosition(this).X;
        _dragStartOffset = _offset;
        Cursor = Cursors.SizeWE;
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        Cursor = Cursors.Hand;
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = false;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) HorizontalOffset = _dragStartOffset - (e.GetPosition(this).X - _dragStartX);
        base.OnMouseMove(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            double mouseX = e.GetPosition(this).X;
            double t = (mouseX + _offset - LeftPad) / _pps;
            Zoom = _zoom * (e.Delta > 0 ? 1.2 : 1 / 1.2);
            EnsureLayout();
            HorizontalOffset = LeftPad + t * _pps - mouseX;
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (ContentWidth <= ActualWidth) return;
        double before = _offset;
        HorizontalOffset = _offset - e.Delta * 0.6;
        if (Math.Abs(before - _offset) > 0.01) e.Handled = true;
    }
}
