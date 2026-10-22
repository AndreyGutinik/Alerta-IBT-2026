using System.Windows.Threading;
using AlertaIBT.App.Platform;
using AlertaIBT.App.Tests.ViewModels;
using AlertaIBT.Core.Input;
using AlertaIBT.Core.Screens;

namespace AlertaIBT.App.Tests.Platform;

// The watchdog and the system-event subscriptions around the lock, on a fake cursor.
public sealed class MouseLockSignalsTests
{
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static MouseLockController Controller(SettingsRig rig, FakeCursor cursor)
    {
        var controller = new MouseLockController(rig.Settings, new MouseLockService(cursor), new FakeKeyboardHook(), action => action());
        controller.Start(new FakeHotkeyRegistrar());
        return controller;
    }

    [Fact]
    public void The_watchdog_runs_only_while_the_lock_is_on()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new SettingsRig();
            var cursor = new FakeCursor();
            using var controller = Controller(rig, cursor);
            using var signals = new MouseLockSignals(controller, Dispatcher.CurrentDispatcher);

            Assert.False(signals.WatchdogRunning); // off: no timer waking the PC up every second

            controller.Toggle();
            Assert.True(signals.WatchdogRunning);

            controller.Toggle();
            Assert.False(signals.WatchdogRunning);
        });
    }

    [Fact]
    public void A_lock_that_starts_on_starts_the_watchdog_at_once()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new SettingsRig();
            var cursor = new FakeCursor();
            using var controller = Controller(rig, cursor);
            controller.Toggle();

            using var signals = new MouseLockSignals(controller, Dispatcher.CurrentDispatcher);

            Assert.True(signals.WatchdogRunning);
        });
    }

    [Fact]
    public void Disposing_stops_the_watchdog_and_later_changes_are_ignored()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new SettingsRig();
            var cursor = new FakeCursor();
            using var controller = Controller(rig, cursor);
            var signals = new MouseLockSignals(controller, Dispatcher.CurrentDispatcher);
            controller.Toggle();

            signals.Dispose();
            signals.Dispose(); // twice is fine
            controller.Toggle();
            controller.Toggle();

            Assert.False(signals.WatchdogRunning);
        });
    }

    [Fact]
    public void The_watchdog_tick_puts_a_released_clip_back_within_a_couple_of_seconds()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new SettingsRig();
            var cursor = new FakeCursor();
            using var controller = Controller(rig, cursor);
            using var signals = new MouseLockSignals(controller, Dispatcher.CurrentDispatcher);
            controller.Toggle();
            cursor.Unclip(); // a lock screen, remote desktop or a game released it

            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < deadline && cursor.Clip != new ScreenRect(0, 0, 1920, 1080))
            {
                Pump();
                Thread.Sleep(25);
            }

            Assert.Equal(new ScreenRect(0, 0, 1920, 1080), cursor.Clip);
        });
    }
}
