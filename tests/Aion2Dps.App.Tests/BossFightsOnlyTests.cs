using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Aion2Dps.App.Dashboard;
using Aion2Dps.App.Demo;
using Aion2Dps.App.Integration;
using Aion2Dps.App.Overlay;
using Aion2Dps.App.Rendering;
using Aion2Dps.App.Settings;
using Aion2Dps.App.Theming;
using Aion2Dps.Combat;
using Aion2Dps.Contracts;

namespace Aion2Dps.App.Tests;

/// <summary>"Track boss fights only" setting and the 60 s linger of a finished boss fight on the overlay.</summary>
public class BossFightsOnlyTests
{
    private static readonly DateTime T0 = new(2026, 10, 8, 18, 0, 0, DateTimeKind.Utc);

    // ───────────── Settings ─────────────

    [Fact]
    public void New_installs_track_bosses_only_and_keep_finished_fights_for_sixty_seconds()
    {
        var g = new AppSettings().General;
        Assert.True(g.BossFightsOnly);
        Assert.Equal(60, g.EndedDisplaySeconds);
    }

    [Fact]
    public void Settings_files_without_the_key_get_boss_only_on()
    {
        string directory = TempDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{\"General\":{\"LivePlayerClock\":true,\"EndedDisplaySeconds\":20}}");
            var loaded = SettingsStore.LoadFrom(path).General;
            Assert.True(loaded.BossFightsOnly);
            Assert.Equal(20, loaded.EndedDisplaySeconds);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Boss_only_survives_save_load_and_clone(bool value)
    {
        string directory = TempDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var settings = new AppSettings();
            settings.General.BossFightsOnly = value;
            SettingsStore.SaveTo(path, settings);
            Assert.Equal(value, SettingsStore.LoadFrom(path).General.BossFightsOnly);
            Assert.Equal(value, settings.Clone().General.BossFightsOnly);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Settings_page_checkbox_applies_and_saves_the_setting() => Sta.Run(() =>
    {
        string directory = TempDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var store = new SettingsStore(path);
            using var services = ServiceFactory.CreateDemo();
            var applied = new List<bool>();
            var context = new DashboardContext
            {
                Settings = store,
                Services = services,
                ApplyServiceSettings = () => applied.Add(store.Current.General.BossFightsOnly),
            };
            var page = new SettingsPage(context);
            OffscreenRenderer.Render(OffscreenRenderer.Themed(page, ThemeCatalog.Obsidian), 1000);
            var check = Assert.Single(Descendants<CheckBox>(page), c => c.Content as string == "Track boss fights only (ignore trash mobs)");
            Assert.True(check.IsChecked);
            Assert.Empty(applied);

            check.IsChecked = false;
            Assert.False(store.Current.General.BossFightsOnly);
            Assert.Equal(new[] { false }, applied);
            Assert.False(SettingsStore.LoadFrom(path).General.BossFightsOnly);

            check.IsChecked = true;
            Assert.Equal(new[] { false, true }, applied);
            Assert.True(SettingsStore.LoadFrom(path).General.BossFightsOnly);
        }
        finally { Directory.Delete(directory, true); }
    });

    // ───────────── Presentation ─────────────

    [Fact]
    public void Finished_fight_stays_expanded_for_the_whole_display_time()
    {
        Assert.Equal(60, OverlayPresentationPolicy.ResolveLinger(60));
        var fight = Guid.NewGuid();
        var t = new OverlayPresentationTracker();
        Assert.Equal(OverlayPresentation.Expanded, t.Update(MeterState.InCombat, fight, T0, true, 60, false));
        Assert.Equal(OverlayPresentation.Expanded, t.Update(MeterState.Ended, fight, T0.AddSeconds(1), true, 60, false));
        Assert.Equal(OverlayPresentation.Expanded, t.Update(MeterState.Ended, fight, T0.AddSeconds(40), true, 60, false));
        Assert.Equal(OverlayPresentation.Expanded, t.Update(MeterState.Ended, fight, T0.AddSeconds(60.9), true, 60, false));
        Assert.Equal(OverlayPresentation.Compact, t.Update(MeterState.Ended, fight, T0.AddSeconds(61), true, 60, false));
    }

    /// <summary>
    /// Real engine + overlay tracker: a boss dies, the party pulls trash for a minute; the result stays on the full
    /// overlay for the 60 s display time, then the engine clears it and the overlay shrinks at the same moment.
    /// </summary>
    [Fact]
    public void Boss_result_lingers_expanded_through_trash_then_engine_and_overlay_clear_together()
    {
        var engine = Engine(new EngineOptions { BossFightsOnly = true, EndedDisplaySeconds = 60 });
        var t = new OverlayPresentationTracker();
        MeterSnapshot Step(double s, out OverlayPresentation p)
        {
            var snap = engine.GetSnapshot(At(s));
            p = t.Update(snap.State, snap.EncounterId, At(s), true, engine.Options.EndedDisplaySeconds, false);
            return snap;
        }

        Hit(engine, 1, Trash, 5000);
        Assert.Equal(MeterState.WaitingForCombat, Step(1, out var p).State);
        Assert.Equal(OverlayPresentation.Compact, p);

        Hit(engine, 2, Boss, 1000);
        Hit(engine, 9, Boss, 1000);
        Assert.Equal(MeterState.InCombat, Step(9, out p).State);
        Assert.Equal(OverlayPresentation.Expanded, p);
        Hp(engine, 10, Boss, 0);
        var ended = Step(10, out p);
        Assert.Equal(MeterState.Ended, ended.State);
        Assert.Equal(OverlayPresentation.Expanded, p);

        for (double s = 10.5; s < 70; s += 0.5)
        {
            if (s % 3 == 0) Hit(engine, s, Trash, 5000);
            engine.Tick(At(s));
            var snap = Step(s, out p);
            Assert.Equal(MeterState.Ended, snap.State);
            Assert.Equal(ended.EncounterId, snap.EncounterId);
            Assert.Equal(2000, snap.TotalDamage);
            Assert.Equal(OverlayPresentation.Expanded, p);
        }

        Assert.Equal(MeterState.WaitingForCombat, Step(70, out p).State);
        Assert.Equal(OverlayPresentation.Compact, p);
    }

    [Fact]
    public void A_new_boss_replaces_the_lingering_result_and_keeps_the_overlay_expanded()
    {
        var engine = Engine(new EngineOptions { BossFightsOnly = true, EndedDisplaySeconds = 60 });
        var t = new OverlayPresentationTracker();
        Hit(engine, 2, Boss, 1000);
        Hp(engine, 10, Boss, 0);
        var ended = engine.GetSnapshot(At(10));
        t.Update(ended.State, ended.EncounterId, At(10), true, 60, false);

        Hit(engine, 30, Boss2, 700);
        var next = engine.GetSnapshot(At(30));
        Assert.Equal(MeterState.InCombat, next.State);
        Assert.NotEqual(ended.EncounterId, next.EncounterId);
        Assert.Equal(700, next.TotalDamage);
        Assert.Equal(OverlayPresentation.Expanded, t.Update(next.State, next.EncounterId, At(30), true, 60, false));
    }

    [Fact]
    public void Setting_off_trash_replaces_the_finished_fight_as_before()
    {
        var engine = Engine(new EngineOptions { BossFightsOnly = false, EndedDisplaySeconds = 60 });
        Hit(engine, 2, Boss, 1000);
        Hp(engine, 10, Boss, 0);
        var ended = engine.GetSnapshot(At(10));
        Hit(engine, 20, Trash, 5000);
        var snap = engine.GetSnapshot(At(20));
        Assert.Equal(MeterState.InCombat, snap.State);
        Assert.Equal(EncounterKind.Trash, snap.EncounterKind);
        Assert.NotEqual(ended.EncounterId, snap.EncounterId);
    }

    // ───────────── helpers ─────────────

    private const uint Me = 100, Boss = 5000, Boss2 = 5001, Trash = 6001;
    private const uint GlaSkill = 11020030;

    private static DateTime At(double s) => T0.AddTicks((long)Math.Round(s * TimeSpan.TicksPerSecond));

    private static CombatEngine Engine(EngineOptions options)
    {
        var engine = new CombatEngine(new FakeGameData(), options);
        engine.OnEvent(new MapLoadEvent { Time = At(0), LoadCount = 1, MapId = FakeGameData.DemoMap });
        engine.OnEvent(new SelfInfoEvent
        {
            Time = At(0), Entity = Me, Name = "Me", ServerId = 2305, ClassCode = 6, Class = ClassInfo.FromCode(6), Level = 30,
        });
        Spawn(engine, Boss, FakeGameData.BossNpc, 3_600_000);
        Spawn(engine, Boss2, FakeGameData.SecondBossNpc, 2_000_000);
        Spawn(engine, Trash, FakeGameData.AddNpc, 50_000);
        return engine;
    }

    private static void Spawn(CombatEngine engine, uint id, uint code, long hp) =>
        engine.OnEvent(new SpawnEvent { Time = At(0), Entity = id, KindByte = 0x0C, KindFlags = 0x22, NpcCode = code, HpCurrent = hp, HpMax = hp });

    private static void Hp(CombatEngine engine, double t, uint id, long hp) =>
        engine.OnEvent(new EntityStatsEvent { Time = At(t), Entity = id, Format = 0x02, CurrentHp = hp });

    private static void Hit(CombatEngine engine, double t, uint target, long amount) =>
        engine.OnEvent(new DamageEvent
        {
            Time = At(t), Target = target, Actor = Me, SkillRaw = GlaSkill, SkillId = SkillIds.Normalize(GlaSkill),
            Switch = 4, Layout = 4, HitTag = 1, DamageType = 2, EffectId = GlaSkill * 100 + 11, HitIndex = 1,
            PowerScalar = 10170, Amount = amount, ExtraHits = Array.Empty<uint>(), EffectValidated = true,
        });

    private static string TempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "aion2dps-boss-only-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
