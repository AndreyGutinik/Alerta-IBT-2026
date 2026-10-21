using AlertaIBT.Core.Input;

namespace AlertaIBT.Core.Tests.Input;

// Which chords may be the global shortcut: never the ones people press all day (copy, paste, Alt+F4...).
public sealed class HotkeyPolicyTests
{
    private const HotkeyModifiers Ctrl = HotkeyModifiers.Control;
    private const HotkeyModifiers Alt = HotkeyModifiers.Alt;
    private const HotkeyModifiers Shift = HotkeyModifiers.Shift;
    private const HotkeyModifiers Win = HotkeyModifiers.Win;

    // ---- rejected ----

    [Theory]
    [InlineData(0x56, "V")]
    [InlineData(0x43, "C")]
    [InlineData(0x58, "X")]
    [InlineData(0x5A, "Z")]
    [InlineData(0x41, "A")]
    [InlineData(0x59, "Y")]
    [InlineData(0x53, "S")]
    public void Ctrl_with_an_editing_key_is_reserved(int key, string name)
    {
        Assert.Equal(HotkeyProblem.Reserved, HotkeyPolicy.Check(Ctrl, key));
        Assert.False(HotkeyGesture.TryCreate(Ctrl, key, out _));
        Assert.False(HotkeyGesture.TryParse($"Ctrl+{name}", out _));
        Assert.False(HotkeyGesture.TryParse($"control + {name.ToLowerInvariant()}", out _));
    }

    [Fact]
    public void Alt_F4_Alt_Tab_And_Win_L_are_reserved()
    {
        Assert.Equal(HotkeyProblem.Reserved, HotkeyPolicy.Check(Alt, 0x73)); // F4
        Assert.Equal(HotkeyProblem.Reserved, HotkeyPolicy.Check(Alt | Shift, 0x73));
        Assert.Equal(HotkeyProblem.Reserved, HotkeyPolicy.Check(Alt, 0x09)); // Tab
        Assert.Equal(HotkeyProblem.Reserved, HotkeyPolicy.Check(Alt | Shift, 0x09));
        Assert.Equal(HotkeyProblem.Reserved, HotkeyPolicy.Check(Win, 0x4C)); // L
        Assert.False(HotkeyGesture.TryParse("Alt+F4", out _));
        Assert.False(HotkeyGesture.TryParse("Win+L", out _));
    }

    [Fact]
    public void Alt_Space_and_Ctrl_F4_are_reserved()
    {
        Assert.Equal(HotkeyProblem.Reserved, HotkeyPolicy.Check(Alt, 0x20)); // Space: the window menu
        Assert.Equal(HotkeyProblem.Reserved, HotkeyPolicy.Check(Alt | Shift, 0x20));
        Assert.Equal(HotkeyProblem.Reserved, HotkeyPolicy.Check(Ctrl, 0x73)); // F4: closes the document
        Assert.False(HotkeyGesture.TryParse("Alt+Space", out _));
        Assert.False(HotkeyGesture.TryParse("Ctrl+F4", out _));
    }

    [Fact]
    public void Neighbours_of_Alt_Space_and_Ctrl_F4_stay_free()
    {
        Assert.Equal(HotkeyProblem.None, HotkeyPolicy.Check(Ctrl | Alt, 0x20)); // Ctrl+Alt+Space, the suggested one
        Assert.Equal(HotkeyProblem.None, HotkeyPolicy.Check(Ctrl | Shift, 0x73)); // Ctrl+Shift+F4 is another command
        Assert.Equal(HotkeyProblem.None, HotkeyPolicy.Check(Ctrl | Alt, 0x73));
        Assert.Equal(HotkeyProblem.None, HotkeyPolicy.Check(Win, 0x20));
    }

    [Fact]
    public void Ctrl_Alt_Delete_is_reserved()
    {
        Assert.Equal(HotkeyProblem.Reserved, HotkeyPolicy.Check(Ctrl | Alt, 0x2E));
        Assert.Equal(HotkeyProblem.Reserved, HotkeyPolicy.Check(Ctrl | Alt | Shift, 0x2E));
        Assert.False(HotkeyGesture.TryParse("Ctrl+Alt+Delete", out _));
    }

    [Fact]
    public void Shift_Insert_and_Shift_Delete_alone_have_no_modifier_that_counts()
    {
        Assert.Equal(HotkeyProblem.NoModifier, HotkeyPolicy.Check(Shift, 0x2D));
        Assert.Equal(HotkeyProblem.NoModifier, HotkeyPolicy.Check(Shift, 0x2E));
        Assert.False(HotkeyGesture.TryParse("Shift+Insert", out _));
        Assert.False(HotkeyGesture.TryParse("Shift+Delete", out _));
    }

    [Theory]
    [InlineData(0x7B)]
    [InlineData(0x20)]
    [InlineData(0x41)]
    public void No_modifier_at_all_is_rejected(int key)
    {
        Assert.Equal(HotkeyProblem.NoModifier, HotkeyPolicy.Check(HotkeyModifiers.None, key));
        Assert.False(HotkeyGesture.TryCreate(HotkeyModifiers.None, key, out _));
    }

    [Theory]
    [InlineData(0x10)] // Shift
    [InlineData(0x11)] // Ctrl
    [InlineData(0x12)] // Alt
    [InlineData(0x5B)] // left Win
    [InlineData(0xA0)] // left Shift
    [InlineData(0xA2)] // left Ctrl
    [InlineData(0xA4)] // left Alt
    public void A_modifier_key_as_the_main_key_is_not_a_shortcut(int modifierKey)
    {
        Assert.False(HotkeyGesture.TryCreate(Ctrl | Alt, modifierKey, out _));
        Assert.NotEqual(HotkeyProblem.None, HotkeyGesture.Validate(Ctrl | Alt, modifierKey));
    }

    [Fact]
    public void Any_combination_of_modifiers_alone_is_not_a_shortcut()
    {
        foreach (var text in new[] { "Ctrl", "Alt", "Shift", "Win", "Ctrl+Alt", "Ctrl+Shift", "Ctrl+Alt+Shift+Win", "Win+Alt" })
        {
            Assert.False(HotkeyGesture.TryParse(text, out _), text);
        }
    }

    // ---- accepted ----

    [Theory]
    [InlineData("Ctrl+Alt+F12")]
    [InlineData("Ctrl+Shift+A")]
    [InlineData("Ctrl+Shift+V")]
    [InlineData("Ctrl+Alt+V")]
    [InlineData("Ctrl+Alt+Z")]
    [InlineData("Ctrl+F9")]
    [InlineData("Alt+F5")]
    [InlineData("Win+Shift+5")]
    [InlineData("Win+Alt+L")]
    [InlineData("Ctrl+Alt+Home")]
    [InlineData("Ctrl+Insert")]
    public void Ordinary_chords_are_accepted(string text)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture), text);
        Assert.Equal(HotkeyProblem.None, HotkeyGesture.Validate(gesture.Modifiers, gesture.VirtualKey));
    }

    [Fact]
    public void Validate_orders_the_problems_the_way_the_hint_needs()
    {
        Assert.Equal(HotkeyProblem.NoModifier, HotkeyGesture.Validate(HotkeyModifiers.None, 0x41));
        Assert.Equal(HotkeyProblem.Reserved, HotkeyGesture.Validate(Alt, 0x09)); // Tab is reserved, not "unknown"
        Assert.Equal(HotkeyProblem.UnknownKey, HotkeyGesture.Validate(Ctrl | Alt, 0x07));
        Assert.Equal(HotkeyProblem.None, HotkeyGesture.Validate(Ctrl | Alt, 0x20));
    }

    [Fact]
    public void Every_problem_has_a_hint_in_portuguese()
    {
        Assert.Equal(string.Empty, HotkeyPolicy.Hint(HotkeyProblem.None));
        foreach (var problem in new[] { HotkeyProblem.NoModifier, HotkeyProblem.UnknownKey, HotkeyProblem.Reserved })
        {
            var hint = HotkeyPolicy.Hint(problem);
            Assert.False(string.IsNullOrWhiteSpace(hint));
            Assert.True(hint.EndsWith('.'), hint);
        }

        Assert.Contains("Windows", HotkeyPolicy.Hint(HotkeyProblem.Reserved), StringComparison.Ordinal);
    }
}
