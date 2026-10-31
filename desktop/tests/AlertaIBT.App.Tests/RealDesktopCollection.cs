using System.Runtime.InteropServices;

namespace AlertaIBT.App.Tests;

/// <summary>
/// Tests that depend on the real desktop: the real cursor, real shown windows and popups, the real screen, or real timers and threads whose timing
/// a busy PC would stretch (the UI-stall watchdog, the NDI window, the thread-pool probes). A collection that disables parallelization runs alone,
/// after the parallel ones, one class at a time: nothing else of the suite is competing for the cursor, the dispatcher or the CPU meanwhile.
/// It also keeps the screen awake (see <see cref="DesktopAwake"/>).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealDesktopCollection : ICollectionFixture<DesktopAwake>
{
    public const string Name = "real desktop";
}

/// <summary>
/// While the real-desktop tests run, tells Windows the display is in use. Without it an idle PC (a test run is not user input) dims the screen after a
/// few minutes: Windows puts a topmost "Shell_SystemDim" window over everything, WPF then sees that window under the cursor, and no mouse report reaches
/// the test window any more (every real-mouse test failed on a run that began minutes after the person left), and the screen pixels read by the NDI
/// test come out darkened (141,0,0 instead of red). It is not input: nothing is moved or pressed.
/// </summary>
public sealed class DesktopAwake : IDisposable
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    private readonly ManualResetEventSlim _stop = new();
    private readonly Thread _thread;

    public DesktopAwake()
    {
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            // The state belongs to the thread that set it: this one lives until the collection is done.
            SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired);
            ready.Set();
            _stop.Wait();
            SetThreadExecutionState(EsContinuous);
        })
        { IsBackground = true, Name = "desktop awake" };
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Resets the display idle timer once more (a test that finds the screen dimmed calls it and looks again).</summary>
    public static void Poke() => SetThreadExecutionState(EsDisplayRequired);

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}
