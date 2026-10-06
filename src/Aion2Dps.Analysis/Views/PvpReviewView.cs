using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis;

/// <summary>
/// Review of a saved PvP session: the opponents you traded blows with (damage dealt vs taken as a two-way bar, kills)
/// and a drill-down per opponent (exchange timeline and the skills used on both sides).
/// </summary>
public sealed class PvpReviewView : UserControl
{
    private readonly ScrollViewer _scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        Focusable = false,
    };
    private readonly StackPanel _content = new() { Margin = new Thickness(18, 16, 18, 6), MinWidth = 640 };
    private readonly ContentControl _detailHost = new() { Focusable = false };
    private EncounterRecord? _record;
    private IGameData? _gameData;
    private GridTable<CombatantRecord>? _table;

    public PvpReviewView()
    {
        Resources.MergedDictionaries.Add(new AnalysisStyles());
        Ui.ApplyRootTheme(this);
        _scroll.Content = _content;
        Ui.FitWidth(_scroll, _content);
        Content = _scroll;
        _content.Children.Add(Ui.Empty("No PvP session loaded"));
        Ui.RebuildOnLanguageChange(this, () => _gameData, () => { if (_record is not null) { var sel = SelectedOpponentId; Build(); if (sel is { } s) SelectOpponent(s); } });
    }

    public PvpReviewView(EncounterRecord record, IGameData gameData) : this() => Load(record, gameData);

    /// <summary>Raised when an opponent is selected for the drill-down.</summary>
    public event Action<uint>? OpponentSelected;

    /// <summary>Raised when "Breakdown" is pressed for a combatant; opens a <see cref="BreakdownWindow"/> unless <see cref="OpenBreakdownWindows"/> is false.</summary>
    public event Action<uint>? PlayerOpened;

    public bool OpenBreakdownWindows { get; set; } = true;

    public uint? SelectedOpponentId { get; private set; }

    public void Load(EncounterRecord record, IGameData gameData)
    {
        _record = record ?? throw new ArgumentNullException(nameof(record));
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
        Build();
    }

    public BitmapSource RenderToBitmap(double dpi = 96)
    {
        var bg = TryFindResource(ThemeKeys.WindowBackground) as Brush ?? Brushes.Black;
        return ImageExport.RenderStacked(bg, dpi, _content);
    }

    /// <summary>Shows the drill-down for an opponent.</summary>
    public void SelectOpponent(uint entityId)
    {
        if (_record is null || ChartData.Find(_record, entityId) is not { } opp) return;
        SelectedOpponentId = entityId;
        if (_table is not null) _table.Selected = _table.Items.FirstOrDefault(c => c.EntityId == entityId);
        _detailHost.Content = BuildDetail(opp);
        OpponentSelected?.Invoke(entityId);
    }

    private void OpenPlayer(uint id)
    {
        PlayerOpened?.Invoke(id);
        if (!OpenBreakdownWindows || _record is null || _gameData is null) return;
        new BreakdownWindow(_record, id, _gameData) { Owner = Window.GetWindow(this) }.Show();
    }

    private void Build()
    {
        _content.Children.Clear();
        var r = _record!;
        var gd = _gameData!;
        var local = ChartData.Local(r);
        var enemies = ChartData.Enemies(r).ToList();

        var title = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        title.Children.Add(Ui.Caption("PvP session", ThemeKeys.Negative));
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        if (local is not null)
        {
            var btn = Ui.Button("Your breakdown", () => OpenPlayer(local.EntityId));
            DockPanel.SetDock(btn, Dock.Right);
            row.Children.Add(btn);
        }
        string place = r.MapId is { } m ? ChartData.MapLabel(gd, m) : "Open world";
        row.Children.Add(Ui.Text(place, 24, ThemeKeys.Text, FontWeights.SemiBold));
        title.Children.Add(row);
        var facts = Ui.Facts(("", local is null ? "" : ChartData.DisplayName(local)), ("", Fmt.LocalDateTime(r.StartUtc)),
            ("lasted", Fmt.Duration(r.DurationSeconds)), ("", string.IsNullOrWhiteSpace(r.Note) ? "" : r.Note!));
        facts.Margin = new Thickness(0, 4, 0, 0);
        title.Children.Add(facts);
        _content.Children.Add(title);
        if (r.CaptureGaps)
            _content.Children.Add(Ui.Banner("The capture had gaps during this session, so some numbers may be incomplete."));

        long dealt = enemies.Sum(e => e.DamageFromLocal), taken = enemies.Sum(e => e.DamageToLocal);
        int kills = enemies.Count(e => e.KilledByLocal);
        _content.Children.Add(Ui.TileGrid(6,
        [
            Ui.Tile("Opponents", enemies.Count.ToString()),
            Ui.Tile("Kills", kills.ToString(), null, null, kills > 0 ? ThemeKeys.Positive : ThemeKeys.Text),
            Ui.Tile("Dealt", Fmt.Number(dealt), null, Fmt.Exact(dealt), ThemeKeys.Positive),
            Ui.Tile("Taken", Fmt.Number(taken), null, Fmt.Exact(taken), ThemeKeys.Negative),
            Ui.Tile("Trade", taken > 0 ? Fmt.Ratio(dealt / (double)taken) : Fmt.Dash, "dealt per taken"),
            Ui.Tile("Deaths", (local?.Deaths ?? 0).ToString(), null, null, (local?.Deaths ?? 0) > 0 ? ThemeKeys.Negative : ThemeKeys.Text),
        ]));

        double max = enemies.Count == 0 ? 1 : Math.Max(1, enemies.Max(e => Math.Max(e.DamageFromLocal, e.DamageToLocal)));
        var cols = new List<TableColumn<CombatantRecord>>
        {
            new("Opponent", -1, e => OpponentCell(e, gd), e => ChartData.DisplayName(e), HorizontalAlignment.Left),
            new("Dealt ↑", 76, e => Ui.Number(Fmt.Number(e.DamageFromLocal), Fmt.Exact(e.DamageFromLocal) + " dealt to them", ThemeKeys.Positive, FontWeights.SemiBold), e => e.DamageFromLocal),
            new("Taken ↓ · dealt ↑", 210, e => TradeBar(e, max), e => e.DamageFromLocal - e.DamageToLocal, HorizontalAlignment.Center),
            new("Taken ↓", 76, e => Ui.Number(Fmt.Number(e.DamageToLocal), Fmt.Exact(e.DamageToLocal) + " taken from them", ThemeKeys.Negative, FontWeights.SemiBold), e => e.DamageToLocal),
            new("Their DPS", 72, e => Ui.Number(Fmt.Dps(e.Dps), Fmt.Exact(e.Dps)), e => e.Dps),
        };
        _table = new GridTable<CombatantRecord>(cols, 1) { Selectable = true, RowHeight = 34, RowMarker = e => e.KilledByLocal ? ThemeKeys.Positive : null };
        _table.RowClicked += e => SelectOpponent(e.EntityId);
        _table.RowDoubleClicked += e => OpenPlayer(e.EntityId);
        _table.SetItems(enemies);
        _content.Children.Add(Ui.Card(_table, "Opponents", subtitle: "click for the exchange · double-click for their breakdown"));
        _content.Children.Add(_detailHost);
        if (enemies.Count > 0) SelectOpponent(enemies[0].EntityId);
        else _detailHost.Content = null;
    }

    private static FrameworkElement OpponentCell(CombatantRecord e, IGameData gd)
    {
        var dp = new DockPanel();
        var em = Ui.Emblem(e.Class, 24);
        em.Margin = new Thickness(0, 0, 9, 0);
        DockPanel.SetDock(em, Dock.Left);
        dp.Children.Add(em);
        if (e.KilledByLocal)
        {
            var b = Ui.Badge("KILLED", ThemeKeys.Positive, solid: true);
            b.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(b, Dock.Right);
            dp.Children.Add(b);
        }
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(Ui.Text(ChartData.DisplayName(e), 0, ThemeKeys.Text, FontWeights.SemiBold));
        string server = e.ServerId is { } s ? gd.GetServerName(s) ?? "" : "";
        names.Children.Add(Ui.Text(string.Join(" · ", new[] { gd.GetClassName(e.Class), server }.Where(x => x.Length > 0)), 10.5, ThemeKeys.TextMuted));
        dp.Children.Add(names);
        return dp;
    }

    private static FrameworkElement TradeBar(CombatantRecord e, double max)
    {
        var bar = new TwoWayBar { Height = 12, ToolTip = $"Dealt {Fmt.Exact(e.DamageFromLocal)} · taken {Fmt.Exact(e.DamageToLocal)}" };
        bar.SetValues(e.DamageFromLocal, e.DamageToLocal, max);
        bar.IsHitTestVisible = true;
        return bar;
    }

    private sealed record SkillAgg(uint Skill, int Hits, int Crits, long Damage, long MaxHit);

    private FrameworkElement BuildDetail(CombatantRecord opp)
    {
        var r = _record!;
        var gd = _gameData!;
        var local = ChartData.Local(r);
        uint localId = local?.EntityId ?? 0;
        var dealtHits = r.Hits.Where(h => h.Actor == localId && h.Target == opp.EntityId && (h.Flags & (HitFlags.Heal | HitFlags.Incoming)) == 0).ToList();
        var takenAll = r.Hits.Where(h => h.Actor == opp.EntityId && h.Target == localId && (h.Flags & HitFlags.Heal) == 0).ToList();
        var takenHits = takenAll.Any(h => (h.Flags & HitFlags.Incoming) != 0) ? takenAll.Where(h => (h.Flags & HitFlags.Incoming) != 0).ToList() : takenAll;

        List<SkillAgg> Agg(IEnumerable<HitRecord> hits) => hits
            .GroupBy(h => gd.GetSkillGroupKey(h.Skill))
            .Select(g => new SkillAgg(g.Key, g.Count(h => (h.Flags & HitFlags.Dot) == 0), g.Count(h => (h.Flags & HitFlags.Crit) != 0), g.Sum(h => h.Amount), g.Max(h => h.Amount)))
            .OrderByDescending(a => a.Damage).ToList();
        var mine = Agg(dealtHits);
        var theirs = Agg(takenHits);

        var sp = new StackPanel();
        int myDirect = dealtHits.Count(h => (h.Flags & HitFlags.Dot) == 0), theirDirect = takenHits.Count(h => (h.Flags & HitFlags.Dot) == 0);
        sp.Children.Add(Ui.TileGrid(6,
        [
            Ui.Tile("Dealt", Fmt.Number(opp.DamageFromLocal), null, Fmt.Exact(opp.DamageFromLocal), ThemeKeys.Positive),
            Ui.Tile("Taken", Fmt.Number(opp.DamageToLocal), null, Fmt.Exact(opp.DamageToLocal), ThemeKeys.Negative),
            Ui.Tile("Your hits", myDirect.ToString(), "crit " + Fmt.Rate(dealtHits.Count(h => (h.Flags & HitFlags.Crit) != 0), myDirect)),
            Ui.Tile("Their hits", theirDirect.ToString(), "crit " + Fmt.Rate(takenHits.Count(h => (h.Flags & HitFlags.Crit) != 0), theirDirect)),
            Ui.Tile("Your biggest", mine.Count == 0 ? Fmt.Dash : Fmt.Number(mine.Max(a => a.MaxHit)), null, null, ThemeKeys.Crit),
            Ui.Tile("Outcome", opp.KilledByLocal ? "Killed" : "Escaped", null, null, opp.KilledByLocal ? ThemeKeys.Positive : ThemeKeys.TextMuted),
        ]));

        int n = ChartData.SecondsLength(r);
        var chart = new DpsTimelineChart { Height = 190, ValueLabel = "damage/s", EmptyText = "No damage exchanged" };
        var series = new List<TimelineSeries>
        {
            new(localId, "You → " + ChartData.DisplayName(opp), local?.Class ?? CharacterClass.Unknown,
                SeriesMath.RollingDps(SeriesMath.PerSecond(dealtHits, _ => true, n), 5, n)),
            new(opp.EntityId, ChartData.DisplayName(opp) + " → you", opp.Class,
                SeriesMath.RollingDps(SeriesMath.PerSecond(takenHits, _ => true, n), 5, n)),
        };
        chart.SetSeries(series);
        var chartTitle = Ui.Text("Exchange over time · rolling 5 s", 11.5, ThemeKeys.TextMuted);
        chartTitle.Margin = new Thickness(0, 6, 0, 6);
        sp.Children.Add(chartTitle);
        sp.Children.Add(chart);

        var tables = new UniformGrid { Columns = 2, Margin = new Thickness(0, 14, 0, 0) };
        tables.Children.Add(SkillTable("Your skills on them", mine, local?.Class ?? CharacterClass.Unknown, gd, ThemeKeys.Positive, new Thickness(0, 0, 8, 0)));
        tables.Children.Add(SkillTable("Their skills on you", theirs, opp.Class, gd, ThemeKeys.Negative, new Thickness(8, 0, 0, 0)));
        sp.Children.Add(tables);

        var open = Ui.Button("Their breakdown", () => OpenPlayer(opp.EntityId));
        return Ui.Card(sp, "Exchange with " + ChartData.DisplayName(opp), open, opp.KilledByLocal ? "you killed them" : null);
    }

    private static FrameworkElement SkillTable(string title, List<SkillAgg> rows, CharacterClass owner, IGameData gd, string fillKey, Thickness margin)
    {
        long max = rows.Count == 0 ? 1 : Math.Max(1, rows.Max(a => a.Damage));
        var cols = new List<TableColumn<SkillAgg>>
        {
            new("Skill", -1, a => Ui.SkillCell(gd.GetSkillName(a.Skill), a.Skill, SkillVisuals.TileClass(gd, a.Skill, owner), tileSize: 18), a => gd.GetSkillName(a.Skill), HorizontalAlignment.Left),
            new("Hits", 42, a => Ui.Number(a.Hits.ToString()), a => a.Hits),
            new("Crit", 46, a => Ui.Number(Fmt.Rate(a.Crits, a.Hits, 0)), a => Fmt.RateValue(a.Crits, a.Hits)),
            new("Damage", 128, a => Ui.BarCell(Fmt.Number(a.Damage), (double)a.Damage / max, Fmt.Exact(a.Damage), fillKey: fillKey), a => a.Damage),
        };
        var t = new GridTable<SkillAgg>(cols, 3) { RowHeight = 26 };
        t.SetItems(rows);
        var sp = new StackPanel { Margin = margin };
        var head = Ui.Text(title, 12.5, ThemeKeys.Text, FontWeights.SemiBold);
        head.Margin = new Thickness(0, 0, 0, 6);
        sp.Children.Add(head);
        sp.Children.Add(t);
        return sp;
    }
}
