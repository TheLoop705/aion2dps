using System.Runtime.ExceptionServices;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Aion2Dps.App.Tests;

internal static class Sta
{
    /// <summary>Runs <paramref name="action"/> on a fresh STA thread (WPF objects need one) and rethrows failures.</summary>
    public static void Run(Action action)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        if (!t.Join(TimeSpan.FromMinutes(3))) throw new TimeoutException("STA test timed out");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}

internal static class Repo
{
    /// <summary>Repository root (folder containing Aion2Dps.sln), or the test output folder as a fallback.</summary>
    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Aion2Dps.sln"))) dir = dir.Parent;
            return dir?.FullName ?? AppContext.BaseDirectory;
        }
    }

    public static string ScreensDir => Path.Combine(Root, "artifacts", "screens");
}
