using Aion2Dps.App.Controls;
using Aion2Dps.App.Settings;
using Aion2Dps.App.Timers;
using Aion2Dps.Combat.Timers;

namespace Aion2Dps.App.Dashboard;

/// <summary>
/// Rift / event countdowns (server schedule) and field-boss respawn timers (the game's own field-boss list, so they are
/// exact while the meter captures). Star a timer for tray alerts and the overlay's "next" line.
/// </summary>
public sealed class TimersPage : DashboardPage
{
    public const string IconTimers = "";

    private readonly StackPanel _events = new();
    private readonly StackPanel _bosses = new();
    private readonly TextBlock _clock = Ui.Text("", ThemeKeys.TextMuted, 12);
    private readonly TextBlock _bossHint = Ui.Text("", ThemeKeys.TextMuted, 12);
    private readonly Dictionary<string, RowView> _rows = new(StringComparer.Ordinal);
    private string _eventLayout = "", _bossLayout = "";

    public TimersPage(DashboardContext context) : base(context)
    {
        var root = new StackPanel();
        root.Children.Add(Ui.PageTitle("Timers"));
        root.Children.Add(Ui.PageSubtitle("Spacetime Rifts, sieges and resets on the server clock, and field-boss respawns read live from the game. " +
                                          "Star a timer to get a tray alert before it starts."));

        var t = T;
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = true };
        var edit = Ui.Button("Edit timers", () => Context.Navigate?.Invoke("Settings"), icon: Ui.IconSettings);
        edit.ToolTip = "Add your own timers and countdowns, change or hide the built-in ones, server clock and alerts (Settings → Timers)";
        DockPanel.SetDock(edit, Dock.Right);
        bar.Children.Add(edit);
        _clock.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(_clock);
        root.Children.Add(bar);

        root.Children.Add(Ui.Card("Add a timer", BuildQuickAdd(),
            "A boss or event the game does not list (e.g. an Abyss field boss): when it is due, and its respawn if it comes back."));
        root.Children.Add(Ui.Card("Events", _events));
        var bossBody = new StackPanel();
        var bossBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        bossBar.Children.Add(Ui.Check("Starred only", t.StarredBossesOnly, v => { T.StarredBossesOnly = v; Save(); _bossLayout = ""; Refresh(); }));
        bossBody.Children.Add(bossBar);
        _bossHint.TextWrapping = TextWrapping.Wrap;
        _bossHint.Margin = new Thickness(0, 0, 0, 8);
        bossBody.Children.Add(_bossHint);
        bossBody.Children.Add(_bosses);
        root.Children.Add(Ui.Card("Field bosses", bossBody,
            "Respawn times come from the game's field-boss list (every few seconds while you play) and are kept across restarts. " +
            "Priority bosses (Gartua first, then Dartan, Kashapa, Lagta and Lawa) and the scheduled Abyss bosses are starred by default."));
        Content = root;
    }

    private static readonly (int Minutes, string Label)[] RespawnChoices =
    [
        (0, "No respawn (one-off)"), (30, "Respawns 30 min"), (60, "Respawns 1 h"), (90, "Respawns 1 h 30"), (120, "Respawns 2 h"),
        (180, "Respawns 3 h"), (240, "Respawns 4 h"), (360, "Respawns 6 h"), (480, "Respawns 8 h"), (720, "Respawns 12 h"), (1440, "Respawns 24 h"),
    ];

    /// <summary>Name · "spawns in" · respawn · repeat · Add. Adds a countdown due from now (Settings → Timers edits it).</summary>
    private FrameworkElement BuildQuickAdd()
    {
        var name = new TextBox { Width = 220, MaxLength = 60, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Boss or event name" };
        var dueIn = new TextBox { Width = 70, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Minutes (28) or hours:minutes (1:30, 2h)" };
        int respawn = 0;
        var repeat = Ui.Check("Repeats on its own", false, _ => { }, "On a fixed cycle: starts the next respawn by itself instead of waiting for Killed");
        repeat.IsEnabled = false;
        repeat.VerticalAlignment = VerticalAlignment.Center;
        var respawnBox = Ui.Combo(RespawnChoices, 0, v => { respawn = v; repeat.IsEnabled = v > 0; }, 170);
        var error = Ui.Text("", ThemeKeys.Negative, 11.5);
        error.VerticalAlignment = VerticalAlignment.Center;
        dueIn.TextChanged += (_, _) =>
        {
            bool ok = dueIn.Text.Length == 0 || TimerService.TryParseDuration(dueIn.Text, out _);
            dueIn.Ref(Control.BorderBrushProperty, ok ? ThemeKeys.Border : ThemeKeys.Negative);
        };
        dueIn.Ref(Control.BorderBrushProperty, ThemeKeys.Border);

        void Add()
        {
            var svc = Context.Timers;
            if (svc is null) return;
            if (!TimerService.TryParseDuration(dueIn.Text, out int minutes))
            {
                error.Text = "Enter when it spawns: minutes (28) or h:mm (1:30).";
                return;
            }
            if (svc.AddCountdown(name.Text, minutes, respawn, repeat.IsChecked == true, Context.Services.Now()) is null)
            {
                error.Text = "Enter a time above 0.";
                return;
            }
            error.Text = "";
            name.Text = "";
            dueIn.Text = "";
            Save();
            _eventLayout = "";
            Refresh();
        }
        dueIn.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Add(); };
        name.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Add(); };

        var row = new WrapPanel();
        row.Children.Add(name);
        row.Children.Add(Label("spawns in"));
        row.Children.Add(dueIn);
        row.Children.Add(Label("min"));
        respawnBox.Margin = new Thickness(6, 0, 10, 0);
        row.Children.Add(respawnBox);
        row.Children.Add(repeat);
        var add = Ui.Button("Add", Add, accent: true, icon: "");
        add.Margin = new Thickness(12, 0, 10, 0);
        row.Children.Add(add);
        row.Children.Add(error);
        return row;

        static TextBlock Label(string text)
        {
            var t = Ui.Text(text, ThemeKeys.TextMuted, 12);
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(8, 0, 6, 0);
            return t;
        }
    }

    public override string Key => "Timers";
    public override string Title => "Timers";
    public override string Icon => IconTimers;

    private TimerSettings T => Context.Settings.Current.Timers;

    private void Save() => Context.Settings.NotifyChanged();

    public override void Refresh()
    {
        var svc = Context.Timers;
        if (svc is null) return;
        var now = Context.Services.Now();
        var zone = svc.ServerZone;
        _clock.Text = $"{(T.AlertsEnabled ? $"Alerts {T.AlertMinutesBefore} min before" : "Alerts off")} · server time {TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(now, DateTimeKind.Utc), zone):ddd HH:mm:ss} · your time {DateTime.SpecifyKind(now, DateTimeKind.Utc).ToLocalTime():HH:mm:ss}";

        var events = svc.EventRows(now);
        Fill(_events, events, ref _eventLayout, now, grouped: false);

        var bosses = svc.BossRows(now);
        if (T.StarredBossesOnly) bosses = bosses.Where(b => b.Starred).ToList();
        bosses = bosses.OrderBy(b => b.Group).ThenByDescending(b => b.Priority).ThenBy(b => b.Live ? 1 : 0).ThenBy(b => b.AtUtc ?? DateTime.MaxValue)
            .ThenByDescending(b => b.Interval ?? TimeSpan.Zero).ToList();
        _bossHint.Text = svc.Book.Version == 0 && bosses.Count == 0
            ? "No field-boss list received yet. Log in (or zone) with the meter running: the game sends it within a few seconds."
            : "";
        _bossHint.Visibility = _bossHint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Fill(_bosses, bosses, ref _bossLayout, now, grouped: true);
    }

    private void Fill(StackPanel host, IReadOnlyList<TimerRow> rows, ref string layout, DateTime now, bool grouped)
    {
        string key = string.Join("|", rows.Select(r => r.Key));
        if (key != layout)
        {
            layout = key;
            host.Children.Clear();
            string? group = null;
            foreach (var r in rows)
            {
                if (grouped && r.Group != group)
                {
                    group = r.Group;
                    var h = Ui.Text(group.ToUpperInvariant(), ThemeKeys.TextMuted, 10.5, FontWeights.SemiBold);
                    h.Margin = new Thickness(0, host.Children.Count == 0 ? 0 : 12, 0, 4);
                    host.Children.Add(h);
                }
                if (!_rows.TryGetValue(r.Key, out var view))
                {
                    view = new RowView(this, r.Key);
                    _rows[r.Key] = view;
                }
                if (view.Root.Parent is Panel p) p.Children.Remove(view.Root);
                host.Children.Add(view.Root);
            }
        }
        foreach (var r in rows) _rows[r.Key].Update(r, now);
    }

    private void ToggleStar(string key)
    {
        var svc = Context.Timers;
        if (svc is null) return;
        bool starred, defaultValue;
        if (key.StartsWith("event:", StringComparison.Ordinal))
        {
            var e = svc.Events().FirstOrDefault(x => TimerService.EventKey(x) == key);
            var c = svc.Countdowns().FirstOrDefault(x => TimerService.CountdownKey(x) == key);
            if (e is not null)
            {
                starred = svc.IsStarred(e);
                defaultValue = e.Favorite;
            }
            else if (c is not null)
            {
                starred = svc.IsStarred(c);
                defaultValue = true;
            }
            else return;
        }
        else
        {
            var b = svc.Book.Snapshot().FirstOrDefault(x => TimerService.BossKey(x) == key);
            if (b is null) return;
            starred = svc.IsStarred(b);
            defaultValue = b.IsImportant;
        }
        svc.SetStar(key, !starred, defaultValue);
        Save();
        _bossLayout = "";
        Refresh();
    }

    private void ToggleCountdown(string key, bool running)
    {
        var svc = Context.Timers;
        if (svc is null) return;
        if (running ? svc.StopCountdown(key) : svc.StartCountdown(key, Context.Services.Now())) Save();
        Refresh();
    }

    private void DeleteCountdown(string key)
    {
        var svc = Context.Timers;
        if (svc is null || !svc.RemoveCountdown(key)) return;
        Save();
        _eventLayout = "";
        Refresh();
    }

    /// <summary>One timer line: star · name (+ detail) · status pill · countdown · local clock.</summary>
    private sealed class RowView
    {
        private readonly Button _star;
        private readonly TextBlock _name = Ui.Text("", ThemeKeys.Text, 13, FontWeights.SemiBold);
        private readonly TextBlock _detail = Ui.Text("", ThemeKeys.TextMuted, 11);
        private readonly TextBlock _status = Ui.Text("", ThemeKeys.TextMuted, 11.5, FontWeights.SemiBold);
        private readonly TextBlock _left = Ui.Text("", ThemeKeys.Text, 14, FontWeights.SemiBold, mono: true);
        private readonly TextBlock _at = Ui.Text("", ThemeKeys.TextMuted, 11.5);
        private readonly Border _badge = Ui.Pill("", ThemeKeys.Warning, ThemeKeys.AccentText, 9);
        private readonly Button _countdown;
        private readonly Button _delete;
        private bool _counting;

        public RowView(TimersPage page, string key)
        {
            _star = Ui.IconButton("", "Star: alert before this timer", () => page.ToggleStar(key), 12);
            _countdown = Ui.Chip("Start", "Start the countdown now", () => page.ToggleCountdown(key, _counting));
            _countdown.Margin = new Thickness(10, 0, 0, 0);
            _countdown.Visibility = Visibility.Collapsed;
            _delete = Ui.IconButton(Ui.IconClose, "Delete this timer", () => page.DeleteCountdown(key), 10);
            _delete.Margin = new Thickness(4, 0, 0, 0);
            _delete.Visibility = Visibility.Collapsed;
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            g.Children.Add(_star);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(_name);
            _badge.Margin = new Thickness(8, 1, 0, 0);
            title.Children.Add(_badge);
            title.Children.Add(_countdown);
            title.Children.Add(_delete);
            text.Children.Add(title);
            text.Children.Add(_detail);
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            _status.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_status, 2);
            g.Children.Add(_status);
            _left.VerticalAlignment = VerticalAlignment.Center;
            _left.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(_left, 3);
            g.Children.Add(_left);
            _at.VerticalAlignment = VerticalAlignment.Center;
            _at.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(_at, 4);
            g.Children.Add(_at);
            Root = g;
        }

        public Grid Root { get; }

        public void Update(TimerRow r, DateTime now)
        {
            _star.Content = r.Starred ? "" : "";
            _star.Ref(Control.ForegroundProperty, r.Starred ? ThemeKeys.Warning : ThemeKeys.TextMuted);
            _name.Text = r.Name;
            _counting = r.Status == "Counting";
            _countdown.Visibility = r.IsCountdown ? Visibility.Visible : Visibility.Collapsed;
            _delete.Visibility = _countdown.Visibility;
            bool respawn = r.IsCountdown && r.Interval is not null;
            _countdown.Content = _counting ? "Stop" : respawn && r.Status == "Up" ? "Killed" : r.Status == "Done" ? "Restart" : "Start";
            _countdown.ToolTip = _counting ? "Stop the countdown" : respawn
                ? $"It died now: count its {TimerService.FormatInterval(r.Interval!.Value)} respawn from now"
                : "Start the countdown now";
            _badge.Visibility = r.Priority == TimerPriority.None ? Visibility.Collapsed : Visibility.Visible;
            ((TextBlock)_badge.Child).Text = r.Priority switch { TimerPriority.Top => "TOP PRIORITY", TimerPriority.High => "HIGH", _ => "MEDIUM" };
            _badge.SetResourceReference(Border.BackgroundProperty, r.Priority switch
            {
                TimerPriority.Top => ThemeKeys.Negative,
                TimerPriority.High => ThemeKeys.Warning,
                _ => ThemeKeys.TextMuted,
            });
            _detail.Text = r.Detail ?? "";
            _detail.Visibility = string.IsNullOrEmpty(r.Detail) ? Visibility.Collapsed : Visibility.Visible;
            _status.Text = r.Status;
            _status.Ref(TextBlock.ForegroundProperty, r.Status switch
            {
                "Open" or "Up" or "Running" or "Done" => ThemeKeys.Positive,
                "Counting" => ThemeKeys.Accent,
                "Probably up" => ThemeKeys.Warning,
                _ => ThemeKeys.TextMuted,
            });
            if (r.AtUtc is DateTime at)
            {
                var left = at - now;
                _left.Text = TimerService.FormatLeft(left);
                _left.Ref(TextBlock.ForegroundProperty, left <= TimeSpan.FromMinutes(5) ? ThemeKeys.Warning : ThemeKeys.Text);
                _at.Text = (r.Live ? "until " : "") + TimerService.LocalClock(at);
            }
            else
            {
                _left.Text = r.Live ? "now" : "–";
                _left.Ref(TextBlock.ForegroundProperty, r.Live ? ThemeKeys.Positive : ThemeKeys.TextMuted);
                _at.Text = "";
            }
        }
    }
}
