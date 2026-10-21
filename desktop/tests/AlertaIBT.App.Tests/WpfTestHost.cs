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

    public static Dispatcher Dispatcher => Host.Value;

    /// <summary>Runs <paramref name="body"/> on the WPF thread and returns its result; exceptions are rethrown to the caller.</summary>
    public static T Run<T>(Func<T> body) => Dispatcher.Invoke(body);

    /// <summary>Runs <paramref name="body"/> on the WPF thread.</summary>
    public static void Run(Action body) => Dispatcher.Invoke(body);

    /// <summary>Runs an async <paramref name="body"/> on the WPF thread and waits for it while the dispatcher keeps pumping.</summary>
    public static T RunAsync<T>(Func<Task<T>> body) => Dispatcher.InvokeAsync(body).Task.Unwrap().GetAwaiter().GetResult();

    private static Dispatcher Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
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
