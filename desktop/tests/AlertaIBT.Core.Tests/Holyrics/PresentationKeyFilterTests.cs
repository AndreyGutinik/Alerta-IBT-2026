using AlertaIBT.Core.Holyrics;

namespace AlertaIBT.Core.Tests.Holyrics;

// The decision of the presentation mode: pure, no hook, no keyboard.
public sealed class PresentationKeyFilterTests
{
    private const int PageDown = 0x22;
    private const int PageUp = 0x21;
    private const int Right = 0x27;
    private const int Left = 0x25;
    private const int LetterB = 0x42;

    private static PresentationKeyFilter On() => new() { On = true, Configured = true };

    private static PresentationKeyEvent Down(int key, bool modifier = false, bool panel = false) => new(key, true, modifier, panel);

    private static PresentationKeyEvent Up(int key, bool modifier = false, bool panel = false) => new(key, false, modifier, panel);

    [Theory]
    [InlineData(PageDown, PresentationKeyAction.Next)]
    [InlineData(Right, PresentationKeyAction.Next)]
    [InlineData(PageUp, PresentationKeyAction.Previous)]
    [InlineData(Left, PresentationKeyAction.Previous)]
    public void The_default_sets_are_consumed_and_give_their_command(int key, PresentationKeyAction expected)
    {
        var verdict = On().Decide(Down(key));

        Assert.True(verdict.Consume);
        Assert.Equal(expected, verdict.Action);
    }

    [Fact]
    public void Any_other_key_passes_through()
    {
        var filter = On();

        Assert.Equal(PresentationVerdict.Pass, filter.Decide(Down(LetterB)));
        Assert.Equal(PresentationVerdict.Pass, filter.Decide(Down(0x26))); // up arrow
    }

    [Fact]
    public void Custom_sets_replace_the_defaults()
    {
        var filter = On();
        filter.SetKeys([LetterB], [0x43]);

        Assert.Equal(PresentationKeyAction.Next, filter.Decide(Down(LetterB)).Action);
        Assert.Equal(PresentationKeyAction.Previous, filter.Decide(Down(0x43)).Action);
        Assert.False(filter.Decide(Down(PageDown)).Consume);
    }

    [Fact]
    public void A_held_modifier_lets_the_key_through_and_sends_nothing()
    {
        var verdict = On().Decide(Down(Right, modifier: true));

        Assert.Equal(PresentationVerdict.Pass, verdict);
    }

    [Fact]
    public void The_mode_off_passes_everything()
    {
        var filter = new PresentationKeyFilter { On = false, Configured = true };

        Assert.Equal(PresentationVerdict.Pass, filter.Decide(Down(PageDown)));
        Assert.False(filter.Matters(PageDown, keyDown: true));
    }

    [Fact]
    public void Without_a_token_nothing_is_consumed()
    {
        var filter = new PresentationKeyFilter { On = true, Configured = false };

        Assert.Equal(PresentationVerdict.Pass, filter.Decide(Down(PageDown)));
    }

    [Fact]
    public void A_key_the_quick_panel_already_handles_passes_so_a_slide_is_never_sent_twice()
    {
        // Left/Right in the text box of the quick panel (arrows switch on): the panel's own path sends; the hook stays out.
        Assert.Equal(PresentationVerdict.Pass, On().Decide(Down(Right, panel: true)));
        Assert.Equal(PresentationVerdict.Pass, On().Decide(Down(Left, panel: true)));
    }

    [Fact]
    public void The_app_windows_are_like_any_other_window_for_every_key_the_panel_does_not_handle()
    {
        // The owner types an alert in the quick panel while the pastor presses Page Down: the event says the panel does not handle that key.
        var filter = On();
        Assert.Equal(PresentationKeyAction.Next, filter.Decide(Down(PageDown, panel: false)).Action);
        Assert.True(filter.Decide(Up(PageDown)).Consume);
        // Right in another box of the app (not the quick panel): consumed as usual.
        Assert.Equal(PresentationKeyAction.Next, filter.Decide(Down(Right, panel: false)).Action);
    }

    [Fact]
    public void A_swallowed_key_down_has_its_key_up_swallowed_so_no_key_is_stuck()
    {
        var filter = On();

        Assert.True(filter.Decide(Down(PageDown)).Consume);
        var up = filter.Decide(Up(PageDown));

        Assert.True(up.Consume);
        Assert.Equal(PresentationKeyAction.None, up.Action);
        Assert.Equal(PresentationVerdict.Pass, filter.Decide(Up(PageDown))); // a second up (never happens) is not ours
    }

    [Fact]
    public void The_key_up_of_a_passed_key_is_not_swallowed()
    {
        var filter = On();

        Assert.False(filter.Decide(Down(LetterB)).Consume);
        Assert.False(filter.Decide(Up(LetterB)).Consume);
        Assert.False(filter.Decide(Up(PageDown)).Consume); // pressed before the mode was turned on
    }

    [Fact]
    public void The_up_is_swallowed_even_if_the_mode_was_turned_off_or_a_modifier_pressed_in_between()
    {
        var filter = On();
        filter.Decide(Down(Right));
        filter.On = false;

        Assert.True(filter.Decide(Up(Right, modifier: true, panel: true)).Consume);
    }

    [Fact]
    public void Auto_repeat_is_swallowed_and_sends_one_command_only()
    {
        var filter = On();

        var first = filter.Decide(Down(PageDown));
        var repeats = Enumerable.Range(0, 5).Select(_ => filter.Decide(Down(PageDown))).ToList();

        Assert.Equal(PresentationKeyAction.Next, first.Action);
        Assert.All(repeats, r => Assert.Equal(new PresentationVerdict(true, PresentationKeyAction.None), r));

        filter.Decide(Up(PageDown));
        Assert.Equal(PresentationKeyAction.Next, filter.Decide(Down(PageDown)).Action); // the next press counts again
    }

    [Fact]
    public void Two_different_keys_held_are_tracked_apart()
    {
        var filter = On();
        filter.Decide(Down(PageDown));
        filter.Decide(Down(PageUp));

        Assert.True(filter.Decide(Up(PageDown)).Consume);
        Assert.True(filter.Decide(Up(PageUp)).Consume);
    }

    [Fact]
    public void Reset_forgets_held_keys()
    {
        var filter = On();
        filter.Decide(Down(PageDown));
        filter.Reset();

        Assert.False(filter.Decide(Up(PageDown)).Consume);
    }

    [Fact]
    public void An_emptied_set_consumes_nothing_for_that_direction()
    {
        var filter = On();
        filter.SetKeys([PageDown], []);

        Assert.True(filter.Decide(Down(PageDown)).Consume);
        Assert.False(filter.Decide(Down(PageUp)).Consume);
    }

    [Fact]
    public void Out_of_range_key_codes_pass()
    {
        Assert.Equal(PresentationVerdict.Pass, On().Decide(Down(999)));
        Assert.Equal(PresentationVerdict.Pass, On().Decide(Down(-1)));
    }

    [Theory]
    [InlineData(0x10)]
    [InlineData(0x11)]
    [InlineData(0x12)]
    [InlineData(0x5B)]
    [InlineData(0x1B)]
    [InlineData(0xAF)] // volume up
    [InlineData(0xB3)] // play/pause
    [InlineData(0x14)]
    public void Modifiers_escape_and_media_keys_cannot_be_chosen(int key) => Assert.False(PresentationKeys.IsAllowed(key));

    [Fact]
    public void Clean_keeps_allowed_keys_once_and_drops_what_the_other_set_holds()
    {
        var cleaned = PresentationKeys.Clean([PageDown, PageDown, 0x11, Right, LetterB], taken: [Right]);

        Assert.Equal([PageDown, LetterB], cleaned);
    }

    [Fact]
    public void Labels_and_names_read_well()
    {
        Assert.Equal("PgDn", PresentationKeys.Label(PageDown));
        Assert.Equal("Seta direita", PresentationKeys.Name(Right));
        Assert.Equal("B", PresentationKeys.Label(LetterB));
        Assert.Equal("F5", PresentationKeys.Label(0x74));
    }
}
