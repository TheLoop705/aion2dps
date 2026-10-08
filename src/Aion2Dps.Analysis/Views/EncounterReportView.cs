using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis;

/// <summary>
/// Report of one encounter: title, stat tiles (party DPS, damage, time, players, HP check), cumulative damage and boss
/// HP charts, the combatant table and per-player cards with their top three skills. Double-clicking a player raises
/// <see cref="PlayerOpened"/> and, unless <see cref="OpenBreakdownOnDoubleClick"/> is false, opens a
/// <see cref="BreakdownWindow"/>.
/// </summary>
public sealed class EncounterReportView : UserControl
{
    private readonly ScrollViewer _scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        Focusable = false,
    };
    private readonly StackPanel _content = new() { Margin = new Thickness(18, 16, 18, 6), MinWidth = 640 };
    private EncounterRecord? _record;
    private IGameData? _gameData;

    public EncounterReportView()
    {
        Resources.MergedDictionaries.Add(new AnalysisStyles());
        Ui.ApplyRootTheme(this);
        _scroll.Content = _content;
        Ui.FitWidth(_scroll, _content);
        Content = _scroll;
        _content.Children.Add(Ui.Empty("No encounter loaded"));
        Ui.RebuildOnLanguageChange(this, () => _gameData, () => { if (_record is not null) Build(); });
    }

    public EncounterReportView(EncounterRecord record, IGameData gameData) : this() => Load(record, gameData);

    /// <summary>A player row or card was double-clicked (entity id).</summary>
    public event Action<uint>? PlayerOpened;

    /// <summary>Open a <see cref="BreakdownWindow"/> on double-click (default true).</summary>
    public bool OpenBreakdownOnDoubleClick { get; set; } = true;

    public EncounterRecord? Record => _record;

    /// <summary>The cumulative damage chart (null before <see cref="Load"/>).</summary>
    public CumulativeDamageChart? DamageChart { get; private set; }

    public BossHpChart? HpChart { get; private set; }

    public void Load(EncounterRecord record, IGameData gameData)
    {
        _record = record ?? throw new ArgumentNullException(nameof(record));
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
        Build();
    }

    /// <summary>Renders the whole report (including content scrolled out of view).</summary>
    public BitmapSource RenderToBitmap(double dpi = 96)
    {
        var bg = TryFindResource(ThemeKeys.WindowBackground) as Brush ?? Brushes.Black;
        return ImageExport.RenderStacked(bg, dpi, _content);
    }

    /// <summary>Raises <see cref="PlayerOpened"/> and opens the breakdown (same as a double-click).</summary>
    public void OpenPlayer(uint entityId)
    {
        PlayerOpened?.Invoke(entityId);
        if (!OpenBreakdownOnDoubleClick || _record is null || _gameData is null) return;
        var w = new BreakdownWindow(_record, entityId, _gameData) { Owner = Window.GetWindow(this) };
        w.Show();
    }

    private void Build()
    {
        _content.Children.Clear();
        var r = _record!;
        var gd = _gameData!;
        _content.Children.Add(BuildTitle(r, gd));
        if (r.CaptureGaps)
            _content.Children.Add(Ui.Banner("The capture had gaps during this fight, so some numbers may be incomplete."));

        var friendly = ChartData.VisibleFriendly(r, out var folded);
        if (r.PartialView)
        {
            string others = folded.Count > 0
                ? $" {folded.Count} player(s) seen only through DoT ticks or heals ({Fmt.Number(folded.Sum(c => c.Damage))} damage) are not listed."
                : "";
            _content.Children.Add(Ui.Banner($"Partial view: only your/party damage is visible ({r.PartialViewReason}). " +
                                            "Contribution is your damage / the boss's max HP; shares of the visible damage and the HP check do not apply." + others));
        }
        bool multi = ChartData.IsMultiBoss(r);
        // Multi-boss fights: the tile shows the check summed over all bosses; the badges under the title show each boss.
        var check = multi ? r.OverallHpCheck ?? r.HpCheck : r.HpCheck;
        string hpCheck = check is { } hc && !r.PartialView ? Fmt.Percent(hc.Ratio) : Fmt.Dash;
        string hpKey = check is null || r.PartialView ? ThemeKeys.TextMuted : check.Passed ? ThemeKeys.Positive : ThemeKeys.Warning;
        string? hpTip = check is { } h
            ? (r.PartialView ? $"Partial view: the decoded damage covers {Fmt.Percent(h.Ratio)} of the boss's HP loss. " : "") +
              (multi ? "All bosses: " : "") + $"decoded damage {Fmt.Exact(h.DecodedDamage)} vs HP lost {Fmt.Exact(h.HpLost)} + boss self-heal {Fmt.Exact(h.BossSelfHealing)}" + (h.Note is null ? "" : $" · {h.Note}")
            : "No boss HP data to check against";
        string hpSub = r.PartialView ? "partial view" : check is null ? "not available" : check.Passed ? (multi ? "all bosses match" : "damage matches HP lost") : "check the capture";
        long bossesMax = multi ? r.Bosses.Sum(b => b.MaxHp ?? 0) : 0;
        string? damageSub = multi && bossesMax > 0 ? $"{r.Bosses.Count} bosses' HP " + Fmt.Number(bossesMax)
            : r.BossMaxHp is { } mh ? "boss HP " + Fmt.Number(mh) : null;
        _content.Children.Add(Ui.TileGrid(5,
        [
            Ui.Tile(r.PartialView ? "Visible DPS" : "Party DPS", Fmt.Dps(r.PartyDps), r.PartialView ? "visible damage / fight time" : "damage / fight time", Fmt.Exact(r.PartyDps), ThemeKeys.Accent),
            Ui.Tile("Damage", Fmt.Number(r.TotalDamage), damageSub, Fmt.Exact(r.TotalDamage)),
            Ui.Tile("Time", Fmt.Duration(r.DurationSeconds), "first to last hit", Fmt.Seconds(r.DurationSeconds)),
            Ui.Tile("Players", friendly.Count(c => c.Kind == CombatantKind.Player).ToString(), r.LocalPlayerName is { } lp ? "you: " + lp : null),
            Ui.Tile("HP check", hpCheck, hpSub, hpTip, hpKey),
        ]));

        DamageChart = new CumulativeDamageChart { Height = 270 };
        DamageChart.SetEncounter(r);
        DamageChart.HighlightEntityId = null;
        _content.Children.Add(Ui.Card(DamageChart, "Cumulative damage", subtitle: "stacked per player · hover for values"));

        bool multiHp = multi && r.Bosses.Count(b => b.HpTimeline.Count >= 2) >= 2;
        if (r.BossHpTimeline.Count >= 2 || multiHp)
        {
            HpChart = new BossHpChart { Height = multiHp ? 170 : 150 };
            HpChart.SetEncounter(r, gd);
            string? sub = multiHp ? "one line per boss · hover for values"
                : r.ResetCount > 0 ? $"{r.ResetCount} earlier reset(s) on this boss" : null;
            _content.Children.Add(Ui.Card(HpChart, multiHp ? "Bosses' HP" : "Boss HP", subtitle: sub));
        }
        else HpChart = null;

        if (multi) _content.Children.Add(BuildBossTable(r, gd));

        _content.Children.Add(BuildCombatantTable(r, friendly));
        _content.Children.Add(BuildPlayerCards(r, gd, friendly));
    }

    private static FrameworkElement BuildTitle(EncounterRecord r, IGameData gd)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var kind = Ui.Caption(Fmt.KindLabel(r.Kind), ThemeKeys.Accent);
        sp.Children.Add(kind);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        row.Children.Add(Ui.Text(ChartData.Title(r, gd), 24, ThemeKeys.Text, FontWeights.SemiBold));
        var badge = Ui.Badge(Fmt.OutcomeLabel(r.Outcome), Fmt.OutcomeBrushKey(r.Outcome));
        badge.Margin = new Thickness(12, 5, 0, 0);
        row.Children.Add(badge);
        sp.Children.Add(row);
        string map = r.MapId is { } m ? ChartData.MapLabel(gd, m) : "";
        string server = r.ServerId is { } s ? gd.GetServerName(s) ?? "" : "";
        var facts = Ui.Facts(("", map), ("", server), ("", Fmt.LocalDateTime(r.StartUtc)), ("lasted", Fmt.Duration(r.DurationSeconds)),
            ("resets", r.ResetCount > 0 ? r.ResetCount.ToString() : ""), ("", string.IsNullOrWhiteSpace(r.Note) ? "" : r.Note!));
        facts.Margin = new Thickness(0, 4, 0, 0);
        sp.Children.Add(facts);
        if (ChartData.IsMultiBoss(r)) sp.Children.Add(BossCheckBadges(r, gd));
        return sp;
    }

    /// <summary>Multi-boss fight: one "Name · HP check" badge per boss (green = decoded damage matches that boss's HP loss).</summary>
    private static FrameworkElement BossCheckBadges(EncounterRecord r, IGameData gd)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var b in r.Bosses)
        {
            string name = ChartData.BossName(b, gd, ChartData.IsOpenWorld(r, gd));
            string key = b.HpCheck is null ? ThemeKeys.TextMuted : b.HpCheck.Passed ? ThemeKeys.Positive : ThemeKeys.Warning;
            string value = b.HpCheck is { } hc ? "HP check " + Fmt.Percent(hc.Ratio) : "HP check " + Fmt.Dash;
            string tip = b.HpCheck is { } h
                ? $"{name}: decoded damage {Fmt.Exact(h.DecodedDamage)} vs HP lost {Fmt.Exact(h.HpLost)} + self-heal {Fmt.Exact(h.BossSelfHealing)}" + (h.Note is null ? "" : $" · {h.Note}")
                : $"{name}: no HP data to check against";
            var badge = Ui.Badge($"{name} · {value}", key, tip: tip);
            badge.Margin = new Thickness(0, 0, 8, 4);
            wrap.Children.Add(badge);
        }
        return wrap;
    }

    /// <summary>Multi-boss fight: one row per boss (max HP, kill time, HP check, top contributor).</summary>
    private FrameworkElement BuildBossTable(EncounterRecord r, IGameData gd)
    {
        string Who(uint id) => ChartData.Find(r, id) is { } c ? ChartData.DisplayName(c) : $"#{id}";
        var cols = new List<TableColumn<BossResult>>
        {
            new("Boss", -1, b => BossCell(b, gd), b => ChartData.BossName(b, gd, ChartData.IsOpenWorld(r, gd)), HorizontalAlignment.Left),
            new("Max HP", 80, b => Ui.Number(b.MaxHp is { } m ? Fmt.Number(m) : Fmt.Dash, b.MaxHp is { } mx ? Fmt.Exact(mx) : null), b => b.MaxHp ?? 0),
            new("Engaged", 66, b => Ui.Number(Fmt.Duration(b.EngagedSeconds), "first hit, from the start of the encounter"), b => b.EngagedSeconds),
            new("Result", 92, b => Ui.Number(BossOutcome(b), null, b.Killed ? ThemeKeys.Positive : ThemeKeys.TextMuted, FontWeights.SemiBold), b => b.KillTimeSeconds ?? double.MaxValue),
            new("HP check", 70, b => Ui.Number(b.HpCheck is { } hc ? Fmt.Percent(hc.Ratio) : Fmt.Dash, b.HpCheck?.Note,
                b.HpCheck is null ? ThemeKeys.TextMuted : b.HpCheck.Passed ? ThemeKeys.Positive : ThemeKeys.Warning), b => b.HpCheck?.Ratio ?? 0),
            new("Top damage", 170, b => b.DamageByCombatant.FirstOrDefault() is { } top
                ? Ui.Number($"{Who(top.EntityId)} · {(top.Contribution is { } c ? Fmt.Percent(c) : Fmt.Number(top.Damage))}",
                    string.Join("\n", b.DamageByCombatant.Select(d => $"{Who(d.EntityId)}: {Fmt.Exact(d.Damage)}" + (d.Contribution is { } dc ? $" ({Fmt.Percent(dc)})" : ""))))
                : Ui.Number(Fmt.Dash), b => b.DamageByCombatant.FirstOrDefault()?.Damage ?? 0),
        };
        var table = new GridTable<BossResult>(cols, -1) { RowHeight = 30 };
        table.SetItems(r.Bosses);
        return Ui.Card(table, "Bosses", subtitle: "fought at the same time · contribution counts damage to every boss of the fight");
    }

    private static string BossOutcome(BossResult b) =>
        b.Killed ? b.KillTimeSeconds is { } kt ? "Kill " + Fmt.Duration(kt) : "Kill"
        : b.HpEnd is { } end && b.MaxHp is > 0 ? Fmt.Percent((double)end / b.MaxHp.Value, 0) + " left"
        : b.Resets > 0 ? "Reset" : Fmt.Dash;

    private static FrameworkElement BossCell(BossResult b, IGameData gd)
    {
        var dp = new DockPanel();
        if (b.IsPrimary)
        {
            var badge = Ui.Badge("PRIMARY", ThemeKeys.Accent, tip: "Largest max HP: the fight is filed under this boss in the history");
            badge.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(badge, Dock.Right);
            dp.Children.Add(badge);
        }
        dp.Children.Add(Ui.Text(ChartData.BossName(b, gd), 0, ThemeKeys.Text, FontWeights.SemiBold));
        return dp;
    }

    private FrameworkElement BuildCombatantTable(EncounterRecord r, List<CombatantRecord> friendly)
    {
        var ranked = friendly.Select((c, i) => (c, rank: i + 1)).ToDictionary(x => x.c, x => x.rank);
        long top = friendly.Count == 0 ? 1 : Math.Max(1, friendly.Max(c => c.Damage));
        var cols = new List<TableColumn<CombatantRecord>>
        {
            new("#", 30, c => Ui.Number(ranked[c].ToString(), null, ThemeKeys.TextMuted), c => -ranked[c]),
            new("Player", -1, c => PlayerCell(c), c => ChartData.DisplayName(c), HorizontalAlignment.Left),
            new("Damage", 170, c => Ui.BarCell(Fmt.Number(c.Damage), (double)c.Damage / top, Fmt.Exact(c.Damage), c.Class), c => c.Damage),
            new("DPS", 70, c => Ui.Number(Fmt.Dps(c.Dps), $"{Fmt.Exact(c.Dps)} (active {Fmt.Dps(c.ActiveDps)})", ThemeKeys.Text, FontWeights.SemiBold), c => c.Dps),
            new("Contrib", 66, c => Ui.Number(Fmt.Percent(c.Contribution), ChartData.IsMultiBoss(r) ? "Share of the bosses' combined max HP removed (damage to every boss of the fight)" : "Share of boss HP removed (or of party damage)"), c => c.Contribution),
            new("Crit", 56, c => Ui.Number(Fmt.Rate(c.Quality.Crits, c.Quality.Hits, 0), $"{c.Quality.Crits}/{c.Quality.Hits}"), c => Fmt.RateValue(c.Quality.Crits, c.Quality.Hits)),
            new("Deaths", 56, c => Ui.Number(c.Deaths > 0 ? c.Deaths.ToString() : Fmt.Dash, null, c.Deaths > 0 ? ThemeKeys.Negative : ThemeKeys.TextMuted), c => c.Deaths),
        };
        var table = new GridTable<CombatantRecord>(cols, 2) { RowHeight = 32, RowMarker = c => c.IsLocal ? ThemeKeys.Accent : null };
        table.RowDoubleClicked += c => OpenPlayer(c.EntityId);
        table.SetItems(friendly);
        return Ui.Card(table, "Combatants", subtitle: "double-click a player for the full breakdown");
    }

    private static FrameworkElement PlayerCell(CombatantRecord c)
    {
        var dp = new DockPanel();
        var em = Ui.Emblem(c.Class, 22);
        em.Margin = new Thickness(0, 0, 9, 0);
        DockPanel.SetDock(em, Dock.Left);
        dp.Children.Add(em);
        if (c.IsLocal)
        {
            var b = Ui.Badge("YOU", ThemeKeys.Accent);
            b.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(b, Dock.Right);
            dp.Children.Add(b);
        }
        dp.Children.Add(Ui.Text(ChartData.DisplayName(c), 0, ThemeKeys.Text, FontWeights.SemiBold));
        return dp;
    }

    private FrameworkElement BuildPlayerCards(EncounterRecord r, IGameData gd, List<CombatantRecord> friendly)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 0, -10, 0) };
        long total = Math.Max(1, friendly.Sum(c => c.Damage));
        foreach (var c in friendly.Where(c => c.Damage > 0))
        {
            var sp = new StackPanel();
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var em = Ui.Emblem(c.Class, 30);
            em.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(em, Dock.Left);
            head.Children.Add(em);
            var share = Ui.Number(Fmt.Percent((double)c.Damage / total, 1), "Share of party damage", ThemeKeys.TextMuted);
            share.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(share, Dock.Right);
            head.Children.Add(share);
            var names = new StackPanel();
            names.Children.Add(Ui.Text(ChartData.DisplayName(c), 13.5, ThemeKeys.Text, FontWeights.SemiBold));
            names.Children.Add(Ui.Text(gd.GetClassName(c.Class), 11, ThemeKeys.TextMuted));
            head.Children.Add(names);
            sp.Children.Add(head);

            var nums = new UniformGrid2();
            nums.Add(Ui.Caption("Damage"), Ui.Number(Fmt.Number(c.Damage), Fmt.Exact(c.Damage), ThemeKeys.Text, FontWeights.SemiBold, 15));
            nums.Add(Ui.Caption("DPS"), Ui.Number(Fmt.Dps(c.Dps), Fmt.Exact(c.Dps), ThemeKeys.Accent, FontWeights.SemiBold, 15));
            sp.Children.Add(nums.Element);

            var topSkills = c.Skills.Where(s => s.Damage > 0).OrderByDescending(s => s.Damage).Take(3).ToList();
            long best = topSkills.Count == 0 ? 1 : Math.Max(1, topSkills[0].Damage);
            foreach (var s in topSkills)
            {
                uint id = s.SampleSkillId != 0 ? s.SampleSkillId : s.SkillId;
                var g = new Grid { Margin = new Thickness(0, 6, 0, 0) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
                var skill = Ui.SkillCell(gd.GetSkillName(s.SkillId), id, SkillVisuals.TileClass(gd, id, c.Class), tileSize: 18);
                var amount = Ui.Number(Fmt.Number(s.Damage), Fmt.Exact(s.Damage), ThemeKeys.TextMuted);
                Grid.SetColumn(amount, 1);
                g.Children.Add(skill);
                g.Children.Add(amount);
                var bar = new MiniBar { Value = (double)s.Damage / best, Class = c.Class, BarHeight = 3, Margin = new Thickness(26, 2, 0, 0) };
                Grid.SetRow(bar, 1);
                Grid.SetColumnSpan(bar, 2);
                g.RowDefinitions.Add(new RowDefinition());
                g.RowDefinitions.Add(new RowDefinition());
                g.Children.Add(bar);
                sp.Children.Add(g);
            }
            if (topSkills.Count == 0) sp.Children.Add(Ui.Text("No skill data", 11, ThemeKeys.TextMuted));

            var card = Ui.Card(sp, padding: new Thickness(14, 12, 14, 12));
            card.Width = 262;
            card.Margin = new Thickness(0, 0, 10, 10);
            card.Cursor = Cursors.Hand;
            card.ToolTip = "Double-click for the full breakdown";
            uint entity = c.EntityId;
            card.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2) { OpenPlayer(entity); e.Handled = true; }
            };
            if (c.IsLocal) card.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.Accent);
            wrap.Children.Add(card);
        }
        var host = new StackPanel();
        var title = Ui.Text("Players", 13.5, ThemeKeys.Text, FontWeights.SemiBold);
        title.Margin = new Thickness(2, 4, 0, 10);
        host.Children.Add(title);
        host.Children.Add(wrap);
        return host;
    }

    /// <summary>Two caption/value columns.</summary>
    private sealed class UniformGrid2
    {
        public readonly System.Windows.Controls.Primitives.UniformGrid Element = new() { Columns = 2, Margin = new Thickness(0, 0, 0, 4) };

        public void Add(TextBlock caption, TextBlock value)
        {
            var sp = new StackPanel();
            sp.Children.Add(caption);
            value.HorizontalAlignment = HorizontalAlignment.Left;
            value.Margin = new Thickness(0, 1, 0, 0);
            sp.Children.Add(value);
            Element.Children.Add(sp);
        }
    }
}
