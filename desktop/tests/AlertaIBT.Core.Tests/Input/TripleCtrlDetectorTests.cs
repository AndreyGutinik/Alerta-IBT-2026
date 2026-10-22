using AlertaIBT.Core.Input;

namespace AlertaIBT.Core.Tests.Input;

// Ctrl pressed three times quickly: explicit timestamps, no clock and no keyboard.
public sealed class TripleCtrlDetectorTests
{
    private const int L = TripleCtrlDetector.VkLeftControl;
    private const int R = TripleCtrlDetector.VkRightControl;
    private const int KeyC = 0x43;
    private const int KeyA = 0x41;
    private const int VkMenu = 0x12;

    private readonly TripleCtrlDetector _detector = new();

    // One tap: the key goes down and comes up. Returns what the key-down said.
    private bool Tap(int key, uint at, bool alt = false)
    {
        var result = _detector.Feed(key, true, alt, at);
        _detector.Feed(key, false, alt, at + 40);
        return result;
    }

    [Fact]
    public void Three_quick_taps_fire_on_the_third_and_only_on_the_third()
    {
        Assert.False(Tap(L, 1000));
        Assert.False(Tap(L, 1200));
        Assert.True(Tap(L, 1400));
    }

    [Fact]
    public void The_third_press_exactly_900_ms_after_the_first_still_counts_and_901_does_not()
    {
        Assert.False(Tap(L, 0));
        Assert.False(Tap(L, 450));
        Assert.True(Tap(L, 900));

        var slow = new TripleCtrlDetector();
        Assert.False(slow.Feed(L, true, false, 0));
        slow.Feed(L, false, false, 10);
        Assert.False(slow.Feed(L, true, false, 450));
        slow.Feed(L, false, false, 460);
        Assert.False(slow.Feed(L, true, false, 901));
        Assert.Equal(1, slow.Count); // that press is the first of a new sequence
    }

    [Fact]
    public void Three_slow_taps_never_fire_however_long_the_user_keeps_going()
    {
        for (uint t = 0; t < 10_000; t += 500)
        {
            Assert.False(Tap(L, t));
        }
    }

    [Fact]
    public void A_slow_start_does_not_spoil_a_quick_triple_that_follows()
    {
        Assert.False(Tap(L, 0));
        Assert.False(Tap(L, 2000)); // too late for the first one: this starts over
        Assert.False(Tap(L, 2200));
        Assert.True(Tap(L, 2400));
    }

    [Fact]
    public void Another_key_between_two_presses_breaks_the_sequence()
    {
        Assert.False(Tap(L, 0));
        Assert.False(Tap(L, 100));
        Assert.False(Tap(KeyA, 150));
        Assert.False(Tap(L, 200)); // first of a new count
        Assert.False(Tap(L, 300));
        Assert.True(Tap(L, 400));
    }

    [Fact]
    public void Ctrl_C_Ctrl_C_Ctrl_C_never_counts()
    {
        uint t = 0;
        for (var i = 0; i < 6; i++)
        {
            Assert.False(_detector.Feed(L, true, false, t));
            Assert.False(_detector.Feed(KeyC, true, false, t + 20));
            _detector.Feed(KeyC, false, false, t + 60);
            _detector.Feed(L, false, false, t + 80);
            t += 120;
        }
    }

    [Fact]
    public void Ctrl_with_Alt_held_never_counts_and_pressing_Alt_between_breaks_it()
    {
        Assert.False(Tap(L, 0, alt: true));
        Assert.False(Tap(L, 100, alt: true));
        Assert.False(Tap(L, 200, alt: true));

        Assert.False(Tap(L, 1000));
        Assert.False(Tap(L, 1100));
        _detector.Feed(VkMenu, true, true, 1150); // Alt goes down
        Assert.False(Tap(L, 1200));
    }

    [Fact]
    public void Holding_Ctrl_is_one_press_however_long_the_auto_repeat_runs()
    {
        for (uint t = 0; t < 3000; t += 30)
        {
            Assert.False(_detector.Feed(L, true, false, t)); // key-down repeats while it is held
        }

        Assert.Equal(1, _detector.Count);
    }

    [Fact]
    public void A_hold_followed_by_two_quick_taps_fires_when_all_fit_in_the_window()
    {
        Assert.False(_detector.Feed(L, true, false, 0));
        Assert.False(_detector.Feed(L, true, false, 30)); // repeat
        Assert.False(_detector.Feed(L, true, false, 60)); // repeat
        _detector.Feed(L, false, false, 100);
        Assert.False(Tap(L, 300));
        Assert.True(Tap(L, 600));
    }

    [Fact]
    public void A_long_hold_followed_by_two_taps_is_too_slow_to_fire()
    {
        for (uint t = 0; t < 3000; t += 30)
        {
            _detector.Feed(L, true, false, t);
        }

        _detector.Feed(L, false, false, 3010);
        Assert.False(Tap(L, 3100));
        Assert.False(Tap(L, 3200));
    }

    [Fact]
    public void Left_and_right_Ctrl_mix_in_any_order()
    {
        Assert.False(Tap(L, 0));
        Assert.False(Tap(R, 100));
        Assert.True(Tap(L, 200));

        Assert.False(Tap(R, 1000));
        Assert.False(Tap(R, 1100));
        Assert.True(Tap(L, 1200));
    }

    [Fact]
    public void The_generic_Ctrl_code_counts_too()
    {
        Assert.False(Tap(TripleCtrlDetector.VkControl, 0));
        Assert.False(Tap(TripleCtrlDetector.VkControl, 100));
        Assert.True(Tap(TripleCtrlDetector.VkControl, 200));
    }

    [Fact]
    public void Four_quick_presses_fire_once_and_the_fourth_starts_a_new_count()
    {
        Assert.False(Tap(L, 0));
        Assert.False(Tap(L, 100));
        Assert.True(Tap(L, 200));
        Assert.False(Tap(L, 300));
        Assert.Equal(1, _detector.Count);
    }

    [Fact]
    public void Six_quick_presses_fire_twice()
    {
        var fired = 0;
        for (uint i = 0; i < 6; i++)
        {
            if (Tap(L, i * 100))
            {
                fired++;
            }
        }

        Assert.Equal(2, fired);
    }

    [Fact]
    public void Reset_forgets_the_presses_and_the_held_keys()
    {
        Assert.False(Tap(L, 0));
        Assert.False(_detector.Feed(L, true, false, 100)); // the second press, still held down
        Assert.Equal(2, _detector.Count);

        _detector.Reset();

        Assert.Equal(0, _detector.Count);
        Assert.False(Tap(L, 200));
        Assert.False(Tap(L, 300));
        Assert.True(Tap(L, 400));
    }

    [Fact]
    public void Letting_go_of_another_key_does_not_break_the_sequence()
    {
        Assert.False(Tap(L, 0));
        Assert.False(Tap(L, 100));
        _detector.Feed(KeyA, false, false, 150); // a key pressed before, released now
        Assert.True(Tap(L, 200));
    }

    [Fact]
    public void A_Ctrl_whose_release_was_never_seen_does_not_leave_the_detector_deaf()
    {
        _detector.Feed(L, true, false, 0); // the release happened on the secure desktop and was never seen
        _detector.Feed(KeyA, true, false, 5000); // any other key clears the held flags

        Assert.False(Tap(L, 6000));
        Assert.False(Tap(L, 6100));
        Assert.True(Tap(L, 6200));
    }

    [Fact]
    public void The_tick_counter_wrapping_around_does_not_matter()
    {
        const uint Near = uint.MaxValue - 100;
        Assert.False(Tap(L, Near));
        Assert.False(Tap(L, unchecked(Near + 150))); // wraps past zero
        Assert.True(Tap(L, unchecked(Near + 300)));
    }

    [Fact]
    public void Other_keys_alone_never_fire_and_never_throw()
    {
        for (uint t = 0; t < 1000; t += 10)
        {
            Assert.False(_detector.Feed(KeyA, true, false, t));
            Assert.False(_detector.Feed(KeyA, false, false, t + 5));
        }

        Assert.Equal(0, _detector.Count);
    }
}
