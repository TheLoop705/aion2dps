namespace Aion2Dps.App.Controls;

/// <summary>
/// Horizontal panel whose first child (a trimming TextBlock) gets whatever width is left after the other children
/// (badges), and the badges are placed directly after the first child's text instead of at the far edge. When the
/// space is so tight that the text would be cut below <see cref="MinTextWidth"/>, the badges are dropped instead.
/// </summary>
public sealed class TrimPanel : Panel
{
    /// <summary>Text width kept before badges start to be hidden.</summary>
    public double MinTextWidth { get; set; } = 64;

    private bool _hideBadges;

    protected override Size MeasureOverride(Size availableSize)
    {
        double badges = 0, height = 0;
        for (int i = 1; i < InternalChildren.Count; i++)
        {
            var c = InternalChildren[i];
            c.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            badges += c.DesiredSize.Width;
            height = Math.Max(height, c.DesiredSize.Height);
        }
        if (InternalChildren.Count == 0) return new Size();
        var first = InternalChildren[0];
        _hideBadges = false;
        if (!double.IsInfinity(availableSize.Width) && badges > 0)
        {
            first.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            double natural = first.DesiredSize.Width;
            if (availableSize.Width - badges < Math.Min(natural, MinTextWidth))
            {
                _hideBadges = true;
                badges = 0;
            }
        }
        double avail = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : Math.Max(0, availableSize.Width - badges);
        first.Measure(new Size(avail, availableSize.Height));
        height = Math.Max(height, first.DesiredSize.Height);
        double width = first.DesiredSize.Width + badges;
        if (!double.IsInfinity(availableSize.Width)) width = Math.Min(width, availableSize.Width);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count == 0) return finalSize;
        double badges = 0;
        if (!_hideBadges)
            for (int i = 1; i < InternalChildren.Count; i++) badges += InternalChildren[i].DesiredSize.Width;
        var first = InternalChildren[0];
        double firstW = Math.Max(0, Math.Min(first.DesiredSize.Width, finalSize.Width - badges));
        first.Arrange(new Rect(0, 0, firstW, finalSize.Height));
        double x = firstW;
        for (int i = 1; i < InternalChildren.Count; i++)
        {
            var c = InternalChildren[i];
            if (_hideBadges)
            {
                c.Arrange(new Rect(x, 0, 0, 0));
                continue;
            }
            double w = c.DesiredSize.Width;
            c.Arrange(new Rect(x, (finalSize.Height - c.DesiredSize.Height) / 2, w, c.DesiredSize.Height));
            x += w;
        }
        return finalSize;
    }
}
