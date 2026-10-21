using AlertaIBT.Core.Input;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.Core.Tests.Input;

public sealed class HotkeyGestureTests
{
    [Fact]
    public void The_default_quick_panel_hotkey_in_the_settings_parses()
    {
        var text = new SystemSettings().Hotkey;

        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, gesture.Modifiers);
        Assert.Equal(0x20, gesture.VirtualKey);
        Assert.Equal("Ctrl+Alt+Space", gesture.ToString());
    }

    [Theory]
    [InlineData("ctrl + shift + p", "Ctrl+Shift+P")]
    [InlineData("Control+Alt+Pause", "Ctrl+Alt+Pause")]
    [InlineData("Win+Shift+5", "Shift+Win+5")]
    [InlineData("Alt+Num7", "Alt+Num7")]
    [InlineData("CTRL+ALT+SPACE", "Ctrl+Alt+Space")]
    public void Names_are_normalized(string text, string expected)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(expected, gesture.ToString());
    }

    [Theory]
    [InlineData("F12")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+Banana")]
    [InlineData("Ctrl+A+B")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Invalid_text_is_rejected(string? text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _));
    }

    [Fact]
    public void A_modifier_and_a_known_key_are_required()
    {
        Assert.True(HotkeyGesture.TryCreate(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x7B, out var gesture));
        Assert.Equal("Ctrl+Alt+F12", gesture.ToString());
        Assert.False(HotkeyGesture.TryCreate(HotkeyModifiers.None, 0x7B, out _));
        Assert.False(HotkeyGesture.TryCreate(HotkeyModifiers.Control, 0x07, out _));
    }

    [Fact]
    public void Shift_alone_is_rejected_because_it_would_swallow_typing()
    {
        Assert.False(HotkeyGesture.TryParse("Shift+A", out _));
        Assert.True(HotkeyGesture.TryParse("Ctrl+Shift+A", out _));
    }

    [Fact]
    public void An_unnamed_key_shows_its_hex_code()
    {
        var gesture = new HotkeyGesture(HotkeyModifiers.Control, 0x07);

        Assert.Equal("Ctrl+0x07", gesture.ToString());
    }
}
