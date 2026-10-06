namespace Aion2Dps.App.Infrastructure;

/// <summary>
/// Named-mutex single-instance guard. A second launch signals the first instance (named event) to show its
/// dashboard, then exits.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly RegisteredWaitHandle? _registration;

    private SingleInstanceGuard(Mutex mutex, EventWaitHandle activate, Action onActivate)
    {
        _mutex = mutex;
        _activate = activate;
        _registration = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Returns a guard when this is the first instance; otherwise signals the running instance and returns null.</summary>
    public static SingleInstanceGuard? TryAcquire(string name, Action onActivateRequested)
    {
        var mutex = new Mutex(initiallyOwned: true, $"Local\\{name}.SingleInstance", out bool created);
        var evt = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\{name}.Activate");
        if (!created)
        {
            try { evt.Set(); } catch { /* ignore */ }
            evt.Dispose();
            mutex.Dispose();
            return null;
        }
        return new SingleInstanceGuard(mutex, evt, onActivateRequested);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activate.Dispose();
        try { _mutex.ReleaseMutex(); } catch { /* not owned on this thread */ }
        _mutex.Dispose();
    }
}
