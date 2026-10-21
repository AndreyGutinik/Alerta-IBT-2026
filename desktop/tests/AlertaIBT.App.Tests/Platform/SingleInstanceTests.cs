using AlertaIBT.App.Platform;

namespace AlertaIBT.App.Tests.Platform;

public sealed class SingleInstanceTests
{
    private static string UniqueName() => "AlertaIBT.Test." + Guid.NewGuid().ToString("N");

    // A named mutex is recursive for the thread that owns it, so "another process" is simulated with another thread.
    private static T OnAnotherThread<T>(Func<T> body) => Task.Factory.StartNew(body, TaskCreationOptions.LongRunning).GetAwaiter().GetResult();

    [Fact]
    public void The_app_uses_the_AlertaIBT_name()
    {
        Assert.Equal("AlertaIBT", SingleInstance.DefaultName);
    }

    [Fact]
    public void A_second_copy_cannot_take_the_lock_until_the_first_lets_go()
    {
        var name = UniqueName();
        var first = SingleInstance.TryAcquire(TimeSpan.Zero, name);
        Assert.NotNull(first);

        Assert.Null(OnAnotherThread(() => SingleInstance.TryAcquire(TimeSpan.Zero, name)));

        first.Dispose();

        var third = OnAnotherThread(() =>
        {
            var instance = SingleInstance.TryAcquire(TimeSpan.Zero, name);
            instance?.Dispose();
            return instance;
        });
        Assert.NotNull(third);
    }

    [Fact]
    public void Signalling_wakes_the_listener_of_the_running_copy()
    {
        var name = UniqueName();
        using var first = SingleInstance.TryAcquire(TimeSpan.Zero, name)!;
        using var woken = new ManualResetEventSlim();
        first.ListenForActivation(woken.Set);

        Assert.True(SingleInstance.SignalExisting(name));

        Assert.True(woken.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Signalling_with_no_running_copy_is_harmless()
    {
        Assert.False(SingleInstance.SignalExisting(UniqueName()));
    }

    [Fact]
    public async Task A_restarted_copy_waits_for_the_old_one_to_leave()
    {
        var name = UniqueName();
        using var held = new ManualResetEventSlim();
        using var leave = new ManualResetEventSlim();

        // The old copy takes and releases the lock on one thread (a mutex belongs to the thread that took it).
        var owner = Task.Factory.StartNew(
            () =>
            {
                var instance = SingleInstance.TryAcquire(TimeSpan.Zero, name)!;
                held.Set();
                leave.Wait();
                instance.Dispose();
            },
            TaskCreationOptions.LongRunning);
        Assert.True(held.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        // The new copy waits up to 5 s for it.
        var waiter = Task.Factory.StartNew(
            () =>
            {
                var instance = SingleInstance.TryAcquire(TimeSpan.FromSeconds(5), name);
                instance?.Dispose();
                return instance;
            },
            TaskCreationOptions.LongRunning);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(waiter.IsCompleted);
        leave.Set();

        Assert.NotNull(await waiter);
        await owner;
    }
}
