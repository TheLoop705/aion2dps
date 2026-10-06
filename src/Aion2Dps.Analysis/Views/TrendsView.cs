using System.Windows;
using System.Windows.Controls;
using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis;

/// <summary>
/// Personal per-boss trends: pick a character, see one row per boss (fights, kills, best, median, last DPS, fastest
/// kill) and the DPS-per-fight chart of the selected boss with best/median guides and the personal best.
/// Every store call runs off the UI thread.
/// </summary>
public sealed class TrendsView : UserControl
{
    private IFightStore? _store;
    private IGameData? _gameData;
    private string? _character;
    private uint? _boss;
    private readonly ComboBox _characterBox = new() { Width = 220, ToolTip = "Character" };
    private readonly TextBlock _status = Ui.Text("", 11.5, ThemeKeys.TextMuted);
    private readonly ContentControl _tableHost = new() { Focusable = false };
    private readonly ContentControl _detailHost = new() { Focusable = false };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    private GridTable<BossTrendSummary>? _table;
    private bool _suppress;
    private int _version, _bossVersion;

    public TrendsView()
    {
        Resources.MergedDictionaries.Add(new AnalysisStyles());
        Ui.ApplyRootTheme(this);
        var content = new StackPanel { Margin = new Thickness(18, 16, 18, 6), MinWidth = 640 };

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(_characterBox);
        right.Children.Add(Ui.Button("Refresh", () => _ = RefreshAsync(), "Reload"));
        DockPanel.SetDock(right, Dock.Right);
        head.Children.Add(right);
        var titles = new StackPanel();
        titles.Children.Add(Ui.Text("Personal trends", 19, ThemeKeys.Text, FontWeights.SemiBold));
        titles.Children.Add(_status);
        head.Children.Add(titles);
        content.Children.Add(head);
        content.Children.Add(_tableHost);
        content.Children.Add(_detailHost);
        _scroll.Content = content;
        Content = _scroll;

        _characterBox.SelectionChanged += (_, _) =>
        {
            if (_suppress || _characterBox.SelectedItem is not ComboBoxItem { Tag: string name }) return;
            _ = SelectCharacterAsync(name);
        };
    }

    public IReadOnlyList<BossTrendSummary> Summaries => _table?.Items ?? [];
    public string? Character => _character;
    public uint? SelectedBoss => _boss;

    /// <summary>The trend chart of the selected boss (null until one is selected).</summary>
    public TrendChart? Chart { get; private set; }

    /// <summary>The latest refresh/selection task.</summary>
    public Task? PendingWork { get; private set; }

    public void Initialize(IFightStore store, IGameData gameData)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
        PendingWork = RefreshAsync();
    }

    /// <summary>Reloads the character list and the summaries of the current (or first) character.</summary>
    public async Task RefreshAsync()
    {
        if (_store is null || _gameData is null) return;
        var store = _store;
        _status.Text = "Loading…";
        IReadOnlyList<string> characters;
        try
        {
            characters = await Task.Run(store.GetCharacters);
        }
        catch (Exception ex)
        {
            AppLog.Error("Analysis", "Trends: loading characters failed", ex);
            _status.Text = "Could not load characters: " + ex.Message;
            return;
        }
        _suppress = true;
        _characterBox.Items.Clear();
        foreach (var c in characters.OrderBy(c => c)) _characterBox.Items.Add(new ComboBoxItem { Content = c, Tag = c });
        string? pick = _character is not null && characters.Contains(_character) ? _character : characters.OrderBy(c => c).FirstOrDefault();
        _characterBox.SelectedItem = _characterBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == pick);
        _suppress = false;
        if (pick is null)
        {
            _status.Text = "No saved fights yet.";
            _tableHost.Content = Ui.Card(Ui.Empty("Trends appear after your first saved boss fight."), "Bosses");
            _detailHost.Content = null;
            return;
        }
        await SelectCharacterAsync(pick);
    }

    public Task SelectCharacterAsync(string characterName)
    {
        var t = SelectCharacterCoreAsync(characterName);
        PendingWork = t;
        return t;
    }

    private async Task SelectCharacterCoreAsync(string characterName)
    {
        if (_store is null || _gameData is null) return;
        int version = ++_version;
        _character = characterName;
        var store = _store;
        _status.Text = "Loading…";
        IReadOnlyList<BossTrendSummary> rows;
        try
        {
            rows = await Task.Run(() => store.GetBossSummaries(characterName));
        }
        catch (Exception ex)
        {
            AppLog.Error("Analysis", "Trends: loading summaries failed", ex);
            _status.Text = "Could not load trends: " + ex.Message;
            return;
        }
        if (version != _version) return;
        _status.Text = $"{characterName} · {rows.Count} bosses · {rows.Sum(r => r.Fights)} fights";
        BuildTable(rows);
        var next = rows.FirstOrDefault(r => r.BossNpcCode == _boss) ?? rows.OrderByDescending(r => r.LastFoughtUtc).FirstOrDefault();
        if (next is not null) await SelectBossCoreAsync(next.BossNpcCode);
        else _detailHost.Content = null;
    }

    public Task SelectBossAsync(uint bossNpcCode)
    {
        var t = SelectBossCoreAsync(bossNpcCode);
        PendingWork = t;
        return t;
    }

    private async Task SelectBossCoreAsync(uint bossNpcCode)
    {
        if (_store is null || _gameData is null || _character is null) return;
        int version = ++_bossVersion;
        _boss = bossNpcCode;
        if (_table is not null) _table.Selected = _table.Items.FirstOrDefault(r => r.BossNpcCode == bossNpcCode);
        var store = _store;
        string character = _character;
        IReadOnlyList<TrendPoint> points;
        TrendPoint? best;
        try
        {
            (points, best) = await Task.Run(() => (store.GetBossTrend(bossNpcCode, character), store.GetPersonalBest(bossNpcCode, character)));
        }
        catch (Exception ex)
        {
            AppLog.Error("Analysis", "Trends: loading boss trend failed", ex);
            _detailHost.Content = Ui.Card(Ui.Empty("Could not load this trend: " + ex.Message), _gameData.GetNpcName(bossNpcCode));
            return;
        }
        if (version != _bossVersion) return;
        BuildDetail(bossNpcCode, points, best);
    }

    private void BuildTable(IReadOnlyList<BossTrendSummary> rows)
    {
        var gd = _gameData!;
        double top = rows.Count == 0 ? 1 : Math.Max(1, rows.Max(r => r.BestDps));
        var cols = new List<TableColumn<BossTrendSummary>>
        {
            new("Boss", -1, r => BossCell(r, gd), r => gd.GetNpcName(r.BossNpcCode), HorizontalAlignment.Left),
            new("Fights", 54, r => Ui.Number(r.Fights.ToString()), r => r.Fights),
            new("Kills", 50, r => Ui.Number(r.Kills.ToString(), null, r.Kills > 0 ? ThemeKeys.Positive : ThemeKeys.TextMuted), r => r.Kills),
            new("Best", 150, r => Ui.BarCell(Fmt.Dps(r.BestDps), r.BestDps / top, Fmt.Exact(r.BestDps), fillKey: ThemeKeys.Accent), r => r.BestDps),
            new("Median", 66, r => Ui.Number(Fmt.Dps(r.MedianDps), Fmt.Exact(r.MedianDps)), r => r.MedianDps),
            new("Last", 66, r => Ui.Number(Fmt.Dps(r.LastDps), Fmt.Exact(r.LastDps), r.LastDps >= r.MedianDps ? ThemeKeys.Positive : ThemeKeys.Warning), r => r.LastDps),
            new("Fastest", 62, r => Ui.Number(r.FastestKillSeconds > 0 ? Fmt.Duration(r.FastestKillSeconds) : Fmt.Dash), r => r.FastestKillSeconds > 0 ? r.FastestKillSeconds : null),
            new("Last fought", 108, r => Ui.Number(Fmt.TimeAgo(r.LastFoughtUtc, DateTime.UtcNow), Fmt.LocalDateTime(r.LastFoughtUtc), ThemeKeys.TextMuted), r => r.LastFoughtUtc),
        };
        _table = new GridTable<BossTrendSummary>(cols, 7) { Selectable = true, RowHeight = 34 };
        _table.RowClicked += r => _ = SelectBossAsync(r.BossNpcCode);
        _table.SetItems(rows);
        _tableHost.Content = Ui.Card(_table, "Bosses", subtitle: "click a boss for its DPS trend");
    }

    private static FrameworkElement BossCell(BossTrendSummary r, IGameData gd)
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(Ui.Text(gd.GetNpcName(r.BossNpcCode), 0, ThemeKeys.Text, FontWeights.SemiBold));
        if (r.MapId is { } m && gd.GetMapName(m) is { } map) sp.Children.Add(Ui.Text(map, 10.5, ThemeKeys.TextMuted));
        return sp;
    }

    private void BuildDetail(uint boss, IReadOnlyList<TrendPoint> points, TrendPoint? best)
    {
        var gd = _gameData!;
        var sp = new StackPanel();
        double median = SeriesMath.Median(points.Select(p => p.Dps));
        var last = points.OrderBy(p => p.StartUtc).LastOrDefault();
        var kills = points.Where(p => p.Outcome == EncounterOutcome.Kill).ToList();
        sp.Children.Add(Ui.TileGrid(5,
        [
            Ui.Tile("Personal best", best is null ? Fmt.Dash : Fmt.Dps(best.Dps), best is null ? "no kill yet" : Fmt.ShortDate(best.StartUtc) + " · " + Fmt.Duration(best.DurationSeconds),
                best is null ? null : Fmt.Exact(best.Dps), ThemeKeys.Crit),
            Ui.Tile("Median", double.IsNaN(median) ? Fmt.Dash : Fmt.Dps(median), $"of {points.Count} fights"),
            Ui.Tile("Last", last is null ? Fmt.Dash : Fmt.Dps(last.Dps), last is null ? null : Fmt.TimeAgo(last.StartUtc, DateTime.UtcNow),
                null, last is not null && !double.IsNaN(median) && last.Dps >= median ? ThemeKeys.Positive : ThemeKeys.Text),
            Ui.Tile("Kills", $"{kills.Count}/{points.Count}", points.Count == 0 ? null : Fmt.Percent((double)kills.Count / points.Count, 0) + " kill rate"),
            Ui.Tile("Fastest kill", kills.Count == 0 ? Fmt.Dash : Fmt.Duration(kills.Min(k => k.DurationSeconds))),
        ]));
        Chart = new TrendChart { Height = 270 };
        Chart.SetData(points, best);
        sp.Children.Add(Chart);
        var legend = Ui.Text("Filled dots are kills, hollow dots are wipes or resets. PB marks your best kill.", 11, ThemeKeys.TextMuted, wrap: true);
        legend.Margin = new Thickness(2, 8, 0, 0);
        sp.Children.Add(legend);
        _detailHost.Content = Ui.Card(sp, gd.GetNpcName(boss), subtitle: "DPS per fight, oldest left");
    }
}
