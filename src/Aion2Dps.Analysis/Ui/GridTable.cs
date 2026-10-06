using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis;

/// <summary>Column of a <see cref="GridTable{T}"/>.</summary>
internal sealed class TableColumn<T>
{
    public TableColumn(string header, double width, Func<T, UIElement> cell, Func<T, IComparable?>? sortKey = null,
        HorizontalAlignment align = HorizontalAlignment.Right, string? headerTip = null)
    {
        Header = header;
        Width = width;
        Cell = cell;
        SortKey = sortKey;
        Align = align;
        HeaderTip = headerTip;
    }

    public string Header { get; }
    /// <summary>Pixels; ≤ 0 means a star column (weight = −width, or 1 when 0).</summary>
    public double Width { get; }
    public Func<T, UIElement> Cell { get; }
    public Func<T, IComparable?>? SortKey { get; }
    public HorizontalAlignment Align { get; }
    public string? HeaderTip { get; }
}

/// <summary>
/// Light-weight themed table: fixed and star columns (so rows line up without a shared-size scope), sortable headers,
/// alternating rows, hover, optional selection, double-click, an emphasis marker, and full-height layout (no inner
/// scrolling, so a "copy as image" captures every row).
/// </summary>
internal sealed class GridTable<T> : Border where T : class
{
    private readonly IReadOnlyList<TableColumn<T>> _columns;
    private readonly StackPanel _rowsPanel = new();
    private readonly Grid _header;
    private List<T> _items = new();
    private int _sortColumn = -1;
    private bool _sortDescending = true;
    private T? _selected;
    private readonly Dictionary<T, Rectangle> _selectionRects = new(ReferenceEqualityComparer.Instance);

    public GridTable(IReadOnlyList<TableColumn<T>> columns, int defaultSortColumn = -1, bool descending = true)
    {
        _columns = columns;
        _sortColumn = defaultSortColumn;
        _sortDescending = descending;
        _header = BuildRowGrid();
        _header.Margin = new Thickness(0, 0, 0, 2);
        var root = new StackPanel();
        root.Children.Add(_header);
        var line = Ui.Separator(0);
        root.Children.Add(line);
        root.Children.Add(_rowsPanel);
        Child = root;
        BuildHeader();
    }

    public event Action<T>? RowClicked;
    public event Action<T>? RowDoubleClicked;

    /// <summary>Rows for which this returns a theme brush key get a coloured marker on the left edge.</summary>
    public Func<T, string?>? RowMarker { get; set; }

    public bool Selectable { get; set; }
    public double RowHeight { get; set; } = 28;
    public int MaxRows { get; set; } = int.MaxValue;

    public IReadOnlyList<T> Items => _items;
    public int RowCount => _rowsPanel.Children.Count;

    public T? Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            foreach (var (item, rect) in _selectionRects) rect.Opacity = ReferenceEquals(item, value) ? 0.2 : 0;
        }
    }

    public void SetItems(IEnumerable<T> items)
    {
        _items = items.ToList();
        Rebuild();
    }

    /// <summary>Sorts by column index (header click does the same).</summary>
    public void SortBy(int column, bool descending)
    {
        _sortColumn = column;
        _sortDescending = descending;
        BuildHeader();
        Rebuild();
    }

    private Grid BuildRowGrid()
    {
        var g = new Grid();
        foreach (var c in _columns)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = c.Width > 0 ? new GridLength(c.Width) : new GridLength(c.Width < 0 ? -c.Width : 1, GridUnitType.Star),
                MinWidth = c.Width > 0 ? 0 : 80,
            });
        }
        return g;
    }

    private void BuildHeader()
    {
        _header.Children.Clear();
        for (int i = 0; i < _columns.Count; i++)
        {
            var col = _columns[i];
            int index = i;
            string arrow = i == _sortColumn ? (_sortDescending ? " ▾" : " ▴") : "";
            var tb = Ui.Text(col.Header.ToUpperInvariant() + arrow, Ui.CaptionSize, i == _sortColumn ? ThemeKeys.Text : ThemeKeys.TextMuted,
                FontWeights.SemiBold, tip: col.HeaderTip);
            tb.HorizontalAlignment = col.Align;
            tb.Margin = new Thickness(6, 4, 6, 5);
            var hit = new Border { Background = Brushes.Transparent, Child = tb };
            if (col.SortKey is not null)
            {
                hit.Cursor = Cursors.Hand;
                hit.MouseLeftButtonUp += (_, e) =>
                {
                    if (_sortColumn == index) _sortDescending = !_sortDescending;
                    else
                    {
                        _sortColumn = index;
                        _sortDescending = col.Align == HorizontalAlignment.Right; // numbers: big first, text: A→Z
                    }
                    BuildHeader();
                    Rebuild();
                    e.Handled = true;
                };
            }
            Grid.SetColumn(hit, i);
            _header.Children.Add(hit);
        }
    }

    private IEnumerable<T> Sorted()
    {
        if (_sortColumn < 0 || _sortColumn >= _columns.Count || _columns[_sortColumn].SortKey is not { } key) return _items;
        var cmp = Comparer<IComparable?>.Create((a, b) =>
            a is null && b is null ? 0 : a is null ? -1 : b is null ? 1 : a.CompareTo(b));
        return _sortDescending ? _items.OrderByDescending(key, cmp) : _items.OrderBy(key, cmp);
    }

    private void Rebuild()
    {
        _rowsPanel.Children.Clear();
        _selectionRects.Clear();
        int i = 0;
        foreach (var item in Sorted())
        {
            if (i >= MaxRows) break;
            _rowsPanel.Children.Add(BuildRow(item, i++));
        }
        if (i == 0) _rowsPanel.Children.Add(Ui.Empty("Nothing to show"));
    }

    private UIElement BuildRow(T item, int index)
    {
        var row = new Grid { MinHeight = RowHeight, Background = Brushes.Transparent };
        var stripe = new Rectangle { Opacity = index % 2 == 1 ? 0.55 : 0, RadiusX = 3, RadiusY = 3 };
        stripe.SetResourceReference(Shape.FillProperty, ThemeKeys.SurfaceAlt);
        var sel = new Rectangle { Opacity = ReferenceEquals(item, _selected) ? 0.2 : 0, RadiusX = 3, RadiusY = 3 };
        sel.SetResourceReference(Shape.FillProperty, ThemeKeys.Accent);
        _selectionRects[item] = sel;
        row.Children.Add(stripe);
        row.Children.Add(sel);
        if (RowMarker?.Invoke(item) is { } markerKey)
        {
            var marker = new Rectangle { Width = 3, HorizontalAlignment = HorizontalAlignment.Left, RadiusX = 1.5, RadiusY = 1.5, Margin = new Thickness(0, 4, 0, 4) };
            marker.SetResourceReference(Shape.FillProperty, markerKey);
            row.Children.Add(marker);
        }
        var cells = BuildRowGrid();
        for (int c = 0; c < _columns.Count; c++)
        {
            var el = _columns[c].Cell(item);
            if (el is FrameworkElement fe)
            {
                fe.Margin = new Thickness(6, 3, 6, 3);
                if (fe.HorizontalAlignment == HorizontalAlignment.Stretch && _columns[c].Align != HorizontalAlignment.Left && fe is TextBlock)
                    fe.HorizontalAlignment = _columns[c].Align;
                fe.VerticalAlignment = VerticalAlignment.Center;
            }
            Grid.SetColumn(el, c);
            cells.Children.Add(el);
        }
        row.Children.Add(cells);
        row.MouseEnter += (_, _) => stripe.Opacity = 1;
        row.MouseLeave += (_, _) => stripe.Opacity = index % 2 == 1 ? 0.55 : 0;
        row.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                RowDoubleClicked?.Invoke(item);
                e.Handled = true;
                return;
            }
            if (Selectable) Selected = item;
            RowClicked?.Invoke(item);
        };
        if (RowDoubleClicked is not null || Selectable || RowClicked is not null) row.Cursor = Cursors.Hand;
        return row;
    }
}
