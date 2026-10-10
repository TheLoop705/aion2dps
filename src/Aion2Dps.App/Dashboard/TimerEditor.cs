using Aion2Dps.App.Controls;
using Aion2Dps.App.Settings;
using Aion2Dps.App.Timers;
using Aion2Dps.Combat.Timers;

namespace Aion2Dps.App.Dashboard;

/// <summary>
/// Settings → Timers: server clock and alerts, the built-in timers (change time / repeat / days / name, hide, reset) and
/// your own timers: a server-time schedule or a countdown you start from the Timers page. Edits save immediately; a time
/// that is not "HH:mm" is marked and not used until it is.
/// </summary>
public sealed class TimerEditor : StackPanel
{
    private static readonly (int Minutes, string Label)[] Repeats =
    [
        (0, "Once a day"), (30, "Every 30 min"), (60, "Every hour"), (120, "Every 2 h"), (180, "Every 3 h"), (240, "Every 4 h"),
        (360, "Every 6 h"), (480, "Every 8 h"), (720, "Every 12 h"),
    ];

    private static readonly DayOfWeek[] Week =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    private readonly DashboardContext _context;
    private readonly StackPanel _builtIn = new();
    private readonly StackPanel _own = new();

    public TimerEditor(DashboardContext context)
    {
        _context = context;
        var t = T;

        var options = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        var data = context.Timers?.Data;
        var zones = (data?.ServerTimeZones.Keys ?? []).Select(k => (k, $"{k} server ({data!.ServerTimeZones[k]})")).ToList();
        if (zones.Count == 0) zones.Add(("EU", "EU server"));
        options.Children.Add(Ui.Field("Server clock", Ui.Combo(zones, t.ServerRegion, v => { T.ServerRegion = v; Save(); }, 260)));
        options.Children.Add(new Border { Width = 16 });
        options.Children.Add(Ui.Field("Alert before", Ui.Combo(new[] { (0, "Off"), (1, "1 min"), (3, "3 min"), (5, "5 min"), (10, "10 min"), (15, "15 min") },
            t.AlertsEnabled ? t.AlertMinutesBefore : 0, v => { T.AlertsEnabled = v > 0; if (v > 0) T.AlertMinutesBefore = v; Save(); }, 120)));
        var checks = new StackPanel { Margin = new Thickness(16, 22, 0, 0) };
        checks.Children.Add(Ui.Check("Alert when a starred boss respawns or a countdown ends", t.AlertOnSpawn, v => { T.AlertOnSpawn = v; Save(); }));
        checks.Children.Add(Ui.Check("Show the timers window next to the overlay (always, also in combat)", t.ShowTimerWindow, v => { T.ShowTimerWindow = v; Save(); }));
        options.Children.Add(checks);
        options.Children.Add(new Border { Width = 16 });
        options.Children.Add(Ui.Field("Timers window looks ahead", Ui.Combo(new[] { (30, "30 min"), (60, "1 hour"), (120, "2 hours"), (180, "3 hours") },
            t.UpcomingMinutes, v => { T.UpcomingMinutes = v; Save(); }, 120)));
        Children.Add(options);

        Children.Add(Heading("Your timers", "Times are server time (the clock above). Leave all days off for every day."));
        Children.Add(_own);
        var add = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        add.Children.Add(Ui.Button("Add timer", AddSchedule, icon: ""));
        add.Children.Add(Ui.Button("Add countdown", AddCountdown, icon: ""));
        Children.Add(add);

        Children.Add(Heading("Built-in timers", "Change a time, repeat or days if your server differs; untick to hide."));
        Children.Add(_builtIn);
        Rebuild();
    }

    private TimerSettings T => _context.Settings.Current.Timers;

    private void Save() => _context.Settings.NotifyChanged();

    private static StackPanel Heading(string title, string hint)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 14, 0, 6) };
        sp.Children.Add(Ui.Text(title, ThemeKeys.Text, 13, FontWeights.SemiBold));
        var h = Ui.Text(hint, ThemeKeys.TextMuted, 11.5);
        h.TextWrapping = TextWrapping.Wrap;
        sp.Children.Add(h);
        return sp;
    }

    /// <summary>Rebuilds both lists (after add / delete / reset).</summary>
    public void Rebuild()
    {
        _own.Children.Clear();
        foreach (var e in T.Entries.Where(x => x.Custom).ToList()) _own.Children.Add(OwnRow(e));
        if (_own.Children.Count == 0)
            _own.Children.Add(Ui.Text("None yet. Add a daily timer (e.g. your guild's boss run) or a countdown (e.g. a respawn you track by hand).",
                ThemeKeys.TextMuted, 12));

        _builtIn.Children.Clear();
        foreach (var e in _context.Timers?.Data.Events ?? []) _builtIn.Children.Add(BuiltInRow(e));
    }

    private void AddSchedule()
    {
        T.Entries.Add(new TimerEntry { Id = "custom-" + Guid.NewGuid().ToString("N")[..8], Custom = true, Name = "My timer", Start = "20:00" });
        Save();
        Rebuild();
    }

    private void AddCountdown()
    {
        T.Entries.Add(new TimerEntry { Id = "custom-" + Guid.NewGuid().ToString("N")[..8], Custom = true, Name = "Respawn", CountdownMinutes = 60 });
        Save();
        Rebuild();
    }

    // ───────────────────────────── rows ─────────────────────────────

    private FrameworkElement OwnRow(TimerEntry e)
    {
        var row = Row();
        row.Children.Add(Ui.Check("", e.Enabled, v => { e.Enabled = v; Save(); }, "Shown on the Timers page"));
        row.Children.Add(NameBox(e.Name ?? "", v => { e.Name = v; Save(); }));
        if (e.IsCountdown)
        {
            row.Children.Add(Label("Countdown"));
            row.Children.Add(NumberBox(e.CountdownMinutes ?? 60, 1, 60 * 24 * 7, v => { e.CountdownMinutes = v; Save(); }, "Minutes"));
            row.Children.Add(Label("min · start it on the Timers page"));
        }
        else
        {
            AddScheduleFields(row, e.Start ?? "", e.EveryMinutes ?? 0, e.Days ?? [],
                v => { e.Start = v; Save(); }, v => { e.EveryMinutes = v; Save(); }, v => { e.Days = v; Save(); });
        }
        row.Children.Add(Ui.IconButton(Ui.IconClose, "Delete this timer", () =>
        {
            T.Entries.Remove(e);
            T.Stars.Remove("event:" + e.Id);
            Save();
            Rebuild();
        }, 11));
        return row;
    }

    private FrameworkElement BuiltInRow(ScheduledEvent def)
    {
        var o = T.Entries.FirstOrDefault(x => !x.Custom && x.Id == def.Id);
        var eff = o is null ? def : TimerService.Apply(def, o);
        var reset = Ui.IconButton(Ui.IconReset, "Back to the built-in time", () =>
        {
            T.Entries.RemoveAll(x => !x.Custom && x.Id == def.Id);
            Save();
            Rebuild();
        }, 11);
        reset.Visibility = o is null ? Visibility.Hidden : Visibility.Visible;
        TimerEntry Override()
        {
            var cur = T.Entries.FirstOrDefault(x => !x.Custom && x.Id == def.Id);
            if (cur is null)
            {
                cur = new TimerEntry { Id = def.Id };
                T.Entries.Add(cur);
            }
            reset.Visibility = Visibility.Visible;
            return cur;
        }

        var row = Row();
        row.Children.Add(Ui.Check("", o?.Enabled ?? true, v => { Override().Enabled = v; Save(); }, "Shown on the Timers page"));
        row.Children.Add(NameBox(eff.Name, v => { Override().Name = v == def.Name ? null : v; Save(); }));
        AddScheduleFields(row, eff.Start.ToString(@"hh\:mm"), eff.EveryMinutes, eff.Days,
            v => { Override().Start = v; Save(); }, v => { Override().EveryMinutes = v; Save(); }, v => { Override().Days = v; Save(); });
        row.Children.Add(reset);
        return row;
    }

    private static void AddScheduleFields(WrapPanel row, string start, int every, IReadOnlyList<DayOfWeek> days,
        Action<string> setStart, Action<int> setEvery, Action<List<DayOfWeek>?> setDays)
    {
        row.Children.Add(Label("at"));
        var time = new TextBox { Text = start, Width = 56, ToolTip = "Server time, HH:mm", VerticalContentAlignment = VerticalAlignment.Center };
        void Validate()
        {
            bool ok = TimerData.TryParseTime(time.Text, out _);
            time.Ref(Control.BorderBrushProperty, ok ? ThemeKeys.Border : ThemeKeys.Negative);
        }
        time.TextChanged += (_, _) =>
        {
            Validate();
            if (TimerData.TryParseTime(time.Text, out var at)) setStart(at.ToString(@"hh\:mm"));
        };
        Validate();
        row.Children.Add(time);
        var repeats = Repeats.Any(r => r.Minutes == every) ? Repeats : [.. Repeats, (every, $"Every {every} min")];
        row.Children.Add(Ui.Combo(repeats, every, setEvery, 130));
        var dayPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
        var selected = new HashSet<DayOfWeek>(days);
        foreach (var d in Week)
        {
            var c = new CheckBox { Content = d.ToString()[..2], IsChecked = selected.Contains(d), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            c.Checked += (_, _) => { selected.Add(d); setDays(Week.Where(selected.Contains).ToList()); };
            c.Unchecked += (_, _) => { selected.Remove(d); setDays(selected.Count == 0 ? [] : Week.Where(selected.Contains).ToList()); };
            dayPanel.Children.Add(c);
        }
        row.Children.Add(dayPanel);
    }

    private static WrapPanel Row() => new() { Margin = new Thickness(0, 3, 0, 3), VerticalAlignment = VerticalAlignment.Center };

    private static TextBlock Label(string text)
    {
        var t = Ui.Text(text, ThemeKeys.TextMuted, 12);
        t.VerticalAlignment = VerticalAlignment.Center;
        t.Margin = new Thickness(6, 0, 6, 0);
        return t;
    }

    private static TextBox NameBox(string value, Action<string> onChanged)
    {
        var box = new TextBox { Text = value, Width = 220, VerticalContentAlignment = VerticalAlignment.Center, MaxLength = 60 };
        box.TextChanged += (_, _) => { if (!string.IsNullOrWhiteSpace(box.Text)) onChanged(box.Text.Trim()); };
        return box;
    }

    private static TextBox NumberBox(int value, int min, int max, Action<int> onChanged, string tooltip)
    {
        var box = new TextBox { Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture), Width = 60, ToolTip = tooltip,
            VerticalContentAlignment = VerticalAlignment.Center };
        box.TextChanged += (_, _) =>
        {
            bool ok = int.TryParse(box.Text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int v)
                && v >= min && v <= max;
            box.Ref(Control.BorderBrushProperty, ok ? ThemeKeys.Border : ThemeKeys.Negative);
            if (ok) onChanged(v);
        };
        return box;
    }
}
