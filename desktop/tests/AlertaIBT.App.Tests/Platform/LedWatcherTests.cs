using AlertaIBT.App.Platform;
using AlertaIBT.Core.Screens;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.App.Tests.Platform;

public sealed class LedWatcherTests
{
    private static readonly ScreenInfo Primary = new("p", @"\\.\DISPLAY1", 0, 0, 1920, 1080, true);
    private static readonly ScreenInfo Led = new("led-1", @"\\.\DISPLAY2", 1920, 0, 2560, 768, false);

    private readonly FakeTimeProvider _clock = new();
    private readonly List<ScreenInfo> _screens = [Primary, Led];
    private readonly List<ScreenInfo?> _events = [];
    private readonly List<string> _remembered = [];
    private readonly List<Exception> _errors = [];

    private LedWatcher Watcher(bool testMode = false, Func<IReadOnlyList<ScreenInfo>>? enumerate = null)
    {
        var tracker = new LedTracker(enumerate ?? (() => _screens.ToList()), () => null, testMode);
        var watcher = new LedWatcher(tracker, _remembered.Add, _errors.Add, _clock);
        watcher.LedChanged += _events.Add;
        return watcher;
    }

    [Fact]
    public void Start_finds_the_led_without_raising_the_event_and_remembers_its_path()
    {
        using var watcher = Watcher();

        watcher.Start();

        Assert.Equal(Led, watcher.Led);
        Assert.Empty(_events);
        Assert.Equal(["led-1"], _remembered);
    }

    [Fact]
    public void The_five_second_check_catches_a_led_that_vanished()
    {
        using var watcher = Watcher();
        watcher.Start();

        _screens.Remove(Led);
        _clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Empty(_events);
        _clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal([(ScreenInfo?)null], _events);
        Assert.Null(watcher.Led);
    }

    [Fact]
    public void The_led_coming_back_is_announced_and_the_unchanged_checks_are_silent()
    {
        using var watcher = Watcher();
        _screens.Remove(Led);
        watcher.Start();
        Assert.Null(watcher.Led);

        _clock.Advance(TimeSpan.FromSeconds(15)); // three checks, nothing new
        Assert.Empty(_events);

        _screens.Add(Led);
        _clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal([(ScreenInfo?)Led], _events);
        Assert.Equal(["led-1"], _remembered);
    }

    [Fact]
    public void A_display_change_message_is_acted_on_after_a_short_pause_not_at_once()
    {
        using var watcher = Watcher();
        watcher.Start();
        _screens.Remove(Led);

        watcher.NotifyDisplayChanged();
        _clock.Advance(TimeSpan.FromMilliseconds(499));
        Assert.Empty(_events);

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal([(ScreenInfo?)null], _events);
    }

    [Fact]
    public void A_burst_of_messages_causes_one_look()
    {
        var looks = 0;
        using var watcher = Watcher(enumerate: () =>
        {
            looks++;
            return _screens.ToList();
        });
        watcher.Start();
        var afterStart = looks;

        for (var i = 0; i < 5; i++)
        {
            watcher.NotifyDisplayChanged();
            _clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        _clock.Advance(TimeSpan.FromMilliseconds(500));

        Assert.Equal(afterStart + 1, looks);
    }

    [Fact]
    public void A_resolution_change_makes_the_led_vanish_as_it_does_for_the_real_screen()
    {
        using var watcher = Watcher();
        watcher.Start();

        _screens[1] = Led with { Width = 1920, Height = 1080 };
        watcher.CheckNow();

        Assert.Equal([(ScreenInfo?)null], _events);
    }

    [Fact]
    public void Test_mode_uses_the_primary_monitor_and_never_remembers_it_as_the_led()
    {
        using var watcher = Watcher(testMode: true);

        watcher.Start();

        Assert.Equal(Primary, watcher.Led);
        Assert.True(watcher.TestMode);
        Assert.Empty(_remembered);
    }

    [Fact]
    public void An_error_while_looking_is_reported_and_the_next_check_tries_again()
    {
        var fail = true;
        using var watcher = Watcher(enumerate: () => fail ? throw new InvalidOperationException("driver") : _screens.ToList());
        // Start does the first look itself, so let it fail quietly by starting with a good list.
        fail = false;
        watcher.Start();
        fail = true;

        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Single(_errors);
        Assert.Empty(_events);

        fail = false;
        _screens.Remove(Led);
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal([(ScreenInfo?)null], _events);
    }

    [Fact]
    public void A_throwing_subscriber_does_not_break_the_watcher()
    {
        using var watcher = Watcher();
        watcher.LedChanged += _ => throw new InvalidOperationException("bad handler");
        watcher.Start();

        _screens.Remove(Led);
        watcher.CheckNow();

        Assert.Single(_errors);
        Assert.Null(watcher.Led);
    }

    [Fact]
    public void After_dispose_nothing_is_checked()
    {
        var watcher = Watcher();
        watcher.Start();
        watcher.Dispose();

        _screens.Remove(Led);
        _clock.Advance(TimeSpan.FromSeconds(30));
        watcher.CheckNow();

        Assert.Empty(_events);
    }

    [Fact]
    public void Real_monitor_enumeration_reports_exactly_one_primary_with_a_size()
    {
        var screens = MonitorService.Enumerate();

        Assert.NotEmpty(screens);
        Assert.Single(screens, s => s.IsPrimary);
        Assert.All(screens, s => Assert.True(s.Width > 0 && s.Height > 0));
    }
}
