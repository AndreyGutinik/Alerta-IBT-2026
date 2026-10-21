using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.ViewModels;

public sealed class AutosaverTests : IDisposable
{
    private readonly SettingsRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static Func<AppSettings, AppSettings> Height(int value) => s => s with { Bar = s.Bar with { Height = value } };

    [Fact]
    public void The_delay_is_the_600_ms_of_the_plan()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(600), Autosaver.Delay);
    }

    [Fact]
    public void A_change_is_saved_600_ms_after_it_and_not_before()
    {
        _rig.Saver.Queue("h", Height(80));

        Assert.Equal(56, _rig.Settings.Current.Bar.Height);
        Assert.Equal(SaveState.Saving, _rig.Saver.State);
        _rig.Clock.Advance(TimeSpan.FromMilliseconds(599));
        Assert.Equal(56, _rig.Settings.Current.Bar.Height);
        Assert.False(File.Exists(_rig.SettingsPath));

        _rig.Clock.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal(80, _rig.Settings.Current.Bar.Height);
        Assert.Equal(80, _rig.OnDisk().Bar.Height); // written at once, not another 600 ms later
        Assert.Equal(SaveState.Saved, _rig.Saver.State);
        Assert.StartsWith("\U00002713 Salvo", _rig.Saver.Label);
    }

    [Fact]
    public void Every_new_change_restarts_the_wait()
    {
        _rig.Saver.Queue("a", Height(70));
        _rig.Clock.Advance(TimeSpan.FromMilliseconds(500));
        _rig.Saver.Queue("b", s => s with { Bar = s.Bar with { Speed = 120 } });
        _rig.Clock.Advance(TimeSpan.FromMilliseconds(500));

        Assert.Equal(56, _rig.Settings.Current.Bar.Height);

        _rig.Clock.Advance(TimeSpan.FromMilliseconds(100));

        Assert.Equal(70, _rig.Settings.Current.Bar.Height);
        Assert.Equal(120, _rig.Settings.Current.Bar.Speed);
    }

    [Fact]
    public void All_the_waiting_changes_go_through_one_update()
    {
        _rig.Saver.Queue("a", Height(70));
        _rig.Saver.Queue("b", s => s with { Bar = s.Bar with { Speed = 120 } });
        _rig.Saver.Queue("c", s => s with { System = s.System with { Hotkey = "Ctrl+Alt+F9" } });

        _rig.Rest();

        Assert.Equal(1, _rig.ChangedCount);
    }

    [Fact]
    public void A_newer_change_of_the_same_control_replaces_the_waiting_one()
    {
        _rig.Saver.Queue("h", Height(70));
        _rig.Saver.Queue("h", Height(90));

        _rig.Rest();

        Assert.Equal(90, _rig.Settings.Current.Bar.Height);
        Assert.Equal(1, _rig.ChangedCount);
    }

    [Fact]
    public void A_cancelled_change_is_never_saved_and_the_stamp_goes_back_to_saved()
    {
        _rig.Saver.Queue("h", Height(70));

        _rig.Saver.Cancel("h");
        _rig.Rest();

        Assert.Equal(56, _rig.Settings.Current.Bar.Height);
        Assert.False(File.Exists(_rig.SettingsPath));
        Assert.Equal(SaveState.Saved, _rig.Saver.State);
        Assert.Equal(0, _rig.ChangedCount);
    }

    [Fact]
    public void Cancelling_one_control_keeps_the_others()
    {
        _rig.Saver.Queue("h", Height(70));
        _rig.Saver.Queue("s", s => s with { Bar = s.Bar with { Speed = 120 } });

        _rig.Saver.Cancel("h");
        _rig.Rest();

        Assert.Equal(56, _rig.Settings.Current.Bar.Height);
        Assert.Equal(120, _rig.Settings.Current.Bar.Speed);
    }

    [Fact]
    public void A_change_is_applied_to_the_settings_of_the_moment_so_a_value_written_meanwhile_survives()
    {
        _rig.Saver.Queue("h", Height(70));
        _rig.Settings.Update(s => s with { Telegram = s.Telegram with { Offset = 4242 }, System = s.System with { LastLedDevicePath = "DISPLAY2" } });

        _rig.Rest();

        Assert.Equal(70, _rig.Settings.Current.Bar.Height);
        Assert.Equal(4242, _rig.Settings.Current.Telegram.Offset);
        Assert.Equal("DISPLAY2", _rig.OnDisk().System.LastLedDevicePath);
    }

    [Fact]
    public void A_value_out_of_range_is_held_in_range_by_the_settings()
    {
        _rig.Saver.Queue("h", Height(5000));

        _rig.Rest();

        Assert.Equal(BarSettings.MaxHeight, _rig.OnDisk().Bar.Height);
    }

    [Fact]
    public void FlushNow_saves_at_once_and_a_later_rest_does_nothing_more()
    {
        _rig.Saver.Queue("h", Height(70));

        _rig.Saver.FlushNow();

        Assert.Equal(70, _rig.OnDisk().Bar.Height);
        Assert.False(_rig.Saver.HasPending);
        var changes = _rig.ChangedCount;
        _rig.Rest();
        Assert.Equal(changes, _rig.ChangedCount);
    }

    [Fact]
    public void Dispose_saves_the_change_that_was_still_waiting()
    {
        _rig.Saver.Queue("h", Height(70));

        _rig.Saver.Dispose();

        Assert.Equal(70, _rig.OnDisk().Bar.Height);
    }

    [Fact]
    public void Nothing_queued_after_dispose()
    {
        _rig.Saver.Dispose();

        _rig.Saver.Queue("h", Height(70));
        _rig.Rest();

        Assert.Equal(56, _rig.Settings.Current.Bar.Height);
    }

    [Fact]
    public void The_after_step_runs_once_per_saved_change_and_not_for_a_replaced_one()
    {
        var first = 0;
        var second = 0;
        _rig.Saver.Queue("rules", Height(70), () => first++);
        _rig.Saver.Queue("rules", Height(71), () => second++);

        Assert.Equal(0, second);
        _rig.Rest();
        _rig.Rest();

        Assert.Equal(0, first);
        Assert.Equal(1, second);
    }

    [Fact]
    public void A_failing_save_shows_the_failed_stamp_and_a_later_success_clears_it()
    {
        var blocker = Path.Combine(_rig.Folder, "blocker");
        File.WriteAllText(blocker, "x");
        var blockedPath = Path.Combine(blocker, "settings.json");
        using var blocked = new SettingsService(new SettingsStore(blockedPath, _rig.Clock), _rig.Clock);
        var saver = new Autosaver(blocked, clock: _rig.Clock);

        saver.Queue("h", Height(70));
        _rig.Rest();

        Assert.Equal(SaveState.Failed, saver.State);
        Assert.Contains("N\U000000E3o foi poss\U000000EDvel salvar", saver.Label);
        Assert.Equal(70, blocked.Current.Bar.Height); // kept in memory: the bar still follows

        File.Delete(blocker);
        saver.Queue("h", Height(72));
        _rig.Rest();

        Assert.Equal(SaveState.Saved, saver.State);
        Assert.Equal(72, new SettingsStore(blockedPath, _rig.Clock).Load().Settings.Bar.Height);
    }

    [Fact]
    public void An_edit_that_throws_is_reported_as_failed_and_does_not_crash()
    {
        _rig.Saver.Queue("bad", _ => throw new InvalidOperationException("boom"));

        _rig.Rest();

        Assert.Equal(SaveState.Failed, _rig.Saver.State);
        Assert.Equal(56, _rig.Settings.Current.Bar.Height);
    }

    [Fact]
    public void The_applied_event_fires_after_each_batch()
    {
        var applied = 0;
        _rig.Saver.Applied += () => applied++;

        _rig.Saver.Queue("a", Height(70));
        _rig.Rest();
        _rig.Saver.Queue("a", Height(72));
        _rig.Rest();

        Assert.Equal(2, applied);
    }

    [Fact]
    public void The_stamp_says_when_it_was_saved()
    {
        _rig.Saver.Queue("a", Height(70));
        _rig.Rest();

        Assert.Matches("^\U00002713 Salvo \U000000E0s [0-9]{2}:[0-9]{2}:[0-9]{2}$", _rig.Saver.Label);
    }
}
