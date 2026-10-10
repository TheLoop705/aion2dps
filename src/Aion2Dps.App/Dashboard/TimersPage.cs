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

        var options = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        var t = T;
        var zones = (context.Timers?.Data.ServerTimeZones.Keys ?? []).Select(k => (k, $"{k} server ({context.Timers!.Data.ServerTimeZones[k]})")).ToList();
        if (zones.Count == 0) zones.Add(("EU", "EU server"));
        options.Children.Add(Ui.Field("Server clock", Ui.Combo(zones, t.ServerRegion, v => { T.ServerRegion = v; Save(); }, 280)));
        options.Children.Add(Spacer());
        options.Children.Add(Ui.Field("Alert before", Ui.Combo(new[] { (0, "Off"), (1, "1 min"), (3, "3 min"), (5, "5 min"), (10, "10 min"), (15, "15 min") },
            t.AlertsEnabled ? t.AlertMinutesBefore : 0, v => { T.AlertsEnabled = v > 0; if (v > 0) T.AlertMinutesBefore = v; Save(); }, 120)));
        var checks = new StackPanel { Margin = new Thickness(18, 22, 0, 0) };
        checks.Children.Add(Ui.Check("Alert when a starred boss respawns", t.AlertOnSpawn, v => { T.AlertOnSpawn = v; Save(); }));
        checks.Children.Add(Ui.Check("Show the next starred timer on the idle overlay", t.ShowNextOnOverlay, v => { T.ShowNextOnOverlay = v; Save(); }));
        options.Children.Add(checks);
        root.Children.Add(options);
        _clock.Margin = new Thickness(0, 0, 0, 10);
        root.Children.Add(_clock);

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

    public override string Key => "Timers";
    public override string Title => "Timers";
    public override string Icon => IconTimers;

    private TimerSettings T => Context.Settings.Current.Timers;

    private static FrameworkElement Spacer() => new Border { Width = 18 };

    private void Save() => Context.Settings.NotifyChanged();

    public override void Refresh()
    {
        var svc = Context.Timers;
        if (svc is null) return;
        var now = Context.Services.Now();
        var zone = svc.ServerZone;
        _clock.Text = $"Server time {TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(now, DateTimeKind.Utc), zone):ddd HH:mm:ss} · your time {DateTime.SpecifyKind(now, DateTimeKind.Utc).ToLocalTime():HH:mm:ss}";

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
            var e = svc.Data.Events.FirstOrDefault(x => TimerService.EventKey(x) == key);
            if (e is null) return;
            starred = svc.IsStarred(e);
            defaultValue = e.Favorite;
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

        public RowView(TimersPage page, string key)
        {
            _star = Ui.IconButton("", "Star: alert before this timer", () => page.ToggleStar(key), 12);
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
                "Open" or "Up" or "Running" => ThemeKeys.Positive,
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
