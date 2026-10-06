using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Aion2Dps.Contracts;

namespace Aion2Dps.Analysis.Tests;

/// <summary>Runs WPF test bodies on a dedicated STA thread with a dispatcher synchronization context.</summary>
internal static class Sta
{
    public static void Run(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                body();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(3))) throw new TimeoutException("STA test body timed out");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>Pumps the dispatcher until <paramref name="task"/> completes (async UI code continues on this thread).</summary>
    public static void Wait(Task? task, int timeoutMs = 30000)
    {
        if (task is null) return;
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(timeoutMs), DispatcherPriority.Send, (_, _) => frame.Continue = false, dispatcher);
        timer.Start();
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        timer.Stop();
        if (!task.IsCompleted) throw new TimeoutException("Task did not complete");
        task.GetAwaiter().GetResult();
        DoEvents();
    }

    public static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}

/// <summary>Offscreen layout and PNG output.</summary>
internal static class Render
{
    public static string RepoRoot { get; } = FindRoot();

    public static string ScreensDir => Path.Combine(RepoRoot, "artifacts", "screens");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Aion2Dps.sln"))) dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    /// <summary>Puts <paramref name="content"/> in a themed host and lays it out at the given size.</summary>
    public static System.Windows.Controls.Border Host(FrameworkElement content, bool dark, double width, double height)
    {
        var host = new System.Windows.Controls.Border { Width = width, Height = height, Child = content };
        host.Resources = TestTheme.Create(dark);
        host.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, ThemeKeys.WindowBackground);
        Layout(host, width, height);
        return host;
    }

    public static void Layout(FrameworkElement host, double width, double height)
    {
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
        Sta.DoEvents();
        host.UpdateLayout();
    }

    public static BitmapSource Snap(FrameworkElement host)
    {
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(host);
        rtb.Freeze();
        return rtb;
    }

    public static string Save(BitmapSource bmp, string name)
    {
        string path = Path.Combine(ScreensDir, name + ".png");
        ImageExport.SavePng(bmp, path);
        return path;
    }

    /// <summary>Number of distinct colours in a coarse sample (a blank render has 1).</summary>
    public static int DistinctColors(BitmapSource bmp)
    {
        int stride = bmp.PixelWidth * 4;
        var px = new byte[stride * bmp.PixelHeight];
        bmp.CopyPixels(px, stride, 0);
        var set = new HashSet<int>();
        for (int y = 0; y < bmp.PixelHeight; y += 3)
        for (int x = 0; x < bmp.PixelWidth; x += 3)
        {
            int i = y * stride + x * 4;
            set.Add(px[i] | px[i + 1] << 8 | px[i + 2] << 16);
            if (set.Count > 64) return set.Count;
        }
        return set.Count;
    }

    public static string Theme(bool dark) => dark ? "dark" : "light";
}
