using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis;

/// <summary>
/// Saved-fight browser: filters (character, instance, boss, kind, time range, kills only), stat tiles, and a list
/// grouped Instance → Boss → fights. Selecting a fight shows its <see cref="EncounterReportView"/> (or
/// <see cref="PvpReviewView"/> for PvP) on the right. Every store call runs off the UI thread.
/// </summary>
public sealed class HistoryView : UserControl
{
    private const double LeftWidth = 500;
    private const double StackedBreakpointDetail = 680;

    private readonly Grid _layout;
    private readonly FrameworkElement _layoutLeft;
    private readonly FrameworkElement _layoutRight;
    private readonly Border _layoutDivider;
    private bool _stacked;

    private IFightStore? _store;
    private IGameData? _gameData;

    private string? _character;
    private uint? _mapId;
    private uint? _bossCode;
    private EncounterKind? _kind;
    private int _rangeDays;
    private bool _killsOnly;

    private readonly ComboBox _characterBox = new() { ToolTip = "Character" };
    private readonly ComboBox _mapBox = new() { ToolTip = "Instance / map" };
    private readonly ComboBox _bossBox = new() { ToolTip = "Boss" };
    private readonly CheckBox _killsBox = new() { Content = "Kills only", VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl _tilesHost = new() { Focusable = false };
    private readonly StackPanel _list = new() { Margin = new Thickness(0, 0, 8, 8) };
    private readonly ScrollViewer _listScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    private readonly TextBlock _status = Ui.Text("", 11.5, ThemeKeys.TextMuted);
    private readonly ContentControl _detailHost = new() { Focusable = false };
    private readonly TextBlock _detailTitle = Ui.Text("", 13, ThemeKeys.TextMuted, FontWeights.SemiBold);
    private readonly Button _deleteButton;
    private readonly FrameworkElement _confirmBar;
    private readonly HashSet<string> _collapsed = new();
    private readonly Dictionary<Guid, Rectangle> _rowSelection = new();
    private bool _suppress;
    private int _refreshVersion;
    private int _loadVersion;
    private List<FightSummary> _fights = new();
    private Guid? _selected;

    public HistoryView()
    {
        Resources.MergedDictionaries.Add(new AnalysisStyles());
        Ui.ApplyRootTheme(this);
        _deleteButton = Ui.Button("Delete", RequestDelete, "Delete the selected fight", "A2.DangerButton");
        _deleteButton.IsEnabled = false;
        _confirmBar = BuildConfirmBar();
        _confirmBar.Visibility = Visibility.Collapsed;

        var left = BuildLeft();
        var right = BuildRight();
        _layout = new Grid();
        _layoutLeft = left;
        _layoutRight = right;
        _layoutDivider = new Border();
        _layoutDivider.SetResourceReference(Border.BackgroundProperty, ThemeKeys.Border);
        _layout.Children.Add(left);
        _layout.Children.Add(_layoutDivider);
        _layout.Children.Add(right);
        ApplyLayout(stacked: false);
        // Side by side needs the list plus a full report (~680 px); narrower hosts (the default dashboard window)
        // get the report below the list instead of a clipped right half.
        SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width <= 0) return;
            bool stacked = e.NewSize.Width < LeftWidth + 1 + StackedBreakpointDetail;
            if (stacked != _stacked) ApplyLayout(stacked);
        };
        Content = _layout;

        _characterBox.SelectionChanged += (_, _) => OnFilterBox(_characterBox, v => _character = v as string);
        _mapBox.SelectionChanged += (_, _) => OnFilterBox(_mapBox, v => { _mapId = v as uint?; _bossCode = null; });
        _bossBox.SelectionChanged += (_, _) => OnFilterBox(_bossBox, v => _bossCode = v as uint?);
        _killsBox.Checked += (_, _) => { if (_suppress) return; _killsOnly = true; _ = RefreshAsync(); };
        _killsBox.Unchecked += (_, _) => { if (_suppress) return; _killsOnly = false; _ = RefreshAsync(); };
        ShowPlaceholder("Select a fight to see its report.");
    }

    /// <summary>A fight was loaded and shown on the right.</summary>
    public event Action<EncounterRecord>? FightOpened;

    /// <summary>A fight was deleted from the store.</summary>
    public event Action<Guid>? FightDeleted;

    /// <summary>Clock for "time ago" labels (UTC).</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>Fights currently listed (after filters).</summary>
    public IReadOnlyList<FightSummary> Fights => _fights;

    public Guid? SelectedFightId => _selected;

    /// <summary>The report/review currently shown on the right, if any.</summary>
    public FrameworkElement? DetailView => _detailHost.Content is EncounterReportView or PvpReviewView ? (FrameworkElement)_detailHost.Content : null;

    /// <summary>The refresh started by the latest filter change or <see cref="Initialize"/>.</summary>
    public Task? PendingRefresh { get; private set; }

    public void Initialize(IFightStore store, IGameData gameData)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
        PendingRefresh = RefreshAsync();
    }

    /// <summary>Sets all filters at once and refreshes.</summary>
    public Task SetFiltersAsync(string? character = null, uint? mapId = null, uint? bossNpcCode = null, EncounterKind? kind = null,
        int rangeDays = 0, bool killsOnly = false)
    {
        _character = character;
        _mapId = mapId;
        _bossCode = bossNpcCode;
        _kind = kind;
        _rangeDays = rangeDays;
        _killsOnly = killsOnly;
        _suppress = true;
        _killsBox.IsChecked = killsOnly;
        _suppress = false;
        RebuildSegments();
        return RefreshAsync();
    }

    public Task RefreshAsync()
    {
        var t = RefreshCoreAsync();
        PendingRefresh = t;
        return t;
    }

    private async Task RefreshCoreAsync()
    {
        if (_store is null || _gameData is null) return;
        int version = ++_refreshVersion;
        var store = _store;
        DateTime? from = _rangeDays > 0 ? Clock().AddDays(-_rangeDays) : null;
        var listQuery = new FightQuery
        {
            LocalPlayerName = _character, MapId = _mapId, BossNpcCode = _bossCode, Kind = _kind, FromUtc = from,
            KillsOnly = _killsOnly, Limit = 2000,
        };
        var optionQuery = new FightQuery { LocalPlayerName = _character, Kind = _kind, FromUtc = from, Limit = 5000 };
        _status.Text = "Loading…";
        try
        {
            var (characters, options, fights) = await Task.Run(() => (store.GetCharacters(), store.Query(optionQuery), store.Query(listQuery)));
            if (version != _refreshVersion) return;
            ApplyOptions(characters, options);
            _fights = fights.OrderByDescending(f => f.StartUtc).ToList();
            RebuildTiles();
            RebuildList();
            _status.Text = _fights.Count == 0 ? "No fights match these filters." : $"{_fights.Count} fights";
        }
        catch (Exception ex)
        {
            AppLog.Error("Analysis", "History refresh failed", ex);
            if (version == _refreshVersion) _status.Text = "Could not load the history: " + ex.Message;
        }
    }

    /// <summary>Loads a fight off the UI thread and shows it on the right.</summary>
    public async Task SelectFightAsync(Guid id)
    {
        if (_store is null || _gameData is null) return;
        int version = ++_loadVersion;
        _selected = id;
        foreach (var (fid, rect) in _rowSelection) rect.Opacity = fid == id ? 0.22 : 0;
        _deleteButton.IsEnabled = true;
        _confirmBar.Visibility = Visibility.Collapsed;
        ShowPlaceholder("Loading fight…");
        var store = _store;
        try
        {
            var record = await Task.Run(() => store.Load(id));
            if (version != _loadVersion) return;
            if (record is null)
            {
                ShowPlaceholder("This fight is no longer in the history.");
                return;
            }
            _detailTitle.Text = $"{ChartData.Title(record, _gameData)} · {Fmt.LocalDateTime(record.StartUtc)}";
            _detailHost.Content = record.Kind == EncounterKind.Pvp
                ? new PvpReviewView(record, _gameData)
                : new EncounterReportView(record, _gameData);
            FightOpened?.Invoke(record);
        }
        catch (Exception ex)
        {
            AppLog.Error("Analysis", "Loading fight failed", ex);
            if (version == _loadVersion) ShowPlaceholder("Could not load this fight: " + ex.Message);
        }
    }

    /// <summary>Shows the delete confirmation for the selected fight.</summary>
    public void RequestDelete()
    {
        if (_selected is null) return;
        _confirmBar.Visibility = Visibility.Visible;
    }

    /// <summary>Deletes the selected fight (after confirmation) off the UI thread and refreshes.</summary>
    public async Task<bool> ConfirmDeleteAsync()
    {
        _confirmBar.Visibility = Visibility.Collapsed;
        if (_store is null || _selected is not { } id) return false;
        var store = _store;
        bool ok;
        try
        {
            ok = await Task.Run(() => store.Delete(id));
        }
        catch (Exception ex)
        {
            AppLog.Error("Analysis", "Deleting fight failed", ex);
            _status.Text = "Could not delete the fight: " + ex.Message;
            return false;
        }
        _selected = null;
        _deleteButton.IsEnabled = false;
        ShowPlaceholder(ok ? "Fight deleted." : "The fight was already gone.");
        if (ok) FightDeleted?.Invoke(id);
        await RefreshAsync();
        return ok;
    }

    // ───────────── Layout ─────────────

    /// <summary>True when the report is shown below the fight list (narrow hosts).</summary>
    public bool IsStacked => _stacked;

    private void ApplyLayout(bool stacked)
    {
        _stacked = stacked;
        _layout.ColumnDefinitions.Clear();
        _layout.RowDefinitions.Clear();
        if (stacked)
        {
            _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
            _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Place(_layoutLeft, 0, 0);
            Place(_layoutDivider, 1, 0);
            Place(_layoutRight, 2, 0);
            _layoutDivider.Margin = new Thickness(0, 12, 0, 0);
        }
        else
        {
            _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LeftWidth), MinWidth = 420 });
            _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Place(_layoutLeft, 0, 0);
            Place(_layoutDivider, 0, 1);
            Place(_layoutRight, 0, 2);
            _layoutDivider.Margin = new Thickness(0);
        }

        static void Place(UIElement e, int row, int column)
        {
            Grid.SetRow(e, row);
            Grid.SetColumn(e, column);
        }
    }

    private FrameworkElement BuildLeft()
    {
        var dock = new DockPanel { Margin = new Thickness(16, 14, 8, 0), LastChildFill = true };

        var head = new DockPanel { Margin = new Thickness(0, 0, 8, 10) };
        var refresh = Ui.Button("Refresh", () => _ = RefreshAsync(), "Reload the history");
        DockPanel.SetDock(refresh, Dock.Right);
        head.Children.Add(refresh);
        head.Children.Add(Ui.Text("Fight history", 19, ThemeKeys.Text, FontWeights.SemiBold));
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);

        var filters = new StackPanel { Margin = new Thickness(0, 0, 8, 6) };
        var combos = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -6, 8) };
        combos.Children.Add(Labeled("Character", _characterBox));
        combos.Children.Add(Labeled("Instance", _mapBox));
        combos.Children.Add(Labeled("Boss", _bossBox));
        filters.Children.Add(combos);
        filters.Children.Add(_kindHost);
        var rangeRow = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(_killsBox, Dock.Right);
        _killsBox.Margin = new Thickness(8, 0, 0, 4);
        rangeRow.Children.Add(_killsBox);
        rangeRow.Children.Add(_rangeHost);
        filters.Children.Add(rangeRow);
        RebuildSegments();
        DockPanel.SetDock(filters, Dock.Top);
        dock.Children.Add(filters);

        _tilesHost.Margin = new Thickness(0, 4, 8, 4);
        DockPanel.SetDock(_tilesHost, Dock.Top);
        dock.Children.Add(_tilesHost);

        var statusRow = new DockPanel { Margin = new Thickness(0, 0, 8, 4) };
        statusRow.Children.Add(_status);
        DockPanel.SetDock(statusRow, Dock.Top);
        dock.Children.Add(statusRow);

        var colHead = FightRowGrid();
        AddCell(colHead, 0, Ui.Caption(""));
        AddCell(colHead, 1, Ui.Caption("When"));
        AddCell(colHead, 2, RightCaption("Dmg", "Party damage"));
        AddCell(colHead, 3, RightCaption("DPS", "Party DPS"));
        AddCell(colHead, 4, RightCaption("You", "Your DPS"));
        AddCell(colHead, 5, RightCaption("Time"));
        AddCell(colHead, 6, RightCaption("Pl", "Players"));
        colHead.Margin = new Thickness(0, 4, 16, 4);
        DockPanel.SetDock(colHead, Dock.Top);
        dock.Children.Add(colHead);

        _listScroll.Content = _list;
        dock.Children.Add(_listScroll);
        return dock;
    }

    private readonly ContentControl _kindHost = new() { Focusable = false };
    private readonly ContentControl _rangeHost = new() { Focusable = false };
    private readonly string _segGroup = Guid.NewGuid().ToString("N");

    private void RebuildSegments()
    {
        _kindHost.Content = Ui.Segments<EncounterKind?>(
        [
            ("All kinds", null), ("Boss", EncounterKind.Boss), ("Dummy", EncounterKind.Dummy), ("PvP", EncounterKind.Pvp),
            ("Trash", EncounterKind.Trash), ("Training", EncounterKind.Training),
        ], _kind, v => { if (_kind != v) { _kind = v; _mapId = null; _bossCode = null; _ = RefreshAsync(); } }, "kind" + _segGroup);
        _rangeHost.Content = Ui.Segments<int>([("All time", 0), ("7 days", 7), ("30 days", 30), ("90 days", 90)], _rangeDays,
            v => { if (_rangeDays != v) { _rangeDays = v; _ = RefreshAsync(); } }, "range" + _segGroup);
    }

    private static FrameworkElement Labeled(string label, FrameworkElement control)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
        var cap = Ui.Caption(label);
        cap.Margin = new Thickness(1, 0, 0, 3);
        sp.Children.Add(cap);
        sp.Children.Add(control);
        return sp;
    }

    private static TextBlock RightCaption(string text, string? tip = null)
    {
        var t = Ui.Caption(text);
        t.HorizontalAlignment = HorizontalAlignment.Right;
        t.ToolTip = tip;
        return t;
    }

    private FrameworkElement BuildRight()
    {
        var dock = new DockPanel { LastChildFill = true };
        var bar = new DockPanel { Margin = new Thickness(18, 14, 18, 0) };
        DockPanel.SetDock(_deleteButton, Dock.Right);
        bar.Children.Add(_deleteButton);
        bar.Children.Add(_detailTitle);
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        DockPanel.SetDock(_confirmBar, Dock.Top);
        dock.Children.Add(_confirmBar);
        dock.Children.Add(_detailHost);
        return dock;
    }

    private FrameworkElement BuildConfirmBar()
    {
        var grid = new Grid { Margin = new Thickness(18, 10, 18, 0) };
        var bg = new Border { CornerRadius = new CornerRadius(5), Opacity = 0.14 };
        bg.SetResourceReference(Border.BackgroundProperty, ThemeKeys.Negative);
        var edge = new Border { CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1) };
        edge.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.Negative);
        var dp = new DockPanel { Margin = new Thickness(12, 7, 8, 7) };
        var cancel = Ui.Button("Cancel", () => _confirmBar.Visibility = Visibility.Collapsed);
        var confirm = Ui.Button("Delete fight", () => _ = ConfirmDeleteAsync(), null, "A2.DangerButton");
        DockPanel.SetDock(cancel, Dock.Right);
        DockPanel.SetDock(confirm, Dock.Right);
        dp.Children.Add(cancel);
        dp.Children.Add(confirm);
        dp.Children.Add(Ui.Text("Delete this fight from the history? This cannot be undone.", 0, ThemeKeys.Text, FontWeights.SemiBold));
        grid.Children.Add(bg);
        grid.Children.Add(edge);
        grid.Children.Add(dp);
        return grid;
    }

    private void ShowPlaceholder(string text)
    {
        _detailTitle.Text = "";
        var t = Ui.Text(text, 13, ThemeKeys.TextMuted, wrap: true);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.VerticalAlignment = VerticalAlignment.Center;
        t.TextAlignment = TextAlignment.Center;
        _detailHost.Content = t;
    }

    // ───────────── Filters ─────────────

    private void OnFilterBox(ComboBox box, Action<object?> apply)
    {
        if (_suppress || box.SelectedItem is not ComboBoxItem item) return;
        apply(item.Tag);
        _ = RefreshAsync();
    }

    private void ApplyOptions(IReadOnlyList<string> characters, IReadOnlyList<FightSummary> options)
    {
        var gd = _gameData!;
        _suppress = true;
        try
        {
            Fill(_characterBox, new[] { ((object?)null, "All characters") }.Concat(characters.OrderBy(c => c).Select(c => ((object?)c, c))), _character);

            var maps = options.Where(o => o.MapId is not null).Select(o => o.MapId!.Value).Distinct()
                .Select(m => ((object?)m, MapName(m))).OrderBy(x => x.Item2).ToList();
            if (_mapId is { } sm && maps.All(x => (uint)x.Item1! != sm)) maps.Add((sm, MapName(sm)));
            Fill(_mapBox, new[] { ((object?)null, "All instances") }.Concat(maps), _mapId);

            var bosses = options.Where(o => o.BossNpcCode is not null && (_mapId is null || o.MapId == _mapId))
                .Select(o => o.BossNpcCode!.Value).Distinct().Select(b => ((object?)b, gd.GetNpcName(b))).OrderBy(x => x.Item2).ToList();
            if (_bossCode is { } sb && bosses.All(x => (uint)x.Item1! != sb)) bosses.Add((sb, gd.GetNpcName(sb)));
            Fill(_bossBox, new[] { ((object?)null, "All bosses") }.Concat(bosses), _bossCode);
        }
        finally
        {
            _suppress = false;
        }
    }

    private static void Fill(ComboBox box, IEnumerable<(object? Value, string Label)> items, object? selected)
    {
        box.Items.Clear();
        foreach (var (value, label) in items)
        {
            var item = new ComboBoxItem { Content = label, Tag = value };
            box.Items.Add(item);
            if (Equals(value, selected)) box.SelectedItem = item;
        }
        if (box.SelectedItem is null && box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private string MapName(uint mapId) => ChartData.MapLabel(_gameData!, mapId);

    // ───────────── List ─────────────

    private void RebuildTiles()
    {
        int kills = _fights.Count(f => f.Outcome == EncounterOutcome.Kill);
        double peak = _fights.Count == 0 ? 0 : _fights.Max(f => f.PartyDps);
        double best = _fights.Count == 0 ? 0 : _fights.Max(f => f.LocalDps);
        double time = _fights.Sum(f => f.DurationSeconds);
        _tilesHost.Content = Ui.TileGrid(4,
        [
            Ui.Tile("Fights", _fights.Count.ToString(), $"{_fights.Select(f => f.MapId).Distinct().Count()} instances", valueSize: 17),
            Ui.Tile("Kills", kills.ToString(), _fights.Count == 0 ? null : Fmt.Percent((double)kills / _fights.Count, 0) + " of fights", valueFg: ThemeKeys.Positive, valueSize: 17),
            Ui.Tile("Peak DPS", _fights.Count == 0 ? Fmt.Dash : Fmt.Dps(peak), best > 0 ? "your best " + Fmt.Dps(best) : "party", tip: Fmt.Exact(peak), valueFg: ThemeKeys.Accent, valueSize: 17),
            Ui.Tile("Time in fights", Fmt.Duration(time), null, valueSize: 17),
        ]);
    }

    private void RebuildList()
    {
        _list.Children.Clear();
        _rowSelection.Clear();
        if (_fights.Count == 0)
        {
            _list.Children.Add(Ui.Empty("No saved fights match these filters."));
            return;
        }
        var gd = _gameData!;
        var now = Clock();
        foreach (var mapGroup in _fights.GroupBy(f => f.MapId).OrderByDescending(g => g.Max(f => f.StartUtc)))
        {
            string mapKey = "m" + mapGroup.Key;
            string mapName = mapGroup.Key is { } m ? MapName(m) : "Open world";
            var fights = mapGroup.ToList();
            _list.Children.Add(GroupHeader(mapKey, mapName, fights, top: true));
            if (_collapsed.Contains(mapKey)) continue;
            foreach (var bossGroup in fights.GroupBy(f => (f.BossNpcCode, f.BossNpcCode is null ? f.Kind : EncounterKind.Boss))
                         .OrderByDescending(g => g.Max(f => f.StartUtc)))
            {
                string bossKey = mapKey + "b" + bossGroup.Key.BossNpcCode + bossGroup.Key.Item2;
                string bossName = bossGroup.Key.BossNpcCode is { } b ? gd.GetNpcName(b) : bossGroup.Key.Item2 switch
                {
                    EncounterKind.Pvp => "PvP sessions",
                    EncounterKind.Training => "Training runs",
                    EncounterKind.Dummy => "Training dummy",
                    _ => "Trash packs",
                };
                var bf = bossGroup.ToList();
                _list.Children.Add(GroupHeader(bossKey, bossName, bf, top: false));
                if (_collapsed.Contains(bossKey)) continue;
                int i = 0;
                foreach (var f in bf.OrderByDescending(f => f.StartUtc)) _list.Children.Add(FightRow(f, now, i++));
            }
        }
    }

    private static int CountRuns(IEnumerable<FightSummary> fights)
    {
        int runs = 0;
        DateTime lastEnd = DateTime.MinValue;
        foreach (var f in fights.OrderBy(f => f.StartUtc))
        {
            if (f.StartUtc - lastEnd > TimeSpan.FromMinutes(30)) runs++;
            var end = f.StartUtc.AddSeconds(f.DurationSeconds);
            if (end > lastEnd) lastEnd = end;
        }
        return runs;
    }

    private FrameworkElement GroupHeader(string key, string name, List<FightSummary> fights, bool top)
    {
        bool collapsed = _collapsed.Contains(key);
        int kills = fights.Count(f => f.Outcome == EncounterOutcome.Kill);
        double peak = fights.Max(f => f.PartyDps);
        var killTimes = fights.Where(f => f.Outcome == EncounterOutcome.Kill).Select(f => f.DurationSeconds).ToList();
        var parts = new List<(string, string)>
        {
            ("", top ? $"{Fmt.Count(CountRuns(fights), "run")} · {Fmt.Count(fights.Count, "fight")}"
                     : $"{Fmt.Count(fights.Count, "fight")} · {Fmt.Count(kills, "kill")}"),
            ("peak", Fmt.Dps(peak)),
            ("fastest", killTimes.Count > 0 ? Fmt.Duration(killTimes.Min()) : ""),
        };
        var dp = new DockPanel { Margin = new Thickness(top ? 0 : 14, top ? 10 : 6, 0, top ? 2 : 3) };
        var chevron = Ui.Text(collapsed ? "▸" : "▾", top ? 13 : 11.5, ThemeKeys.TextMuted);
        chevron.Width = 14;
        chevron.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(chevron, Dock.Left);
        dp.Children.Add(chevron);
        var facts = Ui.Facts(parts.ToArray());
        facts.FontSize = 11;
        if (top)
        {
            // Instance names are long: name + "last played" on the first line, the stats on the second.
            var lines = new StackPanel();
            var first = new DockPanel();
            var last = Ui.Text("last " + Fmt.TimeAgo(fights.Max(f => f.StartUtc), Clock()), 11, ThemeKeys.TextMuted);
            DockPanel.SetDock(last, Dock.Right);
            first.Children.Add(last);
            first.Children.Add(Ui.Text(name, 14, ThemeKeys.Text, FontWeights.SemiBold));
            lines.Children.Add(first);
            facts.Margin = new Thickness(0, 2, 0, 0);
            lines.Children.Add(facts);
            dp.Children.Add(lines);
        }
        else
        {
            facts.Margin = new Thickness(10, 0, 0, 0);
            DockPanel.SetDock(facts, Dock.Right);
            dp.Children.Add(facts);
            dp.Children.Add(Ui.Text(name, 12.5, ThemeKeys.Accent, FontWeights.SemiBold));
        }

        var border = new Border
        {
            Child = dp,
            Padding = new Thickness(6, top ? 6 : 3, 6, top ? 6 : 3),
            Cursor = Cursors.Hand,
            ToolTip = collapsed ? "Expand" : "Collapse",
            BorderThickness = new Thickness(0, 0, 0, top ? 1 : 0),
        };
        border.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.Border);
        border.Background = System.Windows.Media.Brushes.Transparent;
        border.MouseLeftButtonUp += (_, e) =>
        {
            if (!_collapsed.Remove(key)) _collapsed.Add(key);
            RebuildList();
            e.Handled = true;
        };
        return border;
    }

    private static Grid FightRowGrid()
    {
        var g = new Grid();
        foreach (var w in new[] { 70.0, -1, 58, 58, 58, 46, 26 })
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w > 0 ? new GridLength(w) : new GridLength(1, GridUnitType.Star) });
        return g;
    }

    private static void AddCell(Grid g, int col, UIElement el)
    {
        if (el is FrameworkElement fe) fe.Margin = new Thickness(4, 0, 4, 0);
        Grid.SetColumn(el, col);
        g.Children.Add(el);
    }

    private FrameworkElement FightRow(FightSummary f, DateTime now, int index)
    {
        var root = new Grid { MinHeight = 30, Margin = new Thickness(14, 0, 0, 0), Background = System.Windows.Media.Brushes.Transparent, Cursor = Cursors.Hand };
        var stripe = new Rectangle { RadiusX = 3, RadiusY = 3, Opacity = index % 2 == 1 ? 0.5 : 0 };
        stripe.SetResourceReference(Shape.FillProperty, ThemeKeys.SurfaceAlt);
        var sel = new Rectangle { RadiusX = 3, RadiusY = 3, Opacity = f.Id == _selected ? 0.22 : 0 };
        sel.SetResourceReference(Shape.FillProperty, ThemeKeys.Accent);
        _rowSelection[f.Id] = sel;
        root.Children.Add(stripe);
        root.Children.Add(sel);

        var g = FightRowGrid();
        var badge = Ui.Badge(Fmt.OutcomeLabel(f.Outcome), Fmt.OutcomeBrushKey(f.Outcome));
        AddCell(g, 0, badge);
        var when = Ui.Text(Fmt.TimeAgo(f.StartUtc, now), 0, ThemeKeys.Text);
        when.ToolTip = Fmt.LocalDateTime(f.StartUtc);
        AddCell(g, 1, when);
        AddCell(g, 2, Ui.Number(Fmt.Number(f.TotalDamage), Fmt.Exact(f.TotalDamage)));
        AddCell(g, 3, Ui.Number(Fmt.Dps(f.PartyDps), Fmt.Exact(f.PartyDps), ThemeKeys.Text, FontWeights.SemiBold));
        AddCell(g, 4, Ui.Number(f.LocalDps > 0 ? Fmt.Dps(f.LocalDps) : Fmt.Dash, f.LocalPlayerName is { } n ? $"{n}: {Fmt.Exact(f.LocalDps)}" : null, ThemeKeys.Accent));
        AddCell(g, 5, Ui.Number(Fmt.Duration(f.DurationSeconds)));
        AddCell(g, 6, Ui.Number(f.PlayerCount.ToString(), null, ThemeKeys.TextMuted));
        root.Children.Add(g);

        string tip = $"{Fmt.LocalDateTime(f.StartUtc)} · {Fmt.KindLabel(f.Kind)} · {Fmt.OutcomeLabel(f.Outcome)}";
        if (f.LocalPlayerName is { } lp) tip += $"\n{lp} ({ClassInfo.ShortName(f.LocalPlayerClass)}): {Fmt.Number(f.LocalDamage)} dmg, {Fmt.Dps(f.LocalDps)} DPS";
        if (f.HpCheckRatio is { } hr) tip += $"\nHP check {Fmt.Percent(hr)}";
        if (!string.IsNullOrWhiteSpace(f.Note)) tip += "\n" + f.Note;
        root.ToolTip = tip;
        root.MouseEnter += (_, _) => stripe.Opacity = 1;
        root.MouseLeave += (_, _) => stripe.Opacity = index % 2 == 1 ? 0.5 : 0;
        root.MouseLeftButtonUp += (_, e) =>
        {
            _ = SelectFightAsync(f.Id);
            e.Handled = true;
        };
        return root;
    }
}
