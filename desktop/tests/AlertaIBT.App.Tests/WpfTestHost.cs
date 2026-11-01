using System.Windows;
using System.Windows.Threading;

namespace AlertaIBT.App.Tests;

/// <summary>
/// One STA thread with a WPF dispatcher and the real <see cref="App"/> resources (theme, icons, fonts) for the whole test run.
/// WPF objects belong to the thread that made them and only one <see cref="Application"/> can exist per process, so every
/// test that touches WPF runs its body here.
/// </summary>
internal static class WpfTestHost
{
    private static readonly Lazy<Dispatcher> Host = new(Start);

    // One test body at a time: a body that pumps the dispatcher (a nested frame, for the layout and input of a shown window) would otherwise also
    // run the bodies that other test classes (running in parallel) have queued, in the middle of its own steps.
    private static readonly object Gate = new();

    private static int _depth;

    public static Dispatcher Dispatcher => Host.Value;

    // Every body is watched by the window guard (TestWindows.cs): a layout test whose window ends up on a monitor fails with the list.
    private static T Guarded<T>(Func<T> run)
    {
        var outermost = _depth++ == 0;
        if (outermost)
        {
            WindowGuard.Begin();
        }

        T result;
        try
        {
            result = run();
        }
        catch
        {
            _depth--;
            if (outermost)
            {
                WindowGuard.Abort();
            }

            throw;
        }

        _depth--;
        if (outermost)
        {
            WindowGuard.End();
        }

        return result;
    }

    /// <summary>Runs <paramref name="body"/> on the WPF thread and returns its result; exceptions are rethrown to the caller.</summary>
    public static T Run<T>(Func<T> body)
    {
        lock (Gate)
        {
            return Guarded(() => Dispatcher.Invoke(body));
        }
    }

    /// <summary>Runs <paramref name="body"/> on the WPF thread.</summary>
    public static void Run(Action body)
    {
        lock (Gate)
        {
            Guarded(() =>
            {
                Dispatcher.Invoke(body);
                return 0;
            });
        }
    }

    /// <summary>Runs an async <paramref name="body"/> on the WPF thread and waits for it while the dispatcher keeps pumping.</summary>
    public static T RunAsync<T>(Func<Task<T>> body)
    {
        lock (Gate)
        {
            return Guarded(() => Dispatcher.InvokeAsync(body).Task.Unwrap().GetAwaiter().GetResult());
        }
    }

    private static Dispatcher Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() =>
        {
            try
            {
                App.RunsApplication = false; // before the App exists: it posts its own OnStartup to this dispatcher
                var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
                WindowGuard.Start();
                ready.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                ready.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "WPF test host",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }
}
