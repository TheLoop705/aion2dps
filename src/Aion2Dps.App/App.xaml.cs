using System.Threading.Tasks;
using Aion2Dps.App.Infrastructure;
using Aion2Dps.App.Integration;
using Aion2Dps.App.Rendering;

namespace Aion2Dps.App;

/// <summary>
/// Entry point. Command line: <c>--demo</c>, <c>--sim</c>, <c>--replay &lt;file&gt; [--speed x]</c>,
/// <c>--render-screens &lt;dir&gt;</c> (real simulated pipeline rendered offscreen to PNGs, no windows),
/// <c>--render-demo-screens &lt;dir&gt;</c> (design catalog from the demo fakes), <c>--no-overlay</c>, <c>--allow-multiple</c>, <c>--autostart</c> (started by Windows: quiet start in the tray).
/// </summary>
public partial class App : Application
{
    private AppHost? _host;
    private SingleInstanceGuard? _guard;
    private int _uiErrors;
    private DateTime _uiErrorWindow = DateTime.UtcNow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        InstallExceptionHandlers();
        var options = LaunchOptions.Parse(e.Args);

        if (options.RenderScreensDir is not null || options.RenderDemoScreensDir is not null)
        {
            int code = 0;
            try
            {
                if (options.RenderScreensDir is { } dir)
                    foreach (var file in RealScreens.RenderAll(Path.GetFullPath(dir)))
                        Console.WriteLine(file);
                if (options.RenderDemoScreensDir is { } demoDir)
                    foreach (var file in ScreenCatalog.RenderAll(Path.GetFullPath(demoDir)))
                        Console.WriteLine(file);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                code = 1;
            }
            Shutdown(code);
            return;
        }

        if (!options.AllowMultiple)
        {
            // A second launch opens the running meter's dashboard, except Windows' delayed autostart: it must not pop up
            // (and focus) a dashboard over a game the user may be starting.
            _guard = SingleInstanceGuard.TryAcquire("Aion2Dps", () => Dispatcher.BeginInvoke(() => _host?.OpenDashboard(null)),
                signalExisting: !options.Autostart);
            if (_guard is null)
            {
                Shutdown(0);
                return;
            }
        }

        try
        {
            _host = new AppHost(this, options);
            _host.Start();
        }
        catch (Exception ex)
        {
            AppLog.Error("App", "Start-up failed", ex);
            MessageBox.Show("Aion2Dps could not start:\n\n" + ex.Message, "Aion2Dps", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        _guard?.Dispose();
        base.OnExit(e);
    }

    private void InstallExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("App", "Unhandled UI exception", args.Exception);
            // Keep running: a broken refresh must not take the meter down mid-fight. Give up only on a crash loop.
            if ((DateTime.UtcNow - _uiErrorWindow).TotalMinutes > 1) { _uiErrorWindow = DateTime.UtcNow; _uiErrors = 0; }
            args.Handled = ++_uiErrors < 50;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("App", $"Unhandled exception (terminating: {args.IsTerminating})", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("App", "Unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }
}
