using AlertaIBT.App.Hosting;
using AlertaIBT.App.Platform;

namespace AlertaIBT.App.Tests.Platform;

// A test run must never lock, wake or write the copy of the app the church really uses (the owner saw the real window flash and
// pop up while the tests ran, and ~50 tests failed whenever the real app was open).
[Collection("process environment")] // these tests read or change process-wide environment variables
public sealed class TestIsolationTests
{
    private const string RealName = "AlertaIBT";

    // A named mutex belongs to the thread that took it: "another process" is another thread (the tests take and release on one).
    private static T OnAnotherThread<T>(Func<T> body) => Task.Factory.StartNew(body, TaskCreationOptions.LongRunning).GetAwaiter().GetResult();

    private static void OnAnotherThread(Action body) => OnAnotherThread(() =>
    {
        body();
        return 0;
    });

    private static string UniqueName() => "AlertaIBT.Test." + Guid.NewGuid().ToString("N");

    [Fact]
    public void Without_the_variable_the_name_is_the_real_one_and_the_variable_replaces_it()
    {
        var before = Environment.GetEnvironmentVariable(SingleInstance.InstanceNameVariable);
        try
        {
            Environment.SetEnvironmentVariable(SingleInstance.InstanceNameVariable, null);
            Assert.Equal(RealName, SingleInstance.ResolveName());

            Environment.SetEnvironmentVariable(SingleInstance.InstanceNameVariable, "   ");
            Assert.Equal(RealName, SingleInstance.ResolveName()); // blank counts as not set

            Environment.SetEnvironmentVariable(SingleInstance.InstanceNameVariable, " Other.Name ");
            Assert.Equal("Other.Name", SingleInstance.ResolveName());
        }
        finally
        {
            Environment.SetEnvironmentVariable(SingleInstance.InstanceNameVariable, before);
        }
    }

    [Fact]
    public void Two_different_names_do_not_see_each_other()
    {
        var first = UniqueName();
        var second = UniqueName();
        using var a = SingleInstance.TryAcquire(TimeSpan.Zero, first)!;
        var wokeA = 0;
        a.ListenForActivation(() => Interlocked.Increment(ref wokeA));

        // The other name is free (the lock of the first does not hold it) and nobody listens there.
        Assert.False(SingleInstance.SignalExisting(second));
        var other = OnAnotherThread(() => SingleInstance.TryAcquire(TimeSpan.Zero, second));
        Assert.NotNull(other);
        OnAnotherThread(() => other.Dispose());

        Thread.Sleep(200);
        Assert.Equal(0, Volatile.Read(ref wokeA)); // the first never heard the second

        Assert.True(SingleInstance.SignalExisting(first));
        SpinWait.SpinUntil(() => Volatile.Read(ref wokeA) > 0, TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref wokeA));
    }

    [Fact]
    public void The_test_process_runs_under_its_own_name_and_data_folder_never_the_real_ones()
    {
        Assert.NotEqual(RealName, SingleInstance.ResolveName());
        Assert.Equal(TestIsolation.InstanceName, SingleInstance.ResolveName());
        Assert.Equal(TestIsolation.DataFolder, AppPaths.Default.DataFolder);
        Assert.NotEqual(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlertaIBT"),
            AppPaths.Default.DataFolder);
    }

    [Fact]
    public void The_wpf_test_host_does_not_run_the_real_startup()
    {
        // The Application posts its own OnStartup to the dispatcher; the host turns the real one off, so nothing took the lock.
        Assert.False(WpfTestHost.Run(() => App.RunsApplication));
        var free = OnAnotherThread(() => SingleInstance.TryAcquire(TimeSpan.Zero));
        Assert.NotNull(free);
        OnAnotherThread(() => free.Dispose());
    }

    [Fact]
    public void A_stand_in_holding_the_real_name_is_never_signaled_and_does_not_get_in_the_way()
    {
        // The stand-in for the church's copy: the real mutex and "show your window" event.
        using var mutex = new Mutex(true, $@"Local\{RealName}.SingleInstance", out var mutexIsOurs);
        using var activation = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{RealName}.ShowWindow", out var eventIsOurs);
        Assert.SkipUnless(mutexIsOurs && eventIsOurs, "the real Alerta-IBT is running: a stand-in cannot be made, and it must not be signaled");

        // What a test run does: the resolved (isolated) name.
        var instance = OnAnotherThread(() => SingleInstance.TryAcquire(TimeSpan.Zero));
        Assert.NotNull(instance); // the real name being taken does not stop the test run from getting its own lock
        OnAnotherThread(() => instance.Dispose());

        Assert.False(SingleInstance.SignalExisting()); // the resolved name: nobody there
        Assert.False(activation.WaitOne(0), "the stand-in was signaled");

        // And the WPF host (where the app's resources live) works with the real name taken.
        Assert.Equal(1, WpfTestHost.Run(() => 1));
        Assert.False(activation.WaitOne(0), "the stand-in was signaled");

        mutex.ReleaseMutex();
    }
}
