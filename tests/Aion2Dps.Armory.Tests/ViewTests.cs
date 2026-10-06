using System.IO;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Aion2Dps.Armory.Ui;
using Aion2Dps.Contracts;

namespace Aion2Dps.Armory.Tests;

/// <summary>Runs code on a fresh STA thread with a dispatcher synchronization context (no windows are created).</summary>
internal static class Sta
{
    public static void Run(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var t = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                action();
            }
            catch (Exception ex)
            {
                error = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        error?.Throw();
    }

    /// <summary>Pumps the dispatcher until <paramref name="task"/> completes.</summary>
    public static void Wait(Task task, int timeoutMs = 10_000)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(timeoutMs), DispatcherPriority.Normal, (_, _) => frame.Continue = false,
            Dispatcher.CurrentDispatcher);
        Dispatcher.PushFrame(frame);
        timer.Stop();
        if (!task.IsCompleted) throw new TimeoutException("UI task did not complete");
        task.GetAwaiter().GetResult();
    }

    public static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}

/// <summary>A theme with every ThemeKeys entry, as the App would provide it (test-only).</summary>
internal static class TestTheme
{
    public static ResourceDictionary Dark()
    {
        var d = new ResourceDictionary();
        void B(string key, string hex) => d[key] = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)));
        B(ThemeKeys.WindowBackground, "#FF12151B");
        B(ThemeKeys.Surface, "#FF1B2029");
        B(ThemeKeys.SurfaceAlt, "#FF232A35");
        B(ThemeKeys.Border, "#FF2F3846");
        B(ThemeKeys.Text, "#FFE6E9EF");
        B(ThemeKeys.TextMuted, "#FF8F99A8");
        B(ThemeKeys.Accent, "#FF4FA3FF");
        B(ThemeKeys.AccentText, "#FF0B1220");
        B(ThemeKeys.Positive, "#FF4CC38A");
        B(ThemeKeys.Negative, "#FFE5534B");
        B(ThemeKeys.Warning, "#FFE3B341");
        B(ThemeKeys.Crit, "#FFFF9F43");
        B(ThemeKeys.Dot, "#FFB98CFF");
        B(ThemeKeys.Heal, "#FF4CC38A");
        B(ThemeKeys.BarTrack, "#FF2A3240");
        B(ThemeKeys.BarFill, "#FF4FA3FF");
        B(ThemeKeys.HpHigh, "#FF4CC38A");
        B(ThemeKeys.HpMid, "#FFE3B341");
        B(ThemeKeys.HpLow, "#FFE5534B");
        foreach (var c in Enum.GetValues<CharacterClass>()) B(ThemeKeys.ClassBrush(c), ClassHex(c));
        d[ThemeKeys.FontFamily] = new FontFamily("Segoe UI");
        d[ThemeKeys.MonoFontFamily] = new FontFamily("Consolas");
        d[ThemeKeys.FontSize] = 12.0;
        d[ThemeKeys.CornerRadius] = new CornerRadius(6);
        d[ThemeKeys.BarCornerRadius] = new CornerRadius(3);
        return d;
    }

    public static ResourceDictionary Light()
    {
        var d = Dark();
        void B(string key, string hex) => d[key] = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)));
        B(ThemeKeys.WindowBackground, "#FFF3F4F7");
        B(ThemeKeys.Surface, "#FFFFFFFF");
        B(ThemeKeys.SurfaceAlt, "#FFF0F2F6");
        B(ThemeKeys.Border, "#FFD8DCE3");
        B(ThemeKeys.Text, "#FF1C2230");
        B(ThemeKeys.TextMuted, "#FF6B7484");
        B(ThemeKeys.Accent, "#FF1F6FD1");
        B(ThemeKeys.AccentText, "#FFFFFFFF");
        B(ThemeKeys.BarTrack, "#FFE3E7EE");
        return d;
    }

    private static string ClassHex(CharacterClass c) => c switch
    {
        CharacterClass.Gladiator => "#FFE08A3C",
        CharacterClass.Templar => "#FFD9C27A",
        CharacterClass.Ranger => "#FF7FC46A",
        CharacterClass.Assassin => "#FFB46AD9",
        CharacterClass.Elementalist => "#FF5FC8C0",
        CharacterClass.Sorcerer => "#FF6A9CFF",
        CharacterClass.Cleric => "#FFF0E6C8",
        CharacterClass.Chanter => "#FFE6D35A",
        _ => "#FFAAAAAA",
    };

    private static Brush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }
}

public class ViewTests
{
    private static string ScreensDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Aion2Dps.sln"))) dir = dir.Parent;
        var root = dir?.FullName ?? AppContext.BaseDirectory;
        var path = Path.Combine(root, "artifacts", "screens");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Render(FrameworkElement content, ResourceDictionary theme, double width, double height, string fileName)
    {
        var host = new Border { Child = content, Width = width, Height = height };
        host.Resources = theme;
        host.SetResourceReference(Border.BackgroundProperty, ThemeKeys.WindowBackground);
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
        Sta.DoEvents();
        host.UpdateLayout();

        var bmp = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(host);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        var path = Path.Combine(ScreensDir(), fileName);
        using (var fs = File.Create(path)) enc.Save(fs);
        // detach so the content can be re-hosted
        host.Child = null;
        return path;
    }

    private static (CharacterLookupView View, CharacterLookupViewModel Vm) LoadedView(FakeHandler h)
    {
        var client = TestClients.Create(h);
        var view = new CharacterLookupView(client, loadIcons: false);
        var vm = view.ViewModel!;
        Sta.Wait(vm.LoadServersAsync());
        vm.SearchText = "tester";
        Sta.Wait(vm.SearchAsync());
        Sta.Wait(vm.OpenCharacterAsync(vm.SearchResults[1].Result));
        return (view, vm);
    }

    [Fact]
    public void View_model_flow_fills_profile_equipment_and_board()
    {
        Sta.Run(() =>
        {
            var (_, vm) = LoadedView(FakeHandler.GlobalFixtures());
            Assert.Equal(45, vm.Servers.Count); // "All servers" + 44
            Assert.Equal(5, vm.SearchResults.Count);
            Assert.Equal("5 of 2,777 shown", vm.SearchStatus);
            Assert.True(vm.HasMoreResults);
            var p = vm.Profile!;
            Assert.NotNull(p);
            Assert.Equal("Tester", p.Name);
            Assert.Equal("35.2K", p.CombatPowerText);
            Assert.Equal("797", p.ItemLevelText);
            Assert.Equal("Sorcerer · Lv 45 · Fregion · Elyos", p.Subtitle);
            Assert.Equal(9, p.WeaponsAndArmor.Count);
            Assert.Equal(9, p.Accessories.Count);
            Assert.Equal(2, p.Companions.Count);
            Assert.Equal(89, p.BoardNodes.Count);
            Assert.True(p.Boards[0].IsSelected);
            Assert.Equal(6, p.BoardStatEffects.Count);
            Assert.False(p.IsLoadingEquipment);
            Assert.Null(vm.ProfileError);

            Sta.Wait(vm.OpenItemAsync(p.WeaponsAndArmor[0]));
            Assert.True(vm.IsItemPanelOpen);
            Assert.NotNull(vm.ItemDetail);
            Assert.Equal(4, vm.ItemDetail!.Manastones.Count);
            vm.CloseItem();
            Assert.False(vm.IsItemPanelOpen);
        });
    }

    [Fact]
    public void Not_found_character_shows_error_state()
    {
        Sta.Run(() =>
        {
            var client = TestClients.Create(new FakeHandler()
                .OnFixture("/api/gameinfo/servers", "servers_global_eu.json")
                .OnFixture("/search/v2/character", "search_global_eu.json")
                .OnFixture("/api/character/info", "error_not_found.json", HttpStatusCode.NotFound));
            var vm = new CharacterLookupViewModel(client, loadIcons: false);
            vm.SearchText = "tester";
            Sta.Wait(vm.SearchAsync());
            Sta.Wait(vm.OpenCharacterAsync(vm.SearchResults[0].Result));
            Assert.Null(vm.Profile);
            Assert.Contains("Character not found", vm.ProfileError);
            Assert.False(vm.ShowPlaceholder);
        });
    }

    [Fact]
    public void Renders_profile_offscreen_to_png()
    {
        Sta.Run(() =>
        {
            var (view, vm) = LoadedView(FakeHandler.GlobalFixtures());
            var path = Render(view, TestTheme.Dark(), 1280, 1500, "armory-profile-dark.png");
            Assert.True(new FileInfo(path).Length > 10_000);

            Sta.Wait(vm.OpenItemAsync(vm.Profile!.WeaponsAndArmor[0]));
            path = Render(view, TestTheme.Dark(), 1280, 860, "armory-item-detail-dark.png");
            Assert.True(new FileInfo(path).Length > 10_000);
        });
    }

    [Fact]
    public void Renders_light_theme_and_error_states_offscreen()
    {
        Sta.Run(() =>
        {
            var (view, _) = LoadedView(FakeHandler.GlobalFixtures());
            Render(view, TestTheme.Light(), 1280, 1500, "armory-profile-light.png");

            var client = TestClients.Create(new FakeHandler()
                .OnFixture("/search/v2/character", "search_global_eu.json")
                .On("/api/character/info", "{}", HttpStatusCode.ServiceUnavailable)
                .On("/characters/index", "<html></html>", contentType: "text/html"));
            var errView = new CharacterLookupView(client, loadIcons: false);
            var vm = errView.ViewModel!;
            Sta.Wait(vm.LoadServersAsync());
            vm.SearchText = "tester";
            Sta.Wait(vm.SearchAsync());
            Sta.Wait(vm.OpenCharacterAsync(vm.SearchResults[0].Result));
            Assert.NotNull(vm.ServerError);
            Assert.NotNull(vm.ProfileError);
            var path = Render(errView, TestTheme.Dark(), 1280, 600, "armory-error-dark.png");
            Assert.True(File.Exists(path));

            var empty = new CharacterLookupView(TestClients.Create(new FakeHandler()), loadIcons: false);
            Render(empty, TestTheme.Dark(), 1280, 600, "armory-empty-dark.png");
        });
    }
}
