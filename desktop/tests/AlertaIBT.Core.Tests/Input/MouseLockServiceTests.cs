using AlertaIBT.Core.Input;
using AlertaIBT.Core.Screens;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Tests.Input;

// The mouse lock against a fake cursor: nothing here can grab or move the real mouse.
public sealed class MouseLockServiceTests
{
    private static readonly ScreenRect Primary = new(0, 0, 1920, 1080);

    private sealed class ListLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add($"{logLevel}: {formatter(state, exception)}");
    }

    private readonly FakeCursor _cursor = new();
    private readonly ListLogger _log = new();
    private readonly MouseLockService _service;

    public MouseLockServiceTests() => _service = new MouseLockService(_cursor, _log);

    // ---- lock ----

    [Fact]
    public void Locking_confines_the_cursor_to_the_primary_monitor_and_says_so_once()
    {
        var changes = new List<bool>();
        _service.LockedChanged += changes.Add;

        Assert.True(_service.Lock());

        Assert.True(_service.IsLocked);
        Assert.Equal(Primary, _cursor.Clip);
        Assert.Equal(Primary, _service.Applied);
        Assert.Equal(1, _cursor.ClipCalls);
        Assert.Equal([true], changes);

        Assert.True(_service.Lock()); // already on: nothing more happens
        Assert.Equal(1, _cursor.ClipCalls);
        Assert.Equal([true], changes);
    }

    [Fact]
    public void A_cursor_on_the_led_is_pulled_into_the_primary_monitor_when_the_lock_turns_on()
    {
        _cursor.X = -1200; // on the LED wall, left of the primary monitor
        _cursor.Y = 300;

        _service.Lock();

        Assert.Equal([(0, 300)], _cursor.Moves);
        Assert.True(Primary.Contains(_cursor.X, _cursor.Y));
    }

    [Fact]
    public void A_cursor_already_on_the_primary_monitor_is_not_moved()
    {
        _service.Lock();

        Assert.Empty(_cursor.Moves);
    }

    [Fact]
    public void The_lock_stays_off_when_windows_gives_no_primary_monitor_or_refuses()
    {
        _cursor.PrimaryAvailable = false;
        Assert.False(_service.Lock());
        Assert.False(_service.IsLocked);
        Assert.Equal(0, _cursor.ClipCalls);

        _cursor.PrimaryAvailable = true;
        _cursor.ClipAccepted = false;
        Assert.False(_service.Lock());
        Assert.False(_service.IsLocked);
        Assert.Contains(_log.Lines, line => line.StartsWith("Warning", StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_primary_rectangle_is_never_applied()
    {
        _cursor.Primary = new ScreenRect(0, 0, 0, 0);

        Assert.False(_service.Lock());
        Assert.Equal(0, _cursor.ClipCalls);
    }

    // ---- release ----

    [Fact]
    public void Unlocking_releases_the_cursor_once_and_does_nothing_when_it_is_off()
    {
        _service.Unlock();
        Assert.Equal(0, _cursor.UnclipCalls); // never touches a clip it did not set

        _service.Lock();
        var changes = new List<bool>();
        _service.LockedChanged += changes.Add;
        _service.Unlock();
        _service.Unlock();

        Assert.False(_service.IsLocked);
        Assert.Equal(1, _cursor.UnclipCalls);
        Assert.Equal(FakeCursor.Virtual, _cursor.Clip);
        Assert.Equal([false], changes);
    }

    [Fact]
    public void Dispose_is_the_exit_release()
    {
        _service.Lock();

        _service.Dispose();

        Assert.False(_service.IsLocked);
        Assert.Equal(1, _cursor.UnclipCalls);
    }

    [Fact]
    public void Toggle_flips_the_lock_and_returns_the_new_state()
    {
        Assert.True(_service.Toggle());
        Assert.False(_service.Toggle());
        Assert.True(_service.Toggle());
    }

    // ---- watchdog, display change, session change ----

    [Fact]
    public void Verify_never_applies_again_while_the_clip_is_the_wanted_one()
    {
        _service.Lock();
        var calls = _cursor.ClipCalls;

        for (var i = 0; i < 100; i++)
        {
            _service.Verify();
        }

        Assert.Equal(calls, _cursor.ClipCalls);
        Assert.Empty(_cursor.Moves);
    }

    [Fact]
    public void Verify_applies_again_when_windows_or_a_program_reset_the_clip()
    {
        _service.Lock();
        _cursor.Clip = FakeCursor.Virtual; // a lock screen, a game or remote desktop released it

        _service.Verify();

        Assert.Equal(Primary, _cursor.Clip);
        Assert.Equal(2, _cursor.ClipCalls);
        Assert.True(_service.IsLocked);
    }

    [Fact]
    public void A_clip_set_by_another_program_is_replaced_and_the_cursor_is_pulled_back()
    {
        _service.Lock();
        _cursor.Clip = new ScreenRect(-2560, 0, 0, 768); // someone confined it to the LED
        _cursor.X = -100;
        _cursor.Y = 100;

        _service.Verify();

        Assert.Equal(Primary, _cursor.Clip);
        Assert.Equal([(0, 100)], _cursor.Moves);
    }

    [Fact]
    public void A_display_change_that_moves_the_primary_monitor_recomputes_the_rectangle()
    {
        _service.Lock();

        // The owner made another video the primary: the virtual desktop is renumbered around it.
        _cursor.Primary = new ScreenRect(0, 0, 2560, 1440);
        _service.Verify(); // what the display-change event calls

        Assert.Equal(new ScreenRect(0, 0, 2560, 1440), _cursor.Clip);
        Assert.Equal(new ScreenRect(0, 0, 2560, 1440), _service.Applied);
    }

    [Fact]
    public void A_primary_monitor_with_negative_coordinates_is_applied_as_it_is()
    {
        _cursor.Primary = new ScreenRect(-1920, -200, 0, 880);
        _cursor.X = 10;
        _cursor.Y = 10; // outside that primary

        _service.Lock();

        Assert.Equal(new ScreenRect(-1920, -200, 0, 880), _cursor.Clip);
        Assert.Equal([(-1, 10)], _cursor.Moves);
    }

    [Fact]
    public void Verify_does_nothing_while_the_lock_is_off_and_does_not_even_ask_windows()
    {
        _service.Verify();

        Assert.Equal(0, _cursor.ClipCalls);
        Assert.Equal(0, _cursor.ReadClipCalls);
    }

    [Fact]
    public void Verify_leaves_everything_alone_when_the_clip_cannot_be_read_as_on_the_secure_desktop()
    {
        _service.Lock();
        var calls = _cursor.ClipCalls;
        _cursor.ClipReadable = false;
        _cursor.Clip = FakeCursor.Virtual;

        _service.Verify();

        Assert.Equal(calls, _cursor.ClipCalls);
        Assert.True(_service.IsLocked); // still wanted: it comes back by itself when the desktop is back
    }

    [Fact]
    public void Verify_keeps_trying_when_windows_refuses_and_logs_that_only_once()
    {
        _service.Lock();
        _cursor.Clip = FakeCursor.Virtual;
        _cursor.ClipAccepted = false;
        var before = _log.Lines.Count;

        for (var i = 0; i < 20; i++)
        {
            _service.Verify();
        }

        Assert.Equal(21, _cursor.ClipCalls); // the lock, then one refused try per call
        Assert.Single(_log.Lines.Skip(before));
        Assert.True(_service.IsLocked);

        _cursor.ClipAccepted = true; // the secure desktop is gone
        _service.Verify();
        Assert.Equal(Primary, _cursor.Clip);
    }

    [Fact]
    public void A_program_that_keeps_resetting_the_clip_does_not_flood_the_log()
    {
        _service.Lock();
        var before = _log.Lines.Count;

        for (var i = 0; i < 50; i++)
        {
            _cursor.Clip = FakeCursor.Virtual;
            _service.Verify();
        }

        Assert.Single(_log.Lines.Skip(before));
    }

    [Fact]
    public void Verify_in_steady_state_allocates_nothing()
    {
        _service.Lock();
        for (var i = 0; i < 50; i++)
        {
            _service.Verify(); // warm up
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            _service.Verify();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ---- recenter (Ctrl x3) ----

    [Fact]
    public void Recenter_sends_the_cursor_to_the_middle_of_the_primary_monitor_with_the_lock_off()
    {
        _cursor.X = -1500; // on the LED wall
        _cursor.Y = 200;

        Assert.True(_service.Recenter());

        Assert.Equal([(960, 540)], _cursor.Moves);
        Assert.Equal(0, _cursor.ClipCalls); // the lock stays off
        Assert.False(_service.IsLocked);
    }

    [Fact]
    public void Recenter_with_the_lock_on_moves_inside_the_clip()
    {
        _service.Lock();

        Assert.True(_service.Recenter());

        Assert.Equal((960, 540), _cursor.Moves[^1]);
        Assert.True(_cursor.Clip.Contains(960, 540));
        Assert.Equal(1, _cursor.ClipCalls);
    }

    [Fact]
    public void Recenter_first_fixes_a_stale_clip_after_the_primary_monitor_changed()
    {
        _service.Lock();
        _cursor.Primary = new ScreenRect(0, 0, 2560, 1440);

        Assert.True(_service.Recenter());

        Assert.Equal(new ScreenRect(0, 0, 2560, 1440), _cursor.Clip);
        Assert.Equal((1280, 720), _cursor.Moves[^1]);
    }

    [Fact]
    public void Recenter_uses_a_primary_monitor_at_negative_coordinates()
    {
        _cursor.Primary = new ScreenRect(-2560, 0, 0, 768);

        Assert.True(_service.Recenter());

        Assert.Equal([(-1280, 384)], _cursor.Moves);
    }

    [Fact]
    public void Recenter_fails_without_moving_anything_when_windows_gives_no_primary_monitor()
    {
        _cursor.PrimaryAvailable = false;

        Assert.False(_service.Recenter());
        Assert.Empty(_cursor.Moves);
    }
}
