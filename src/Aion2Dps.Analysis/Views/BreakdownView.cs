using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis;

/// <summary>Tabs of the per-player breakdown.</summary>
public enum BreakdownTab
{
    Dps,
    Accuracy,
    Defense,
    Buffs,
}

/// <summary>
/// Per-player breakdown of one encounter: header (class, name, server, damage, DPS, active DPS, contribution, share)
/// and the tabs DPS (party timeline, rotation ribbon, skills table), Accuracy, Defense and Buffs.
/// Works from an <see cref="EncounterRecord"/> and <see cref="IGameData"/> only.
/// </summary>
public sealed class BreakdownView : UserControl
{
    private readonly Border _headerHost = new() { Padding = new Thickness(16, 14, 16, 6) };
    private readonly Border _tabHost = new() { Padding = new Thickness(10, 0, 10, 0), BorderThickness = new Thickness(0, 0, 0, 1) };
    private readonly ScrollViewer _scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        Focusable = false,
    };
    private readonly StackPanel _content = new() { Margin = new Thickness(16, 14, 16, 4), MinWidth = 700 };
    private readonly Dictionary<BreakdownTab, RadioButton> _tabButtons = new();
    private readonly string _groupName = "a2tabs" + Guid.NewGuid().ToString("N");

    private EncounterRecord? _record;
    private IGameData? _gameData;
    private uint _entityId;
    private BreakdownTab _tab = BreakdownTab.Dps;
    private bool _pinned;
    private bool _showPin = true;
    private bool _showPicker = true;
    private bool _suppressPicker;
    private RotationData? _rotation;
    private uint _rotationFor;
    private RotationRibbon? _ribbon;
    private GridTable<SkillRow>? _skillTable;
    private bool _languageHooked;

    public BreakdownView()
    {
        Resources.MergedDictionaries.Add(new AnalysisStyles());
        Ui.ApplyRootTheme(this);
        _tabHost.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.Border);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (tab, label) in new[] { (BreakdownTab.Dps, "DPS"), (BreakdownTab.Accuracy, "Accuracy"), (BreakdownTab.Defense, "Defense"), (BreakdownTab.Buffs, "Buffs") })
        {
            var rb = new RadioButton { Content = label, GroupName = _groupName, IsChecked = tab == _tab };
            rb.SetResourceReference(StyleProperty, "A2.Tab");
            rb.Checked += (_, _) => { if (_tab != tab) SelectedTab = tab; };
            _tabButtons[tab] = rb;
            tabs.Children.Add(rb);
        }
        _tabHost.Child = tabs;
        _scroll.Content = _content;
        Ui.FitWidth(_scroll, _content);

        var layout = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_headerHost, Dock.Top);
        DockPanel.SetDock(_tabHost, Dock.Top);
        layout.Children.Add(_headerHost);
        layout.Children.Add(_tabHost);
        layout.Children.Add(_scroll);
        Content = layout;
        _content.Children.Add(Ui.Empty("No encounter loaded"));

        Loaded += (_, _) => HookLanguage(true);
        Unloaded += (_, _) => HookLanguage(false);
    }

    public BreakdownView(EncounterRecord record, uint combatantEntityId, IGameData gameData) : this()
    {
        Load(record, combatantEntityId, gameData);
    }

    /// <summary>Raised after the shown combatant changed (picker or <see cref="ShowCombatant"/>).</summary>
    public event Action<uint>? CombatantChanged;

    /// <summary>Raised when the pin button toggles (entity id, pinned).</summary>
    public event Action<uint, bool>? PinToggled;

    /// <summary>
    /// Raised when this view is pinned and another player is picked: (pinned entity, picked entity). When nobody
    /// handles it the view simply switches to the picked player.
    /// </summary>
    public event Action<uint, uint>? CompareRequested;

    public EncounterRecord? Record => _record;
    public IGameData? GameData => _gameData;
    public uint CombatantEntityId => _entityId;
    public CombatantRecord? Combatant => _record is null ? null : ChartData.Find(_record, _entityId);

    /// <summary>Shows the PINNED badge (and checks the pin button).</summary>
    public bool IsPinned
    {
        get => _pinned;
        set { _pinned = value; RebuildHeader(); }
    }

    public bool ShowPinButton
    {
        get => _showPin;
        set { _showPin = value; RebuildHeader(); }
    }

    public bool ShowPlayerPicker
    {
        get => _showPicker;
        set { _showPicker = value; RebuildHeader(); }
    }

    public BreakdownTab SelectedTab
    {
        get => _tab;
        set
        {
            _tab = value;
            if (_tabButtons.TryGetValue(value, out var rb) && rb.IsChecked != true) rb.IsChecked = true;
            RebuildContent();
            _scroll.ScrollToTop();
        }
    }

    /// <summary>The rotation ribbon of the DPS tab (null on other tabs).</summary>
    public RotationRibbon? Ribbon => _ribbon;

    public void Load(EncounterRecord record, uint combatantEntityId, IGameData gameData)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(gameData);
        if (_languageHooked && !ReferenceEquals(_gameData, gameData))
        {
            HookLanguage(false);
            _gameData = gameData;
            HookLanguage(true);
        }
        _record = record;
        _gameData = gameData;
        _entityId = ChartData.Find(record, combatantEntityId) is not null
            ? combatantEntityId
            : (ChartData.Local(record) ?? record.Combatants.OrderByDescending(c => c.Damage).FirstOrDefault())?.EntityId ?? combatantEntityId;
        _rotation = null;
        Rebuild();
    }

    public void ShowCombatant(uint entityId)
    {
        if (_record is null || ChartData.Find(_record, entityId) is null || entityId == _entityId) return;
        _entityId = entityId;
        _rotation = null;
        Rebuild();
        CombatantChanged?.Invoke(entityId);
    }

    /// <summary>Renders header, tabs and the whole tab content (including rows scrolled out of view) to a bitmap.</summary>
    public BitmapSource RenderToBitmap(double dpi = 96)
    {
        if (ActualWidth <= 0)
        {
            Measure(new Size(1000, double.PositiveInfinity));
            Arrange(new Rect(DesiredSize));
            UpdateLayout();
        }
        var bg = TryFindResource(ThemeKeys.WindowBackground) as Brush ?? TryFindResource(ThemeKeys.Surface) as Brush ?? Brushes.Black;
        return ImageExport.RenderStacked(bg, dpi, _headerHost, _tabHost, _content);
    }

    /// <summary>"Copy as image": puts <see cref="RenderToBitmap"/> on the clipboard.</summary>
    public bool CopyImageToClipboard() => ImageExport.CopyToClipboard(RenderToBitmap());

    // ───────────── Building ─────────────

    private void HookLanguage(bool on)
    {
        if (_gameData is null || on == _languageHooked) return;
        if (on) _gameData.LanguageChanged += OnLanguageChanged;
        else _gameData.LanguageChanged -= OnLanguageChanged;
        _languageHooked = on;
    }

    private void OnLanguageChanged() => Dispatcher.BeginInvoke(Rebuild);

    private void Rebuild()
    {
        RebuildHeader();
        RebuildContent();
    }

    private RotationData Rotation(CombatantRecord c)
    {
        if (_rotation is null || _rotationFor != c.EntityId)
        {
            _rotation = CastGrouper.Build(_record!.Hits, c.EntityId, includeIncoming: c.Kind == CombatantKind.EnemyPlayer);
            _rotationFor = c.EntityId;
        }
        return _rotation;
    }

    private void RebuildHeader()
    {
        if (_record is null || _gameData is null || Combatant is not { } c)
        {
            _headerHost.Child = null;
            return;
        }
        var r = _record;
        var root = new StackPanel();

        var top = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 12) };
        var emblem = Ui.Emblem(c.Class, 46);
        emblem.Margin = new Thickness(0, 0, 14, 0);
        DockPanel.SetDock(emblem, Dock.Left);
        top.Children.Add(emblem);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        if (_showPicker) controls.Children.Add(BuildPicker(r));
        if (_showPin)
        {
            var pin = new ToggleButton
            {
                Content = _pinned ? "Pinned" : "Pin",
                IsChecked = _pinned,
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = "Pin this player, then pick another one to compare side by side",
            };
            pin.SetResourceReference(StyleProperty, "A2.ToggleButton");
            pin.Click += (_, _) =>
            {
                _pinned = pin.IsChecked == true;
                RebuildHeader();
                PinToggled?.Invoke(_entityId, _pinned);
            };
            controls.Children.Add(pin);
        }
        controls.Children.Add(Ui.Button("Copy image", () => CopyImageToClipboard(), "Copy the whole breakdown (all rows) to the clipboard"));
        DockPanel.SetDock(controls, Dock.Right);
        top.Children.Add(controls);

        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(Ui.Text(ChartData.DisplayName(c), 21, ThemeKeys.Text, FontWeights.SemiBold));
        void AddBadge(FrameworkElement b)
        {
            b.Margin = new Thickness(10, 3, 0, 0);
            nameRow.Children.Add(b);
        }
        if (_pinned) AddBadge(Ui.Badge("PINNED", ThemeKeys.Accent, solid: true));
        if (c.IsLocal) AddBadge(Ui.Badge("YOU", ThemeKeys.Accent));
        if (c.Kind == CombatantKind.EnemyPlayer) AddBadge(Ui.Badge("ENEMY", ThemeKeys.Negative));
        if (c.Kind == CombatantKind.EnemyPlayer && c.KilledByLocal) AddBadge(Ui.Badge("KILLED", ThemeKeys.Positive));
        if (c.Deaths > 0) AddBadge(Ui.Badge(c.Deaths == 1 ? "DIED" : $"DIED ×{c.Deaths}", ThemeKeys.Negative));
        names.Children.Add(nameRow);
        string server = c.ServerId is { } sid ? _gameData.GetServerName(sid) ?? $"Server {sid}" : "";
        var facts = Ui.Facts(
            ("", _gameData.GetClassName(c.Class)),
            ("", server),
            ("Lv", c.Level?.ToString() ?? ""),
            ("GS", c.GearScore is { } gs ? Fmt.Exact((long)gs) : ""),
            ("CP", c.CombatPower is { } cp ? Fmt.Number((long)Math.Min(cp, long.MaxValue)) : ""),
            ("", ChartData.Title(r, _gameData) + " · " + Fmt.Duration(r.DurationSeconds)));
        facts.Margin = new Thickness(0, 3, 0, 0);
        names.Children.Add(facts);
        top.Children.Add(names);
        root.Children.Add(top);

        double active = c.FirstHitUtc is { } f && c.LastHitUtc is { } l ? Math.Max(1, (l - f).TotalSeconds) : r.DurationSeconds;
        bool multiBoss = ChartData.IsMultiBoss(r);
        bool contributionIsHp = r.BossMaxHp is > 0 && (c.BossDamage > 0 || (multiBoss && c.AllBossesDamage > 0));
        string maxHitSkill = c.Quality.MaxHit > 0 && c.Quality.MaxHitSkillId != 0 ? _gameData.GetSkillName(c.Quality.MaxHitSkillId) : "";
        root.Children.Add(Ui.TileGrid(6,
        [
            Ui.Tile("Damage", Fmt.Number(c.Damage), c.Healing > 0 ? "+" + Fmt.Number(c.Healing) + " healing" : null, Fmt.Exact(c.Damage) + " damage"),
            Ui.Tile("DPS", Fmt.Dps(c.Dps), "over " + Fmt.Duration(r.DurationSeconds), $"{Fmt.Exact(c.Dps)} = damage / fight time ({Fmt.Duration(r.DurationSeconds)})", ThemeKeys.Accent),
            Ui.Tile("Active DPS", Fmt.Dps(c.ActiveDps), "over " + Fmt.Duration(active), $"{Fmt.Exact(c.ActiveDps)} = damage / own first-to-last hit ({Fmt.Duration(active)})"),
            Ui.Tile("Contribution", Fmt.Percent(c.Contribution), contributionIsHp ? multiBoss ? "of the bosses' HP" : "of boss HP" : "of party damage",
                contributionIsHp
                    ? multiBoss ? "Share of the bosses' combined max HP this player removed (damage to every boss of the fight)" : "Share of the boss's max HP this player removed"
                    : "Boss HP unknown: share of party damage"),
            r.PartialView
                ? Ui.Tile("Share", Fmt.Dash, "partial view", "Partial view: only your/party damage is visible, so a share of the visible damage means nothing")
                : Ui.Tile("Share", Fmt.Percent(c.DamageShare), "of party damage", "Share of all friendly damage"),
            Ui.Tile("Max hit", c.Quality.MaxHit > 0 ? Fmt.Number(c.Quality.MaxHit) : Fmt.Dash, maxHitSkill.Length > 0 ? maxHitSkill : null,
                c.Quality.MaxHit > 0 ? Fmt.Exact(c.Quality.MaxHit) + (maxHitSkill.Length > 0 ? " · " + maxHitSkill : "") : "No direct hits", ThemeKeys.Crit),
        ]));
        _headerHost.Child = root;
    }

    private FrameworkElement BuildPicker(EncounterRecord r)
    {
        var picker = new ComboBox { Width = 250, VerticalAlignment = VerticalAlignment.Top, ToolTip = "Switch player" };
        var self = Combatant;
        var list = (self?.Kind == CombatantKind.EnemyPlayer ? ChartData.Enemies(r) : ChartData.Friendly(r)).ToList();
        if (self is not null && !list.Contains(self)) list.Insert(0, self);
        int rank = 1;
        _suppressPicker = true;
        foreach (var c in list)
        {
            var item = new ComboBoxItem
            {
                Content = $"{rank++}. {ChartData.DisplayName(c)} · {ClassInfo.ShortName(c.Class)} · {Fmt.Number(c.Damage)}",
                Tag = c.EntityId,
            };
            picker.Items.Add(item);
            if (c.EntityId == _entityId) picker.SelectedItem = item;
        }
        _suppressPicker = false;
        picker.SelectionChanged += (_, _) =>
        {
            if (_suppressPicker || picker.SelectedItem is not ComboBoxItem { Tag: uint id } || id == _entityId) return;
            if (_pinned && CompareRequested is not null)
            {
                uint pinnedId = _entityId;
                Dispatcher.BeginInvoke(() =>
                {
                    RebuildHeader(); // restore the picker to the pinned player
                    CompareRequested?.Invoke(pinnedId, id);
                });
                return;
            }
            Dispatcher.BeginInvoke(() => ShowCombatant(id));
        };
        return picker;
    }

    private void RebuildContent()
    {
        _content.Children.Clear();
        _ribbon = null;
        _skillTable = null;
        if (_record is null || _gameData is null || Combatant is not { } c)
        {
            _content.Children.Add(Ui.Empty("No encounter loaded"));
            return;
        }
        if (_record.CaptureGaps)
            _content.Children.Add(Ui.Banner("The capture had gaps during this fight, so some numbers may be incomplete."));
        switch (_tab)
        {
            case BreakdownTab.Dps: BuildDpsTab(c); break;
            case BreakdownTab.Accuracy: BuildAccuracyTab(c); break;
            case BreakdownTab.Defense: BuildDefenseTab(c); break;
            case BreakdownTab.Buffs: BuildBuffsTab(c); break;
        }
    }

    // ───────────── DPS tab ─────────────

    internal sealed class SkillRow
    {
        public required SkillStats S { get; init; }
        public required string Name { get; init; }
        public CharacterClass TileClass { get; init; }
        public long Damage => S.Damage;
        public double Share { get; init; }
        public double Dps { get; init; }
        public int Casts { get; init; }
        public long? Avg { get; init; }
        public bool IsBiggest { get; init; }
        public double BarFraction { get; init; }
    }

    private void BuildDpsTab(CombatantRecord c)
    {
        var r = _record!;
        var gd = _gameData!;
        bool enemySide = c.Kind == CombatantKind.EnemyPlayer;

        var chart = new DpsTimelineChart { Height = 220 };
        chart.SetEncounter(r, c.EntityId, 5, x => (x.Kind == CombatantKind.EnemyPlayer) == enemySide);
        _content.Children.Add(Ui.Card(chart, enemySide ? "Opponent DPS timeline" : "Party DPS timeline", subtitle: "rolling 5 s average · hover for values"));

        var rotation = Rotation(c);
        _ribbon = new RotationRibbon { Height = 156 };
        _ribbon.SetRotation(rotation, gd, r.DurationSeconds, c.Class);
        var zoomHint = Ui.Text("Ctrl + wheel to zoom", 11, ThemeKeys.TextMuted);
        _content.Children.Add(Ui.Card(_ribbon, "Rotation", zoomHint,
            $"{rotation.Casts.Count} casts · {rotation.DotTicks.Count} DoT ticks · gold ring = mostly crits · purple marks = DoT ticks"));

        long total = Math.Max(1, c.Damage);
        long biggest = c.Skills.Count == 0 ? 0 : c.Skills.Max(s => s.MaxHit);
        var castCounts = CastGrouper.CountByGroup(rotation, gd.GetSkillGroupKey);
        var dmgSkills = c.Skills.Where(s => s.Damage > 0 || s.DotDamage > 0 || s.Healing <= 0).ToList();
        long maxDmg = dmgSkills.Count == 0 ? 1 : Math.Max(1, dmgSkills.Max(s => s.Damage));
        var rows = dmgSkills.Select(s => new SkillRow
        {
            S = s,
            Name = gd.GetSkillName(s.SkillId),
            TileClass = SkillVisuals.TileClass(gd, s.SampleSkillId != 0 ? s.SampleSkillId : s.SkillId, c.Class),
            Share = (double)s.Damage / total,
            Dps = s.Damage / Math.Max(1, r.DurationSeconds),
            Casts = s.Casts > 0 ? s.Casts : castCounts.GetValueOrDefault(s.SkillId),
            Avg = s.Hits > 0 ? (s.Damage - s.DotDamage) / s.Hits : null,
            IsBiggest = biggest > 0 && s.MaxHit == biggest,
            BarFraction = (double)s.Damage / maxDmg,
        }).ToList();

        var cols = new List<TableColumn<SkillRow>>
        {
            new("Skill", -1, x => Ui.SkillCell(x.Name, x.S.SampleSkillId != 0 ? x.S.SampleSkillId : x.S.SkillId, x.TileClass, SkillTags(x.S)),
                x => x.Name, HorizontalAlignment.Left),
            new("Hits", 50, x => Ui.Number(x.S.Hits.ToString(), x.S.DotTicks > 0 ? $"{x.S.Hits} direct hits + {x.S.DotTicks} DoT ticks" : $"{x.S.Hits} direct hits"), x => x.S.Hits),
            new("Casts", 50, x => Ui.Number(x.Casts > 0 ? x.Casts.ToString() : Fmt.Dash), x => x.Casts),
            new("Damage", 150, x => Ui.BarCell(Fmt.Number(x.Damage), x.BarFraction, Fmt.Exact(x.Damage) + (x.S.DotDamage > 0 ? $" (DoT {Fmt.Exact(x.S.DotDamage)})" : ""), c.Class), x => x.Damage),
            new("DPS", 60, x => Ui.Number(Fmt.Dps(x.Dps), Fmt.Exact(x.Dps)), x => x.Dps),
            new("Avg", 60, x => Ui.Number(x.Avg is { } a ? Fmt.Number(a) : Fmt.Dash, x.Avg is { } a2 ? Fmt.Exact(a2) + " per direct hit" : "No direct hits"), x => x.Avg),
            new("Min", 56, x => Ui.Number(x.S.Hits > 0 ? Fmt.Number(x.S.MinHit) : Fmt.Dash, x.S.Hits > 0 ? Fmt.Exact(x.S.MinHit) : null), x => x.S.Hits > 0 ? x.S.MinHit : null),
            new("Max", 66, x => MaxCell(x), x => x.S.Hits > 0 ? x.S.MaxHit : null),
            new("Share", 54, x => Ui.Number(Fmt.Percent(x.Share)), x => x.Share),
            new("Crit", 52, x => Ui.Number(Fmt.Rate(x.S.Crits, x.S.Hits, 0), $"{x.S.Crits} of {x.S.Hits} direct hits"), x => Fmt.RateValue(x.S.Crits, x.S.Hits)),
        };
        _skillTable = new GridTable<SkillRow>(cols, defaultSortColumn: 3) { Selectable = true, RowMarker = x => x.IsBiggest ? ThemeKeys.Crit : null };
        _skillTable.RowClicked += row =>
        {
            if (_ribbon is null) return;
            _ribbon.HighlightSkillGroup = _ribbon.HighlightSkillGroup == row.S.SkillId ? null : row.S.SkillId;
            if (_ribbon.HighlightSkillGroup is null) _skillTable.Selected = null;
        };
        _skillTable.SetItems(rows);
        var totalLine = Ui.Facts(("Total", Fmt.Number(c.Damage)), ("DoT", c.Quality.DotDamage > 0 ? Fmt.Number(c.Quality.DotDamage) : ""),
            ("skills", rows.Count.ToString()));
        _content.Children.Add(Ui.Card(_skillTable, "Skills", totalLine, "click a row to spotlight it in the rotation"));

        var healSkills = c.Skills.Where(s => s.Healing > 0).ToList();
        if (healSkills.Count > 0)
        {
            long maxHeal = healSkills.Max(s => s.Healing);
            var healCols = new List<TableColumn<SkillStats>>
            {
                new("Skill", -1, s => Ui.SkillCell(gd.GetSkillName(s.SkillId), s.SampleSkillId != 0 ? s.SampleSkillId : s.SkillId,
                    SkillVisuals.TileClass(gd, s.SampleSkillId != 0 ? s.SampleSkillId : s.SkillId, c.Class)), s => gd.GetSkillName(s.SkillId), HorizontalAlignment.Left),
                new("Hits", 60, s => Ui.Number(s.Hits.ToString()), s => s.Hits),
                new("Healing", 200, s => Ui.BarCell(Fmt.Number(s.Healing), (double)s.Healing / maxHeal, Fmt.Exact(s.Healing), fillKey: ThemeKeys.Heal), s => s.Healing),
                new("HPS", 70, s => Ui.Number(Fmt.Dps(s.Healing / Math.Max(1, r.DurationSeconds))), s => s.Healing),
            };
            var healTable = new GridTable<SkillStats>(healCols, 2);
            healTable.SetItems(healSkills);
            _content.Children.Add(Ui.Card(healTable, "Healing done", Ui.Facts(("Total", Fmt.Number(c.Healing)))));
        }
    }

    private static IEnumerable<(string, string)> SkillTags(SkillStats s)
    {
        if (s.DotTicks > 0) yield return ("DoT", ThemeKeys.Dot);
        if (s.FromSummon) yield return ("Summon", ThemeKeys.Accent);
    }

    private static UIElement MaxCell(SkillRow x)
    {
        if (x.S.Hits <= 0) return Ui.Number(Fmt.Dash);
        if (!x.IsBiggest) return Ui.Number(Fmt.Number(x.S.MaxHit), Fmt.Exact(x.S.MaxHit));
        var b = Ui.Badge(Fmt.Number(x.S.MaxHit), ThemeKeys.Crit, tip: "Biggest hit of the fight: " + Fmt.Exact(x.S.MaxHit));
        b.HorizontalAlignment = HorizontalAlignment.Right;
        return b;
    }

    // ───────────── Accuracy tab ─────────────

    private void BuildAccuracyTab(CombatantRecord c)
    {
        var q = c.Quality;
        var gd = _gameData!;
        int qm = q.QualityMeasuredHits;
        string Sub(int denom) => denom > 0 ? $"{Fmt.Exact((long)denom)} hits measured" : "not reported";
        _content.Children.Add(Ui.TileGrid(4,
        [
            Ui.Tile("Crit", Fmt.Rate(q.Crits, q.Hits), Sub(q.Hits), $"{q.Crits} critical hits of {q.Hits} direct hits", ThemeKeys.Crit),
            Ui.Tile("Back", Fmt.Rate(q.Back, qm), Sub(qm), $"{q.Back} back attacks of {qm} hits with direction data"),
            Ui.Tile("Front", Fmt.Rate(q.Front, qm), Sub(qm), $"{q.Front} front attacks of {qm} hits with direction data"),
            Ui.Tile("Perfect", Fmt.Rate(q.Perfect, qm), Sub(qm), $"{q.Perfect} perfect hits of {qm}"),
            Ui.Tile("Double", Fmt.Rate(q.Double, qm), Sub(qm), $"{q.Double} double hits of {qm}"),
            Ui.Tile("Smite", Fmt.Rate(q.Smite, qm), Sub(qm), $"{q.Smite} smites of {qm}"),
            Ui.Tile("Parried", Fmt.Rate(q.Parried, qm), Sub(qm), $"The target parried {q.Parried} of {qm} hits", ThemeKeys.Warning),
            Ui.Tile("Dodged", Fmt.Rate(q.Dodged, q.Hits), Sub(q.Hits), $"The target dodged {q.Dodged} attacks", ThemeKeys.Warning),
        ]));

        long parriedAmount = _record!.Hits.Where(h => h.Actor == c.EntityId && (h.Flags & HitFlags.Parry) != 0 && (h.Flags & HitFlags.Incoming) == 0).Sum(h => h.Amount);
        var facts = Ui.Facts(
            ("Multi-hits", q.MultiHits > 0 ? $"{q.MultiHits} (+{q.ExtraHitCount} extra)" : Fmt.Dash),
            ("DoT ticks", q.DotTicks > 0 ? $"{q.DotTicks} for {Fmt.Number(q.DotDamage)}" : "none"),
            ("Lost to parries ≈", q.Parried > 0 ? Fmt.Number(parriedAmount) : Fmt.Dash));
        facts.Margin = new Thickness(2, 0, 0, 6);
        _content.Children.Add(facts);
        var note = Ui.Text("DoT ticks never count as hits. A hit can be both Smite and Perfect, so those rates overlap. " +
                           "A dash means the game did not report that field for these hits.", 11.5, ThemeKeys.TextMuted, wrap: true);
        note.Margin = new Thickness(2, 0, 0, 14);
        _content.Children.Add(note);

        var skills = c.Skills.Where(s => s.Hits > 0).ToList();
        string R(int part, int denom) => Fmt.Rate(part, denom, 0);
        var cols = new List<TableColumn<SkillStats>>
        {
            new("Skill", -1, s => Ui.SkillCell(gd.GetSkillName(s.SkillId), s.SampleSkillId != 0 ? s.SampleSkillId : s.SkillId,
                SkillVisuals.TileClass(gd, s.SampleSkillId != 0 ? s.SampleSkillId : s.SkillId, c.Class), SkillTags(s)), s => gd.GetSkillName(s.SkillId), HorizontalAlignment.Left),
            new("Hits", 52, s => Ui.Number(s.Hits.ToString()), s => s.Hits),
            new("Crit", 56, s => Ui.Number(R(s.Crits, s.Hits), $"{s.Crits}/{s.Hits}", ThemeKeys.Crit), s => Fmt.RateValue(s.Crits, s.Hits)),
            new("Back", 56, s => Ui.Number(R(s.Back, s.QualityMeasuredHits), $"{s.Back}/{s.QualityMeasuredHits}"), s => Fmt.RateValue(s.Back, s.QualityMeasuredHits)),
            new("Front", 56, s => Ui.Number(R(s.Front, s.QualityMeasuredHits), $"{s.Front}/{s.QualityMeasuredHits}"), s => Fmt.RateValue(s.Front, s.QualityMeasuredHits)),
            new("Perf", 56, s => Ui.Number(R(s.Perfect, s.QualityMeasuredHits), $"{s.Perfect}/{s.QualityMeasuredHits}"), s => Fmt.RateValue(s.Perfect, s.QualityMeasuredHits), headerTip: "Perfect"),
            new("Dbl", 56, s => Ui.Number(R(s.Double, s.QualityMeasuredHits), $"{s.Double}/{s.QualityMeasuredHits}"), s => Fmt.RateValue(s.Double, s.QualityMeasuredHits), headerTip: "Double"),
            new("Smite", 56, s => Ui.Number(R(s.Smite, s.QualityMeasuredHits), $"{s.Smite}/{s.QualityMeasuredHits}"), s => Fmt.RateValue(s.Smite, s.QualityMeasuredHits)),
            new("Parry", 56, s => Ui.Number(R(s.Parried, s.QualityMeasuredHits), $"{s.Parried}/{s.QualityMeasuredHits}", ThemeKeys.Warning), s => Fmt.RateValue(s.Parried, s.QualityMeasuredHits), headerTip: "Parried by the target"),
            new("Damage", 80, s => Ui.Number(Fmt.Number(s.Damage), Fmt.Exact(s.Damage)), s => s.Damage),
        };
        var table = new GridTable<SkillStats>(cols, 9);
        table.SetItems(skills);
        _content.Children.Add(Ui.Card(table, "Hit quality per skill", subtitle: "rates over each skill's measured hits"));
    }

    // ───────────── Defense tab ─────────────

    private void BuildDefenseTab(CombatantRecord c)
    {
        var d = c.Defense;
        var gd = _gameData!;
        var r = _record!;
        string Opt(int? v) => v is { } x ? Fmt.Exact((long)x) : Fmt.Dash;
        const string notReported = "not reported by the game";
        _content.Children.Add(Ui.TileGrid(5,
        [
            Ui.Tile("Hits taken", Fmt.Exact((long)d.HitsTaken), null, $"{d.HitsTaken} incoming hits"),
            Ui.Tile("Damage taken", Fmt.Number(d.DamageTaken), d.HitsTaken > 0 ? "avg " + Fmt.Number(d.DamageTaken / Math.Max(1, d.HitsTaken)) : null, Fmt.Exact(d.DamageTaken), ThemeKeys.Negative),
            Ui.Tile("Crits taken", Fmt.Exact((long)d.CritsTaken), Fmt.Rate(d.CritsTaken, d.HitsTaken) + " of hits", null),
            Ui.Tile("Back hits taken", Fmt.Exact((long)d.BackHitsTaken), Fmt.Rate(d.BackHitsTaken, d.HitsTaken) + " of hits", null),
            Ui.Tile("Healing received", Fmt.Number(d.HealingReceived), null, Fmt.Exact(d.HealingReceived), ThemeKeys.Heal),
            Ui.Tile("Dodged", Fmt.Exact((long)d.Dodged), Fmt.Rate(d.Dodged, d.HitsTaken + d.Dodged) + " of attacks", null, ThemeKeys.Positive),
            Ui.Tile("Parried", Fmt.Exact((long)d.Parried), Fmt.Rate(d.Parried, d.HitsTaken) + " of hits", null, ThemeKeys.Positive),
            Ui.Tile("Blocked", Opt(d.Blocked), d.Blocked is null ? notReported : null, d.Blocked is null ? "The protocol does not report blocks" : null),
            Ui.Tile("Endured", Opt(d.Endured), d.Endured is null ? notReported : null, d.Endured is null ? "The protocol does not report endures" : null),
            Ui.Tile("Resisted", Opt(d.Resisted), d.Resisted is null ? notReported : null, d.Resisted is null ? "The protocol does not report resists" : null),
        ]));
        if (c.Deaths > 0)
        {
            var deaths = Ui.Facts(("Deaths", c.Deaths.ToString()));
            deaths.Margin = new Thickness(2, 0, 0, 10);
            _content.Children.Add(deaths);
        }

        var sources = c.DamageTakenBySource.OrderByDescending(s => s.Damage).ToList();
        long maxSrc = sources.Count == 0 ? 1 : Math.Max(1, sources.Max(s => s.Damage));
        long totalTaken = Math.Max(1, sources.Sum(s => s.Damage));
        var cols = new List<TableColumn<SourceDamage>>
        {
            new("Source", -1, s => SourceCell(s, r, gd), s => SourceName(s, r, gd), HorizontalAlignment.Left),
            new("Skill", 190, s => Ui.Text(gd.GetSkillName(s.SkillId), 0, ThemeKeys.Text, tip: $"{gd.GetSkillName(s.SkillId)} (id {s.SkillId})"), s => gd.GetSkillName(s.SkillId), HorizontalAlignment.Left),
            new("Hits", 50, s => Ui.Number(s.Hits.ToString()), s => s.Hits),
            new("Crits", 50, s => Ui.Number(s.Crits.ToString()), s => s.Crits),
            new("Damage", 150, s => Ui.BarCell(Fmt.Number(s.Damage), (double)s.Damage / maxSrc, Fmt.Exact(s.Damage), fillKey: ThemeKeys.Negative), s => s.Damage),
            new("Share", 56, s => Ui.Number(Fmt.Percent((double)s.Damage / totalTaken)), s => s.Damage),
        };
        var table = new GridTable<SourceDamage>(cols, 4);
        table.SetItems(sources);
        _content.Children.Add(Ui.Card(table, "Incoming damage by source", subtitle: sources.Count == 0 ? "no damage taken" : $"{sources.Count} source/skill pairs"));
    }

    private static string SourceName(SourceDamage s, EncounterRecord r, IGameData gd)
    {
        if (!string.IsNullOrWhiteSpace(s.SourcePlayerName)) return s.SourcePlayerName!;
        if (s.SourceNpcCode is { } code) return gd.GetNpcName(code);
        var t = r.Targets.FirstOrDefault(x => x.EntityId == s.SourceEntityId);
        if (t?.NpcCode is { } tc) return gd.GetNpcName(tc);
        if (t?.PlayerName is { } pn) return pn;
        return ChartData.Find(r, s.SourceEntityId) is { } c ? ChartData.DisplayName(c) : "Entity " + s.SourceEntityId;
    }

    private static FrameworkElement SourceCell(SourceDamage s, EncounterRecord r, IGameData gd)
    {
        var sp = new DockPanel();
        bool player = !string.IsNullOrWhiteSpace(s.SourcePlayerName) || ChartData.Find(r, s.SourceEntityId) is not null;
        bool boss = s.SourceNpcCode is { } code && (code == r.BossNpcCode || gd.GetNpc(code)?.IsBoss == true);
        var badge = Ui.Badge(player ? "PLAYER" : boss ? "BOSS" : "NPC", player ? ThemeKeys.Negative : boss ? ThemeKeys.Warning : ThemeKeys.TextMuted);
        badge.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(badge, Dock.Left);
        sp.Children.Add(badge);
        sp.Children.Add(Ui.Text(SourceName(s, r, gd)));
        return sp;
    }

    // ───────────── Buffs tab ─────────────

    private void BuildBuffsTab(CombatantRecord c)
    {
        var gd = _gameData!;
        var r = _record!;
        var buffs = c.Buffs.OrderByDescending(b => b.Uptime).ToList();
        _content.Children.Add(Ui.TileGrid(4,
        [
            Ui.Tile("Buffs tracked", buffs.Count.ToString()),
            Ui.Tile("Average uptime", buffs.Count == 0 ? Fmt.Dash : Fmt.Percent(buffs.Average(b => b.Uptime)), "over " + Fmt.Duration(r.DurationSeconds)),
            Ui.Tile("Applications", buffs.Sum(b => b.Applications).ToString()),
            Ui.Tile("Self-applied", buffs.Count == 0 ? Fmt.Dash : buffs.Count(b => b.CasterEntityId == c.EntityId).ToString(), "of " + buffs.Count),
        ]));
        if (buffs.Count == 0)
        {
            _content.Children.Add(Ui.Card(Ui.Empty("No buff data was captured for this player."), "Buff uptime"));
            return;
        }
        var cols = new List<TableColumn<BuffUptime>>
        {
            new("Buff", -1, b => Ui.SkillCell(gd.GetSkillName(b.SkillId), b.SkillId, SkillVisuals.TileClass(gd, b.SkillId, CasterClass(b, r, c))), b => gd.GetSkillName(b.SkillId), HorizontalAlignment.Left),
            new("From", 140, b => Ui.Text(CasterName(b, r, c), 0, ThemeKeys.TextMuted), b => CasterName(b, r, c), HorizontalAlignment.Left),
            new("Uptime", 230, b => Ui.BarCell(Fmt.Percent(b.Uptime), b.Uptime, $"{Fmt.Percent(b.Uptime, 2)} of the fight", fillKey: ThemeKeys.Positive), b => b.Uptime),
            new("Active", 64, b => Ui.Number(Fmt.Duration(b.UptimeSeconds), Fmt.Seconds(b.UptimeSeconds)), b => b.UptimeSeconds),
            new("Apps", 54, b => Ui.Number(b.Applications.ToString(), $"{b.Applications} applications"), b => b.Applications, headerTip: "Applications"),
        };
        var table = new GridTable<BuffUptime>(cols, 2);
        table.SetItems(buffs);
        _content.Children.Add(Ui.Card(table, "Buff uptime", subtitle: "active time / fight time"));
    }

    private static string CasterName(BuffUptime b, EncounterRecord r, CombatantRecord self)
    {
        if (b.CasterEntityId is not { } id) return Fmt.Dash;
        if (id == self.EntityId) return "self";
        return ChartData.Find(r, id) is { } c ? ChartData.DisplayName(c) : "Entity " + id;
    }

    private static CharacterClass CasterClass(BuffUptime b, EncounterRecord r, CombatantRecord self) =>
        b.CasterEntityId is { } id && ChartData.Find(r, id) is { } c ? c.Class : self.Class;
}
