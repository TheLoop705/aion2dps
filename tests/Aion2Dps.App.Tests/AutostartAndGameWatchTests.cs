using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Aion2Dps.App.Dashboard;
using Aion2Dps.App.Infrastructure;
using Aion2Dps.App.Integration;
using Aion2Dps.App.Overlay;
using Aion2Dps.App.Rendering;
using Aion2Dps.App.Settings;
using Aion2Dps.App.Theming;
using Microsoft.Win32;

namespace Aion2Dps.App.Tests;

/// <summary>
/// "Start with Windows" (registry Run value), the game process watcher, the overlay-follows-the-game policy and their
/// settings. Registry tests use HKCU\Software\Aion2Dps.Tests\Run-&lt;guid&gt; only (never the real Run key); no window is shown.
/// </summary>
public class AutostartAndGameWatchTests
{
    private const string TestRoot = @"Software\Aion2Dps.Tests";

    // ───────────────────────── AutostartRegistration ─────────────────────────

    private sealed class TestRunKey : IDisposable
    {
        public TestRunKey()
        {
            Path = TestRoot + @"\Run-" + Guid.NewGuid().ToString("N");
            Assert.NotEqual(AutostartRegistration.DefaultRunKeyPath, Path, StringComparer.OrdinalIgnoreCase);
            Registry.CurrentUser.CreateSubKey(Path)!.Dispose();
        }

        public string Path { get; }

        public string? Get(string name)
        {
            using var key = Registry.CurrentUser.OpenSubKey(Path);
            return key?.GetValue(name) as string;
        }

        public void Set(string name, string value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(Path)!;
            key.SetValue(name, value, RegistryValueKind.String);
        }

        /// <summary>The scratch StartupApproved key the registration derives for this Run key.</summary>
        public string ApprovedPath => Path + @"\StartupApproved";

        /// <summary>What Task Manager writes: 12 bytes, first byte 02 = enabled, 03 = disabled.</summary>
        public void SetApproval(string name, byte first)
        {
            using var key = Registry.CurrentUser.CreateSubKey(ApprovedPath)!;
            var data = new byte[12];
            data[0] = first;
            key.SetValue(name, data, RegistryValueKind.Binary);
        }

        public byte[]? GetApproval(string name)
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovedPath);
            return key?.GetValue(name) as byte[];
        }

        public void Dispose()
        {
            Registry.CurrentUser.DeleteSubKeyTree(Path, throwOnMissingSubKey: false);
            using var root = Registry.CurrentUser.OpenSubKey(TestRoot);
            bool empty = root is not null && root.SubKeyCount == 0 && root.ValueCount == 0;
            root?.Dispose();
            if (empty) Registry.CurrentUser.DeleteSubKey(TestRoot, throwOnMissingSubKey: false);
        }
    }

    private static string FakeExe(string folder = "install") =>
        Path.Combine(Path.GetTempPath(), "aion2dps-autostart-" + Guid.NewGuid().ToString("N"), folder, "Aion2Dps.exe");

    [Fact]
    public void Enable_writes_a_quoted_command_with_the_autostart_flag_and_disable_removes_it()
    {
        using var run = new TestRunKey();
        var exe = FakeExe("Program Files With Spaces");
        var reg = new AutostartRegistration(exe, run.Path);
        Assert.False(reg.IsEnabled());

        reg.Enable();
        Assert.Equal($"\"{exe}\" --autostart", run.Get(AutostartRegistration.ValueName));
        Assert.True(reg.IsEnabled());
        Assert.True(new AutostartRegistration(exe.ToUpperInvariant(), run.Path).IsEnabled()); // paths compare case-insensitively

        reg.Set(false);
        Assert.Null(run.Get(AutostartRegistration.ValueName));
        Assert.False(reg.IsEnabled());
        reg.Disable(); // idempotent
        reg.Set(true);
        Assert.True(reg.IsEnabled());
    }

    [Fact]
    public void Other_apps_run_values_are_never_touched()
    {
        using var run = new TestRunKey();
        run.Set("Steam", "\"C:\\Program Files (x86)\\Steam\\steam.exe\" -silent");
        run.Set("Aion2DpsHelper", "\"C:\\Nowhere\\Aion2Dps.exe\" --autostart"); // similar name, other value
        var reg = new AutostartRegistration(FakeExe(), run.Path);

        reg.Enable();
        reg.Reconcile();
        reg.Disable();

        Assert.Equal("\"C:\\Program Files (x86)\\Steam\\steam.exe\" -silent", run.Get("Steam"));
        Assert.Equal("\"C:\\Nowhere\\Aion2Dps.exe\" --autostart", run.Get("Aion2DpsHelper"));
    }

    [Fact]
    public void Disable_leaves_a_value_that_starts_another_program()
    {
        using var run = new TestRunKey();
        const string other = "\"C:\\Tools\\SomethingElse.exe\" --flag";
        run.Set(AutostartRegistration.ValueName, other);
        var reg = new AutostartRegistration(FakeExe(), run.Path);

        Assert.False(reg.IsEnabled());
        reg.Disable();
        Assert.Equal(other, run.Get(AutostartRegistration.ValueName));
    }

    [Fact]
    public void Reconcile_repoints_a_value_of_a_moved_or_replaced_install_to_this_exe()
    {
        using var run = new TestRunKey();
        var stale = FakeExe("old-install"); // never created: the old install is gone
        run.Set(AutostartRegistration.ValueName, $"\"{stale}\" --autostart");
        var exe = FakeExe("new-install");
        var reg = new AutostartRegistration(exe, run.Path);

        Assert.False(reg.IsEnabled());
        Assert.True(reg.Reconcile());
        Assert.Equal($"\"{exe}\" --autostart", run.Get(AutostartRegistration.ValueName));
        Assert.False(reg.Reconcile()); // already ours
    }

    [Fact]
    public void Reconcile_never_creates_a_value_or_changes_a_foreign_or_still_existing_one()
    {
        using var run = new TestRunKey();
        var reg = new AutostartRegistration(FakeExe(), run.Path);

        Assert.False(reg.Reconcile());
        Assert.Null(run.Get(AutostartRegistration.ValueName)); // never enables on its own

        const string foreign = "\"C:\\Missing\\OtherTool.exe\" --autostart"; // missing, but not an Aion2Dps executable
        run.Set(AutostartRegistration.ValueName, foreign);
        Assert.False(reg.Reconcile());
        Assert.Equal(foreign, run.Get(AutostartRegistration.ValueName));

        var otherCopy = FakeExe("other-copy"); // another Aion2Dps copy that still exists keeps its entry
        Directory.CreateDirectory(Path.GetDirectoryName(otherCopy)!);
        File.WriteAllText(otherCopy, "");
        try
        {
            run.Set(AutostartRegistration.ValueName, $"\"{otherCopy}\" --autostart");
            Assert.False(reg.Reconcile());
            Assert.Equal($"\"{otherCopy}\" --autostart", run.Get(AutostartRegistration.ValueName));
        }
        finally { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(otherCopy))!, true); }
    }

    [Fact]
    public void A_test_run_key_never_reaches_the_real_startup_approved_key()
    {
        var test = new AutostartRegistration(FakeExe(), TestRoot + @"\Run-x");
        Assert.Equal(TestRoot + @"\Run-x\StartupApproved", test.StartupApprovedKeyPath);
        var real = new AutostartRegistration(FakeExe());   // only inspected, never read or written here
        Assert.Equal(AutostartRegistration.DefaultStartupApprovedKeyPath, real.StartupApprovedKeyPath);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", real.StartupApprovedKeyPath);
    }

    [Fact]
    public void Switched_off_in_task_manager_reads_as_off_and_ticking_switches_it_on_again()
    {
        using var run = new TestRunKey();
        var reg = new AutostartRegistration(FakeExe(), run.Path);
        reg.Enable();
        run.SetApproval(AutostartRegistration.ValueName, 0x03);   // Task Manager → Startup apps → Disable
        run.SetApproval("Docker Desktop", 0x03);                  // another app's flag

        Assert.False(reg.IsEnabled());
        Assert.True(reg.IsDisabledByWindows());
        Assert.NotNull(reg.ReadCommand());                        // Windows keeps the Run value

        reg.Enable();
        Assert.True(reg.IsEnabled());
        Assert.False(reg.IsDisabledByWindows());
        Assert.Null(run.GetApproval(AutostartRegistration.ValueName));
        Assert.Equal(0x03, run.GetApproval("Docker Desktop")![0]);

        run.SetApproval(AutostartRegistration.ValueName, 0x02);   // switched on again in Task Manager
        Assert.True(reg.IsEnabled());
        run.SetApproval(AutostartRegistration.ValueName, 0x07);
        Assert.False(reg.IsEnabled());

        reg.Disable();                                            // removes the Run value and our flag, nothing else
        Assert.Null(reg.ReadCommand());
        Assert.Null(run.GetApproval(AutostartRegistration.ValueName));
        Assert.Equal(0x03, run.GetApproval("Docker Desktop")![0]);
        Assert.False(reg.IsDisabledByWindows());
    }

    [Fact]
    public void Disable_keeps_the_windows_flag_of_another_copys_entry()
    {
        using var run = new TestRunKey();
        const string other = "\"C:\\Elsewhere\\Aion2Dps.exe\" --autostart";
        run.Set(AutostartRegistration.ValueName, other);
        run.SetApproval(AutostartRegistration.ValueName, 0x03);
        var reg = new AutostartRegistration(FakeExe(), run.Path);

        Assert.False(reg.IsDisabledByWindows());   // the flag is about the other copy's entry
        reg.Disable();
        Assert.Equal(other, run.Get(AutostartRegistration.ValueName));
        Assert.NotNull(run.GetApproval(AutostartRegistration.ValueName));
    }

    [Theory]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe", false)]   // dotnet Aion2Dps.dll
    [InlineData(@"C:\Users\x\AppData\Local\Programs\Aion2Dps\Aion2Dps.exe", true)]
    [InlineData(@"C:\Apps\AION2DPS.EXE", true)]
    [InlineData(@"C:\Apps\Aion2Dps.dll", false)]
    [InlineData(@"C:\Apps\testhost.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_apps_own_executable_can_be_registered(string? processPath, bool available)
    {
        var reg = AutostartRegistration.ForProcessPath(processPath);
        Assert.Equal(available, reg is not null);
        if (reg is not null) Assert.Equal(AutostartRegistration.DefaultRunKeyPath, reg.RunKeyPath); // inspected only
    }

    [Fact]
    public void The_install_default_switches_it_on_once_for_installed_copies_only()
    {
        using var run = new TestRunKey();
        var exe = FakeExe("Programs With Spaces");
        var root = Path.GetDirectoryName(Path.GetDirectoryName(exe))!;
        try
        {
            var reg = new AutostartRegistration(exe, run.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);

            Assert.False(reg.IsInstalledCopy);
            Assert.Null(reg.ApplyInstallDefault());                  // build output / loose copy: nothing, retry later
            Assert.Null(reg.ReadCommand());

            File.WriteAllText(Path.Combine(Path.GetDirectoryName(exe)!, AutostartRegistration.InstallMarkerName), "0.3.0");
            Assert.True(reg.IsInstalledCopy);
            Assert.True(reg.ApplyInstallDefault());
            Assert.Equal($"\"{exe}\" --autostart", run.Get(AutostartRegistration.ValueName));
            Assert.True(reg.IsEnabled());

            Assert.False(reg.ApplyInstallDefault());                 // already there: unchanged
            Assert.True(reg.IsEnabled());

            reg.Disable();
            run.SetApproval(AutostartRegistration.ValueName, 0x03);  // an earlier decision in Windows is respected
            Assert.False(reg.ApplyInstallDefault());
            Assert.Null(reg.ReadCommand());

            using var run2 = new TestRunKey();                       // a foreign value is never replaced
            const string foreign = "\"C:\\Tools\\Other.exe\"";
            run2.Set(AutostartRegistration.ValueName, foreign);
            Assert.False(new AutostartRegistration(exe, run2.Path).ApplyInstallDefault());
            Assert.Equal(foreign, run2.Get(AutostartRegistration.ValueName));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void A_second_instance_signals_the_first_unless_told_not_to()
    {
        string name = "Aion2Dps.Tests." + Guid.NewGuid().ToString("N");
        using var signalled = new ManualResetEventSlim();
        using var first = SingleInstanceGuard.TryAcquire(name, signalled.Set);
        Assert.NotNull(first);

        // Windows' delayed --autostart: exits without popping up the running meter's dashboard.
        Assert.Null(SingleInstanceGuard.TryAcquire(name, () => { }, signalExisting: false));
        Assert.False(signalled.Wait(TimeSpan.FromMilliseconds(300)));

        // A normal second launch still asks the running meter to open its dashboard.
        Assert.Null(SingleInstanceGuard.TryAcquire(name, () => { }));
        Assert.True(signalled.Wait(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData("\"C:\\A B\\Aion2Dps.exe\" --autostart", "C:\\A B\\Aion2Dps.exe")]
    [InlineData("C:\\A B\\Aion2Dps.exe --autostart", "C:\\A B\\Aion2Dps.exe")]
    [InlineData("C:\\Apps\\Aion2Dps.exe", "C:\\Apps\\Aion2Dps.exe")]
    [InlineData("  \"C:\\x.exe\"", "C:\\x.exe")]
    [InlineData("\"unterminated", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Run_commands_are_parsed_to_their_executable(string? command, string? expected) =>
        Assert.Equal(expected, AutostartRegistration.TryParseExePath(command));

    // ───────────────────────── GameProcessWatcher ─────────────────────────

    private sealed class FakeGame
    {
        public bool Running;
        public DateTime Now = new(2026, 10, 8, 20, 0, 0, DateTimeKind.Utc);
        public readonly List<string> Events = new();

        public GameProcessWatcher Watcher(TimeSpan? grace = null)
        {
            var w = new GameProcessWatcher(() => Running, post: a => a(), pollInterval: TimeSpan.FromSeconds(2),
                exitGrace: grace ?? TimeSpan.FromSeconds(5), clock: () => Now);
            w.GameStarted += () => Events.Add("started");
            w.GameExited += () => Events.Add("exited");
            w.GameNotRunning += () => Events.Add("not-running");
            return w;
        }

        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
    }

    [Fact]
    public void A_game_already_running_at_start_is_reported_on_the_first_check()
    {
        var game = new FakeGame { Running = true };
        using var w = game.Watcher();
        w.Poll();
        Assert.Equal(["started"], game.Events);
        Assert.True(w.IsGameRunning);
        game.Advance(2); w.Poll();
        game.Advance(2); w.Poll();
        Assert.Equal(["started"], game.Events);
    }

    [Fact]
    public void Game_start_after_waiting_is_reported_once()
    {
        var game = new FakeGame();
        using var w = game.Watcher();
        w.Poll();
        game.Advance(2); w.Poll();
        Assert.Equal(["not-running"], game.Events);
        Assert.False(w.IsGameRunning);
        game.Running = true;
        game.Advance(2); w.Poll();
        game.Advance(2); w.Poll();
        Assert.Equal(["not-running", "started"], game.Events);
    }

    [Fact]
    public void Game_exit_is_reported_only_after_the_grace_period()
    {
        var game = new FakeGame { Running = true };
        using var w = game.Watcher();
        w.Poll();
        game.Running = false;
        game.Advance(2); w.Poll();   // gone since t
        game.Advance(2); w.Poll();   // t + 2
        game.Advance(2); w.Poll();   // t + 4
        Assert.Equal(["started"], game.Events);
        Assert.True(w.IsGameRunning);
        game.Advance(2); w.Poll();   // t + 6 ≥ 5 s grace
        Assert.Equal(["started", "exited"], game.Events);
        Assert.False(w.IsGameRunning);
        game.Advance(2); w.Poll();
        Assert.Equal(["started", "exited"], game.Events);
    }

    [Fact]
    public void A_quick_restart_within_the_grace_raises_nothing_and_restarts_the_grace()
    {
        var game = new FakeGame { Running = true };
        using var w = game.Watcher();
        w.Poll();
        game.Running = false;                       // Steam / the launcher restarts the client
        game.Advance(2); w.Poll();
        game.Advance(2); w.Poll();
        game.Running = true;
        game.Advance(2); w.Poll();
        Assert.Equal(["started"], game.Events);

        game.Running = false;
        game.Advance(2); w.Poll();                  // a new absence starts a new grace
        game.Advance(4); w.Poll();
        Assert.Equal(["started"], game.Events);
        game.Advance(1); w.Poll();
        Assert.Equal(["started", "exited"], game.Events);
    }

    [Fact]
    public void A_failing_process_list_keeps_the_current_state()
    {
        var game = new FakeGame { Running = true };
        bool fail = false;
        using var w = new GameProcessWatcher(() => fail ? throw new InvalidOperationException("boom") : game.Running,
            post: a => a(), exitGrace: TimeSpan.Zero, clock: () => game.Now);
        int exits = 0;
        w.GameExited += () => exits++;
        w.Poll();
        fail = true;
        game.Advance(10); w.Poll();
        game.Advance(10); w.Poll();
        Assert.Equal(0, exits);
        Assert.True(w.IsGameRunning);
    }

    [Fact]
    public void The_timer_polls_off_the_calling_thread_and_stop_resets_the_state()
    {
        bool running = true;
        var started = new ManualResetEventSlim();
        var exited = new ManualResetEventSlim();
        bool onPoolThread = true;
        int caller = Environment.CurrentManagedThreadId;
        using var w = new GameProcessWatcher(() => Volatile.Read(ref running),
            post: a => { if (Environment.CurrentManagedThreadId == caller) onPoolThread = false; a(); },
            pollInterval: TimeSpan.FromMilliseconds(20), exitGrace: TimeSpan.FromMilliseconds(60));
        w.GameStarted += () => started.Set();
        w.GameExited += () => exited.Set();
        w.Start();
        Assert.True(w.IsStarted);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        Volatile.Write(ref running, false);
        Assert.True(exited.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(onPoolThread);

        w.Stop();
        Assert.False(w.IsStarted);
        Assert.False(w.IsGameRunning);
        started.Reset();
        Volatile.Write(ref running, true);
        w.Start();                                  // a restarted watcher reports the running game again
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        w.Dispose();
        Assert.False(w.IsStarted);
        Assert.Throws<ObjectDisposedException>(() => w.Start());
    }

    [Fact]
    public void The_real_process_check_runs_without_throwing()
    {
        _ = GameProcessWatcher.IsGameProcessRunning(); // the game may or may not run on this machine
    }

    // ───────────────────────── GameOverlayVisibilityPolicy ─────────────────────────

    [Theory]
    [InlineData(false, true, true, false, true)]    // classic: shown as before
    [InlineData(false, false, true, false, false)]  // "Show the overlay on start" off
    [InlineData(false, true, false, false, false)]  // hidden when last closed
    [InlineData(false, true, true, true, false)]    // --no-overlay
    [InlineData(true, true, true, false, false)]    // follows the game: waits for the first check
    public void Start_up_visibility(bool gameAware, bool launchOverlayOnStart, bool overlayVisible, bool noOverlay, bool expected) =>
        Assert.Equal(expected, GameOverlayVisibilityPolicy.ShowAtStartup(gameAware, launchOverlayOnStart, overlayVisible, noOverlay));

    [Theory]
    [InlineData(true, true, "Aion2Dps runs in the tray and appears when AION 2 starts.")]
    [InlineData(true, false, "Aion2Dps runs in the tray. Right-click the icon for the menu.")]   // "Show the overlay on start" off
    [InlineData(false, true, "Aion2Dps runs in the tray. Right-click the icon for the menu.")]
    [InlineData(false, false, "Aion2Dps runs in the tray. Right-click the icon for the menu.")]
    public void The_first_autostart_notice_promises_the_overlay_only_when_it_will_appear(bool gameAware, bool allowAutoShow, string text) =>
        Assert.Equal(text, GameOverlayVisibilityPolicy.AutostartNoticeText(gameAware, allowAutoShow));

    private static GameOverlayVisibilityPolicy Enabled()
    {
        var p = new GameOverlayVisibilityPolicy();
        Assert.Equal(OverlayVisibilityCommand.None, p.SetEnabled(true, overlayVisible: false, userWantsOverlay: true, allowAutoShow: true));
        return p;
    }

    [Fact]
    public void Disabled_policy_never_commands_anything()
    {
        var p = new GameOverlayVisibilityPolicy();
        Assert.Equal(OverlayVisibilityCommand.None, p.OnGameStarted(overlayVisible: false, allowAutoShow: true));
        Assert.Equal(OverlayVisibilityCommand.None, p.OnGameExited(overlayVisible: true));
        Assert.Equal(OverlayVisibilityCommand.None, p.OnGameNotRunning(overlayVisible: true));
        Assert.Null(p.StatusText);
    }

    [Fact]
    public void Game_start_shows_and_game_exit_hides()
    {
        var p = Enabled();
        Assert.Equal(OverlayVisibilityCommand.None, p.OnGameNotRunning(overlayVisible: false));
        Assert.Equal("Waiting for AION 2", p.StatusText);
        Assert.Equal(OverlayVisibilityCommand.Show, p.OnGameStarted(overlayVisible: false, allowAutoShow: true));
        Assert.Equal("AION 2 running", p.StatusText);
        Assert.Equal(OverlayVisibilityCommand.Hide, p.OnGameExited(overlayVisible: true));
        Assert.Equal("Waiting for AION 2", p.StatusText);
        Assert.Equal(OverlayVisibilityCommand.None, p.OnGameExited(overlayVisible: false));
    }

    [Fact]
    public void Show_on_start_off_or_no_overlay_keeps_it_hidden_when_the_game_starts()
    {
        var p = Enabled();
        Assert.Equal(OverlayVisibilityCommand.None, p.OnGameStarted(overlayVisible: false, allowAutoShow: false));
        Assert.True(p.GameRunning);
    }

    [Fact]
    public void A_manual_hide_while_playing_is_respected_until_the_next_game_start()
    {
        var p = Enabled();
        Assert.Equal(OverlayVisibilityCommand.Show, p.OnGameStarted(false, true));
        p.OnUserHid();
        Assert.True(p.HiddenByUser);
        // The same running game reported again (watcher restarted by switching the setting off and on): stays hidden.
        p.SetEnabled(false, overlayVisible: false, userWantsOverlay: false, allowAutoShow: true);
        p.SetEnabled(true, overlayVisible: false, userWantsOverlay: false, allowAutoShow: true);
        Assert.Equal(OverlayVisibilityCommand.None, p.OnGameStarted(false, true));
        // Exit, then the next start shows it again.
        Assert.Equal(OverlayVisibilityCommand.None, p.OnGameExited(overlayVisible: false));
        Assert.False(p.HiddenByUser);
        Assert.Equal(OverlayVisibilityCommand.Show, p.OnGameStarted(false, true));
    }

    [Fact]
    public void A_manual_show_clears_the_manual_hide_and_hiding_while_waiting_is_not_remembered()
    {
        var p = Enabled();
        p.OnUserHid(); // no game yet: nothing to remember
        Assert.False(p.HiddenByUser);
        p.OnGameStarted(false, true);
        p.OnUserHid();
        p.OnUserShowed();
        Assert.False(p.HiddenByUser);
        Assert.Equal(OverlayVisibilityCommand.Hide, p.OnGameExited(overlayVisible: true));
    }

    [Fact]
    public void Enabling_while_the_game_is_not_running_hides_a_visible_overlay_after_the_first_check()
    {
        var p = new GameOverlayVisibilityPolicy();
        Assert.Equal(OverlayVisibilityCommand.None, p.SetEnabled(true, overlayVisible: true, userWantsOverlay: true, allowAutoShow: true));
        Assert.Equal(OverlayVisibilityCommand.Hide, p.OnGameNotRunning(overlayVisible: true));
    }

    [Fact]
    public void Switching_the_setting_off_brings_the_overlay_back_unless_the_user_hid_it()
    {
        var p = Enabled();
        p.OnGameNotRunning(false);
        Assert.Equal(OverlayVisibilityCommand.Show, p.SetEnabled(false, overlayVisible: false, userWantsOverlay: true, allowAutoShow: true));
        Assert.Null(p.StatusText);

        p = Enabled();
        Assert.Equal(OverlayVisibilityCommand.None, p.SetEnabled(false, overlayVisible: false, userWantsOverlay: false, allowAutoShow: true));
        p = Enabled();
        Assert.Equal(OverlayVisibilityCommand.None, p.SetEnabled(false, overlayVisible: false, userWantsOverlay: true, allowAutoShow: false));
        p = Enabled();
        p.OnGameStarted(false, true);
        p.OnUserHid();
        Assert.Equal(OverlayVisibilityCommand.None, p.SetEnabled(false, overlayVisible: false, userWantsOverlay: true, allowAutoShow: true));
        Assert.Equal(OverlayVisibilityCommand.None, p.SetEnabled(false, false, true, true)); // unchanged → nothing
    }

    [Fact]
    public void Automatic_hide_keeps_the_saved_visibility_and_is_not_reported_as_a_user_choice() => Sta.Run(() =>
    {
        string dir = TempDirectory();
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            using var services = ServiceFactory.CreateDemo();
            using var controller = new OverlayController(services, store, _ => { }); // never shown
            var user = new List<bool>();
            controller.UserVisibilityChanged += user.Add;

            controller.HideAutomatically();
            Assert.True(store.Current.Overlay.Visible);
            Assert.Empty(user);

            controller.Hide();
            Assert.False(store.Current.Overlay.Visible);
            Assert.Equal([false], user);
        }
        finally { Directory.Delete(dir, true); }
    });

    // ───────────────────────── Launch options & settings ─────────────────────────

    [Fact]
    public void Launch_options_parse_the_autostart_flag()
    {
        Assert.False(LaunchOptions.Parse([]).Autostart);
        var o = LaunchOptions.Parse(["--autostart"]);
        Assert.True(o.Autostart);
        Assert.Equal(LaunchMode.Live, o.Mode);
        Assert.True(LaunchOptions.Parse(["--AUTOSTART", "--no-overlay"]).Autostart);
    }

    [Fact]
    public void Startup_settings_round_trip_and_default_on_for_older_files()
    {
        string dir = TempDirectory();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            var defaults = new AppSettings();
            Assert.True(defaults.General.ShowOverlayOnlyWhileGameRuns);
            Assert.False(defaults.General.AutostartNoticeShown);

            File.WriteAllText(path, "{ \"Version\": 1, \"General\": { \"OpenDashboardOnStart\": false } }");
            var old = SettingsStore.LoadFrom(path);
            Assert.True(old.General.ShowOverlayOnlyWhileGameRuns);
            Assert.False(old.General.OpenDashboardOnStart);

            old.General.ShowOverlayOnlyWhileGameRuns = false;
            Assert.False(old.General.AutostartDefaultApplied);   // older files: the one-time default is still to come
            old.General.AutostartNoticeShown = true;
            old.General.AutostartDefaultApplied = true;
            SettingsStore.SaveTo(path, old);
            var back = SettingsStore.LoadFrom(path);
            Assert.False(back.General.ShowOverlayOnlyWhileGameRuns);
            Assert.True(back.General.AutostartNoticeShown);
            Assert.True(back.General.AutostartDefaultApplied);
            Assert.True(back.Clone().General.AutostartDefaultApplied);
            Assert.False(back.Clone().General.ShowOverlayOnlyWhileGameRuns);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Settings_page_startup_checkboxes_follow_the_registry_and_save_the_game_setting() => Sta.Run(() =>
    {
        string dir = TempDirectory();
        using var run = new TestRunKey();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            var store = new SettingsStore(path);
            using var services = ServiceFactory.CreateDemo();
            var exe = FakeExe();
            var reg = new AutostartRegistration(exe, run.Path);
            var page = new SettingsPage(new DashboardContext { Settings = store, Services = services, Autostart = reg });
            OffscreenRenderer.Render(OffscreenRenderer.Themed(page, ThemeCatalog.Obsidian), 1000);

            var auto = Descendants<CheckBox>(page).Single(c => Equals(c.Content, "Start Aion2Dps with Windows (in the tray)"));
            Assert.True(auto.IsEnabled);
            Assert.False(auto.IsChecked);
            auto.IsChecked = true;
            Assert.Equal($"\"{exe}\" --autostart", run.Get(AutostartRegistration.ValueName));
            auto.IsChecked = false;
            Assert.Null(run.Get(AutostartRegistration.ValueName));

            reg.Enable();      // changed outside the page (installer -Autostart): the page shows the registry state
            page.OnShown();
            Assert.True(auto.IsChecked);
            Assert.False(SettingsStore.LoadFrom(path).General.AutostartNoticeShown); // nothing mirrored in settings.json

            var game = Descendants<CheckBox>(page).Single(c => Equals(c.Content, "Show the overlay only while AION 2 is running"));
            Assert.True(game.IsChecked);
            game.IsChecked = false;
            Assert.False(store.Current.General.ShowOverlayOnlyWhileGameRuns);
            Assert.False(SettingsStore.LoadFrom(path).General.ShowOverlayOnlyWhileGameRuns);
        }
        finally { Directory.Delete(dir, true); }
    });

    [Fact]
    public void Settings_page_shows_an_entry_switched_off_in_windows_as_off_and_ticking_switches_it_on() => Sta.Run(() =>
    {
        string dir = TempDirectory();
        using var run = new TestRunKey();
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            using var services = ServiceFactory.CreateDemo();
            var reg = new AutostartRegistration(FakeExe(), run.Path);
            reg.Enable();
            run.SetApproval(AutostartRegistration.ValueName, 0x03);
            var page = new SettingsPage(new DashboardContext { Settings = store, Services = services, Autostart = reg });
            OffscreenRenderer.Render(OffscreenRenderer.Themed(page, ThemeCatalog.Obsidian), 1000);

            var auto = Descendants<CheckBox>(page).Single(c => Equals(c.Content, "Start Aion2Dps with Windows (in the tray)"));
            var note = Descendants<TextBlock>(page).Single(t => t.Text.StartsWith("Switched off in Windows", StringComparison.Ordinal));
            Assert.False(auto.IsChecked);
            Assert.Equal(Visibility.Visible, note.Visibility);

            auto.IsChecked = true;
            Assert.True(reg.IsEnabled());
            Assert.Null(run.GetApproval(AutostartRegistration.ValueName));
            Assert.True(auto.IsChecked);
            Assert.Equal(Visibility.Collapsed, note.Visibility);
        }
        finally { Directory.Delete(dir, true); }
    });

    [Fact]
    public void Settings_page_without_a_registration_disables_the_autostart_checkbox() => Sta.Run(() =>
    {
        string dir = TempDirectory();
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            using var services = ServiceFactory.CreateDemo();
            var page = new SettingsPage(new DashboardContext { Settings = store, Services = services });
            OffscreenRenderer.Render(OffscreenRenderer.Themed(page, ThemeCatalog.Obsidian), 1000);
            var auto = Descendants<CheckBox>(page).Single(c => Equals(c.Content, "Start Aion2Dps with Windows (in the tray)"));
            Assert.False(auto.IsEnabled);
            Assert.False(auto.IsChecked);
        }
        finally { Directory.Delete(dir, true); }
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    private static string TempDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "aion2dps-autostart-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
