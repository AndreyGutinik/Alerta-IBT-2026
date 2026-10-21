namespace AlertaIBT.App.Platform;

/// <summary>
/// Only one Alerta-IBT per Windows session. A second copy does not start: it asks the first one to show its window and exits.
/// The lock is a named mutex, so it is owned by the thread that took it; take it and release it on the same thread (the
/// UI thread in the app).
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>The name the app uses for its mutex and activation event.</summary>
    public const string DefaultName = "AlertaIBT";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activation;
    private RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle activation)
    {
        _mutex = mutex;
        _activation = activation;
    }

    /// <summary>
    /// Takes the lock, or returns null when another copy holds it after waiting <paramref name="wait"/> (zero for a
    /// first start; a few seconds after an automatic restart, while the old process is still leaving).
    /// </summary>
    /// <param name="wait">How long to wait for a running copy to let go.</param>
    /// <param name="name">The shared name; only tests use anything but <see cref="DefaultName"/>.</param>
    public static SingleInstance? TryAcquire(TimeSpan wait, string name = DefaultName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var mutex = new Mutex(initiallyOwned: false, MutexName(name));
        bool owned;
        try
        {
            owned = mutex.WaitOne(wait);
        }
        catch (AbandonedMutexException)
        {
            owned = true; // the previous copy died without letting go: the lock is ours now
        }

        if (!owned)
        {
            mutex.Dispose();
            return null;
        }

        return new SingleInstance(mutex, new EventWaitHandle(false, EventResetMode.AutoReset, EventName(name)));
    }

    /// <summary>Asks the running copy to show its window. False when no copy is listening.</summary>
    public static bool SignalExisting(string name = DefaultName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!EventWaitHandle.TryOpenExisting(EventName(name), out var activation))
        {
            return false;
        }

        using (activation)
        {
            return activation.Set();
        }
    }

    /// <summary>Calls <paramref name="onActivate"/> (on a thread-pool thread) every time a second copy signals.</summary>
    public void ListenForActivation(Action onActivate)
    {
        ArgumentNullException.ThrowIfNull(onActivate);

        _registration = ThreadPool.RegisterWaitForSingleObject(_activation, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Lets go of the lock so a new copy can start.</summary>
    public void Dispose()
    {
        _registration?.Unregister(null);
        _activation.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not the owner (left from another thread): nothing to release.
        }

        _mutex.Dispose();
    }

    private static string MutexName(string name) => $@"Local\{name}.SingleInstance";

    private static string EventName(string name) => $@"Local\{name}.ShowWindow";
}
