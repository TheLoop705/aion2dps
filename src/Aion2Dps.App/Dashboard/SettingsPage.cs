using Aion2Dps.App.Controls;
using Aion2Dps.App.Infrastructure;
using Aion2Dps.App.Settings;

namespace Aion2Dps.App.Dashboard;

/// <summary>Language, network adapter, meter behaviour, idle timeouts, filters, hotkeys, folders, start-up options and timers.</summary>
public sealed class SettingsPage : DashboardPage
{
    private readonly StackPanel _hotkeys = new();
    private readonly CheckBox _autostartCheck;
    private readonly TextBlock _autostartError;
    private readonly TextBlock _autostartOffInWindows;
    private bool _syncingAutostart;

    public SettingsPage(DashboardContext context) : base(context)
    {
        var root = new StackPanel();
        root.Children.Add(Ui.PageTitle("Settings"));
        root.Children.Add(Ui.PageSubtitle("Saved automatically to %APPDATA%\\Aion2Dps\\settings.json."));

        var cols = new Grid();
        cols.ColumnDefinitions.Add(new ColumnDefinition());
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        cols.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel();
        var right = new StackPanel();
        var g = context.Settings.Current.General;
        var o = context.Settings.Current.Overlay;

        // Game data & capture
        var data = new StackPanel();
        data.Children.Add(Ui.Field("Game data language", Ui.Combo(new[]
        {
            (GameLanguage.English, "English"), (GameLanguage.Korean, "한국어 (Korean)"),
            (GameLanguage.ChineseSimplified, "简体中文 (Simplified Chinese)"), (GameLanguage.ChineseTraditional, "繁體中文 (Traditional Chinese)"),
        }, g.Language, v => { g.Language = v; Save(); }, 260), "Names of skills, bosses, dungeons and classes."));
        var adapters = new List<(string?, string)> { (null, "Automatic (follow the game connection)") };
        try
        {
            adapters.AddRange(context.Services.Capture.GetAdapters().Select(a =>
                ((string?)a.Name, $"{a.Description}{(a.IPv4Addresses.Count > 0 ? "  ·  " + string.Join(", ", a.IPv4Addresses) : "")}")));
        }
        catch (Exception ex) { AppLog.Warn("Settings", $"Adapter list failed: {ex.Message}"); }
        var adapterCombo = Ui.Combo(adapters, g.AdapterOverride, v => { g.AdapterOverride = v; Save(); }, 360);
        var adapterRow = new StackPanel();
        adapterRow.Children.Add(adapterCombo);
        var restart = Ui.Button("Apply (restart capture)", () => context.RestartCapture(), icon: Ui.IconReset);
        restart.Margin = new Thickness(0, 8, 0, 0);
        restart.HorizontalAlignment = HorizontalAlignment.Left;
        adapterRow.Children.Add(restart);
        data.Children.Add(Ui.Field("Network adapter", adapterRow, "Automatic works for most setups, including gaming VPN / ping-reducer adapters."));
        left.Children.Add(Ui.Card("Game data & capture", data));

        // Meter behaviour
        var meter = new StackPanel();
        var clock = new StackPanel();
        var own = new RadioButton { Content = "Each player's own clock (late joiners not penalised)", GroupName = "clock", IsChecked = g.LivePlayerClock };
        var shared = new RadioButton { Content = "Shared encounter clock", GroupName = "clock", IsChecked = !g.LivePlayerClock };
        own.Checked += (_, _) => { g.LivePlayerClock = true; Save(); };
        shared.Checked += (_, _) => { g.LivePlayerClock = false; Save(); };
        clock.Children.Add(own);
        clock.Children.Add(shared);
        meter.Children.Add(Ui.Field("Live DPS clock", clock, "Saved fights always use the whole-fight clock so runs stay comparable."));
        var barMode = new StackPanel();
        var rel = new RadioButton { Content = "Relative to the top player", GroupName = "barmode", IsChecked = o.BarMode == BarMode.RelativeToTop };
        var share = new RadioButton { Content = "Share of party damage", GroupName = "barmode", IsChecked = o.BarMode == BarMode.ShareOfParty };
        rel.Checked += (_, _) => { o.BarMode = BarMode.RelativeToTop; Save(); };
        share.Checked += (_, _) => { o.BarMode = BarMode.ShareOfParty; Save(); };
        barMode.Children.Add(rel);
        barMode.Children.Add(share);
        meter.Children.Add(Ui.Field("Bar fill", barMode));
        meter.Children.Add(Ui.Field("Default mode", Ui.Combo(new[] { (MeterMode.BossOnly, "Boss only"), (MeterMode.AllTargets, "All targets") }, o.DefaultMode,
            v => { o.DefaultMode = v; context.Services.Engine.Mode = v; Save(); }, 200)));
        meter.Children.Add(Ui.Field("Rows shown", SliderWithLabel(1, 24, o.MaxRows, 1, v => $"{v:0}", v => { o.MaxRows = (int)v; Save(); }),
            "The top of the ranking. When you are not among them, the last row is yours with your real rank."));
        meter.Children.Add(Ui.Field("Shrink a finished fight to the slim bar after", SliderWithLabel(0, 120, g.EndedDisplaySeconds, 5,
            v => v <= 0 ? $"{Overlay.OverlayPresentationPolicy.DefaultLingerSeconds:0} s" : $"{v:0} s", v => { g.EndedDisplaySeconds = v; Save(); }),
            "A finished fight is never cleared: it stays until the next fight starts. After this delay the overlay shrinks to the slim bar; click the bar to see the result again."));
        meter.Children.Add(Ui.Check("Only my group inside instances too (outside instances the meter always ranks just you, your party or your force)",
            g.PartyOnly, v => { g.PartyOnly = v; Save(); }));
        meter.Children.Add(Ui.Check("Track boss fights only (ignore trash mobs)", g.BossFightsOnly, v => { g.BossFightsOnly = v; Save(); }));
        var bossOnlyHint = Ui.Text("Trash mobs never start or replace a fight on the meter: it waits for a boss, and a finished boss fight stays up " +
                                   "until the next boss is engaged. Training runs, dummies and PvP still count.",
            ThemeKeys.TextMuted, 11.5);
        bossOnlyHint.TextWrapping = TextWrapping.Wrap;
        bossOnlyHint.Margin = new Thickness(24, 2, 0, 6);
        meter.Children.Add(bossOnlyHint);
        meter.Children.Add(Ui.Check("Save trash fights to history", g.SaveTrashFights, v => { g.SaveTrashFights = v; Save(); }));
        left.Children.Add(Ui.Card("Meter", meter));

        // Idle timeouts
        var idle = new StackPanel();
        idle.Children.Add(Ui.Field("End trash encounters after", SliderWithLabel(3, 60, g.IdleTimeoutSeconds, 1, v => $"{v:0} s without damage", v => { g.IdleTimeoutSeconds = v; Save(); })));
        idle.Children.Add(Ui.Field("End boss encounters after", SliderWithLabel(5, 120, g.BossIdleTimeoutSeconds, 1, v => $"{v:0} s without damage", v => { g.BossIdleTimeoutSeconds = v; Save(); }),
            "A boss returning to full HP always starts a new attempt (wipe)."));
        left.Children.Add(Ui.Card("Idle timeouts", idle));

        // Overlay
        var overlay = new StackPanel();
        overlay.Children.Add(Ui.Check("Bars only (no header, footer or box; click a bar for details, drag a bar to move)", o.BarsOnly, v => { o.BarsOnly = v; Save(); }));
        overlay.Children.Add(Ui.Check("Shrink overlay when not in combat", o.ShrinkWhenIdle, v => { o.ShrinkWhenIdle = v; Save(); }));
        var shrinkHint = Ui.Text("Out of combat the overlay becomes a slim status bar. It expands for every fight (also PvP and training runs), " +
                                 "keeps the result readable for the \"Shrink a finished fight\" time, then shrinks to the bar, which keeps the result. " +
                                 "Click the bar to expand it until the next fight.", ThemeKeys.TextMuted, 11.5);
        shrinkHint.TextWrapping = TextWrapping.Wrap;
        shrinkHint.Margin = new Thickness(24, 2, 0, 0);
        overlay.Children.Add(shrinkHint);
        right.Children.Add(Ui.Card("Overlay", overlay));

        // Hotkeys
        right.Children.Add(Ui.Card("Hotkeys", _hotkeys, "Global hotkeys. Some games swallow them while focused; the overlay buttons always work."));
        right.Children.Add(Ui.Card("Hotkey options", Ui.Check("Enable global hotkeys (restart to apply)", g.EnableGlobalHotkeys, v => { g.EnableGlobalHotkeys = v; Save(); })));

        // Folders
        var folders = new StackPanel();
        folders.Children.Add(FolderRow("Settings, logs & history", AppPaths.DataDirectory));
        folders.Children.Add(FolderRow("Recordings", context.CaptureFolder));
        right.Children.Add(Ui.Card("Data folders", folders));

        // Startup
        var startup = new StackPanel();
        _autostartCheck = Ui.Check("Start Aion2Dps with Windows (in the tray)", false, OnAutostartToggled);
        _autostartError = Ui.Text("", ThemeKeys.Negative, 11.5);
        _autostartError.TextWrapping = TextWrapping.Wrap;
        _autostartError.Margin = new Thickness(24, 2, 0, 0);
        _autostartError.Visibility = Visibility.Collapsed;
        _autostartOffInWindows = Hint("Switched off in Windows (Task Manager > Startup apps). Tick the box to switch it on again.");
        _autostartOffInWindows.Visibility = Visibility.Collapsed;
        SyncAutostart();
        startup.Children.Add(_autostartCheck);
        startup.Children.Add(Hint("Starts quietly when you sign in to Windows: no dashboard window, just the tray icon. " +
                                  "Nothing is changed in Steam or the game."));
        startup.Children.Add(_autostartOffInWindows);
        startup.Children.Add(_autostartError);
        var whileGame = Ui.Check("Show the overlay only while AION 2 is running", g.ShowOverlayOnlyWhileGameRuns,
            v => { g.ShowOverlayOnlyWhileGameRuns = v; Save(); });
        whileGame.Margin = new Thickness(0, 8, 0, 0);
        startup.Children.Add(whileGame);
        startup.Children.Add(Hint("The overlay appears when the game starts (also from Steam) and hides a few seconds after it closes. " +
                                  "If you hide it while playing, it stays hidden until the next game start."));
        var onStart = Ui.Check("Show the overlay on start (or when AION 2 starts)", g.LaunchOverlayOnStart, v => { g.LaunchOverlayOnStart = v; Save(); });
        onStart.Margin = new Thickness(0, 8, 0, 0);
        startup.Children.Add(onStart);
        startup.Children.Add(Ui.Check("Open the dashboard on start (not when started with Windows)", g.OpenDashboardOnStart, v => { g.OpenDashboardOnStart = v; Save(); }));
        right.Children.Add(Ui.Card("Startup", startup));

        cols.Children.Add(left);
        Grid.SetColumn(right, 2);
        cols.Children.Add(right);
        root.Children.Add(cols);
        if (context.Timers is not null)
            root.Children.Add(Ui.Card("Timers", new TimerEditor(context),
                "Your own timers and countdowns, and the built-in rift / siege / reset times. Star and watch them on the Timers page."));
        Content = root;
    }

    public override string Key => "Settings";
    public override string Title => "Settings";
    public override string Icon => Ui.IconSettings;

    private static TextBlock Hint(string text)
    {
        var t = Ui.Text(text, ThemeKeys.TextMuted, 11.5);
        t.TextWrapping = TextWrapping.Wrap;
        t.Margin = new Thickness(24, 2, 0, 0);
        return t;
    }

    /// <summary>The checkbox mirrors the registry (the installer or another copy may have changed it).</summary>
    private void SyncAutostart()
    {
        _syncingAutostart = true;
        try
        {
            if (Context.Autostart is not { } reg)
            {
                _autostartCheck.IsChecked = false;
                _autostartCheck.IsEnabled = false;
                _autostartCheck.ToolTip = "Not available in this mode.";
                return;
            }
            bool offInWindows = false;
            try
            {
                _autostartCheck.IsChecked = reg.IsEnabled();
                offInWindows = reg.IsDisabledByWindows();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Settings", $"Could not read the start-with-Windows entry: {ex.Message}");
                _autostartCheck.IsChecked = false;
            }
            _autostartOffInWindows.Visibility = offInWindows ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _syncingAutostart = false; }
    }

    private void OnAutostartToggled(bool enabled)
    {
        if (_syncingAutostart || Context.Autostart is not { } reg) return;
        try
        {
            reg.Set(enabled);
            _autostartError.Visibility = Visibility.Collapsed;
            AppLog.Info("Settings", enabled ? $"Start with Windows enabled ({reg.Command})" : "Start with Windows disabled");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Settings", $"Could not change the start-with-Windows entry: {ex.Message}");
            _autostartError.Text = "Windows did not allow the change: " + ex.Message;
            _autostartError.Visibility = Visibility.Visible;
        }
        SyncAutostart();
    }

    private void Save()
    {
        Context.ApplyServiceSettings();
        Context.Settings.NotifyChanged();
    }

    public override void OnShown()
    {
        SyncAutostart();
        _hotkeys.Children.Clear();
        foreach (var h in Context.Hotkeys())
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var key = Ui.Pill(h.Display, AppThemeKeysSoft, ThemeKeys.Accent, 11);
            key.HorizontalAlignment = HorizontalAlignment.Left;
            row.Children.Add(key);
            var desc = Ui.Text(h.Description, ThemeKeys.Text, 12.5);
            Grid.SetColumn(desc, 1);
            row.Children.Add(desc);
            var state = Ui.Text(h.Registered ? "active" : "not registered", h.Registered ? ThemeKeys.Positive : ThemeKeys.TextMuted, 11);
            state.ToolTip = h.Registered ? "Registered system-wide" : "Not registered (disabled, preview, or used by another application)";
            Grid.SetColumn(state, 2);
            row.Children.Add(state);
            _hotkeys.Children.Add(row);
        }
    }

    private const string AppThemeKeysSoft = Theming.AppThemeKeys.AccentSoft;

    private static StackPanel SliderWithLabel(double min, double max, double value, double tick, Func<double, string> format, Action<double> changed)
    {
        var label = Ui.Text(format(value), ThemeKeys.TextMuted, 12);
        var slider = Ui.Slider(min, max, value, v => { label.Text = format(v); changed(v); }, 220, tick);
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(slider);
        label.Margin = new Thickness(12, 0, 0, 0);
        sp.Children.Add(label);
        return sp;
    }

    private static Grid FolderRow(string label, string path)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sp = new StackPanel();
        sp.Children.Add(Ui.Text(label, ThemeKeys.Text, 12.5, FontWeights.SemiBold));
        var p = Ui.Text(path, ThemeKeys.TextMuted, 11.5, mono: true);
        p.ToolTip = path;
        sp.Children.Add(p);
        g.Children.Add(sp);
        var open = Ui.Button("Open", () => { Directory.CreateDirectory(path); Ui.OpenUrl(path); }, icon: Ui.IconFolder);
        open.VerticalAlignment = VerticalAlignment.Center;
        open.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(open, 1);
        g.Children.Add(open);
        return g;
    }
}
