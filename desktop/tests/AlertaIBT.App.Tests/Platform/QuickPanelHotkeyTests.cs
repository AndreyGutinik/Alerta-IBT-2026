using AlertaIBT.App.Platform;
using AlertaIBT.Core.Input;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.App.Tests.Platform;

public sealed class QuickPanelHotkeyTests
{
    private sealed class FakeRegistrar : IHotkeyRegistrar
    {
        public bool Accept { get; set; } = true;

        public bool Throw { get; set; }

        public List<string> Registered { get; } = [];

        public int Unregistered { get; private set; }

        public bool Register(HotkeyGesture gesture)
        {
            if (Throw)
            {
                throw new InvalidOperationException("boom");
            }

            Registered.Add(gesture.ToString());
            return Accept;
        }

        public void Unregister() => Unregistered++;
    }

    [Fact]
    public void A_free_combination_is_registered_and_reported_with_no_message()
    {
        var registrar = new FakeRegistrar();
        var hotkey = new QuickPanelHotkey(registrar);
        var seen = new List<HotkeyStatus>();
        hotkey.StatusChanged += seen.Add;

        hotkey.Apply("Ctrl+Alt+Space");

        Assert.Equal(["Ctrl+Alt+Space"], registrar.Registered);
        Assert.True(hotkey.Status.Registered);
        Assert.Null(hotkey.Status.Message);
        Assert.Null(hotkey.Status.Short);
        Assert.Equal("Ctrl+Alt+Espa\U000000E7o", hotkey.Status.Display);
        Assert.Equal("Ctrl+Alt+Space", hotkey.Status.Text);
        Assert.Single(seen);
    }

    [Fact]
    public void A_combination_taken_by_another_program_gives_a_clear_message_and_does_not_throw()
    {
        var hotkey = new QuickPanelHotkey(new FakeRegistrar { Accept = false });

        hotkey.Apply("Ctrl+Alt+Space");

        Assert.False(hotkey.Status.Registered);
        Assert.Equal(
            "O atalho Ctrl+Alt+Espa\U000000E7o j\U000000E1 \U000000E9 usado por outro programa. Use o \U000000EDcone da bandeja ou escolha outro atalho em Configura\U000000E7\U000000F5es.",
            hotkey.Status.Message);
        Assert.Equal("Atalho Ctrl+Alt+Espa\U000000E7o em uso por outro programa", hotkey.Status.Short);
        Assert.True(hotkey.Status.Short!.Length < 100); // the tray tooltip is short
    }

    [Fact]
    public void A_system_call_that_throws_is_treated_like_a_combination_in_use()
    {
        var hotkey = new QuickPanelHotkey(new FakeRegistrar { Throw = true });

        hotkey.Apply("Ctrl+Alt+F12");

        Assert.False(hotkey.Status.Registered);
        Assert.NotNull(hotkey.Status.Message);
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add((logLevel, formatter(state, exception)));
    }

    // A value saved in a bad way (an edited file, an older version) never registers: the default takes its place, with one log line.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("banana")]
    [InlineData("Shift+A")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+V")]
    [InlineData("Ctrl+C")]
    [InlineData("Alt+F4")]
    [InlineData("Alt+Tab")]
    [InlineData("Win+L")]
    [InlineData("Ctrl+Alt+Delete")]
    [InlineData("Shift+Insert")]
    [InlineData("F12")]
    public void A_saved_invalid_value_falls_back_to_the_default_and_is_logged_instead_of_registering(string text)
    {
        var registrar = new FakeRegistrar();
        var log = new ListLogger();
        var hotkey = new QuickPanelHotkey(registrar, log);
        var seen = new List<HotkeyStatus>();
        hotkey.StatusChanged += seen.Add;

        hotkey.Apply(text);

        Assert.Equal(["Ctrl+Alt+Space"], registrar.Registered);
        Assert.True(hotkey.Status.Registered);
        Assert.Equal("Ctrl+Alt+Space", hotkey.Status.Text);
        Assert.Equal(text, hotkey.Status.FellBackFrom);
        var line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("Ctrl+Alt+Space", line.Text, StringComparison.Ordinal);
        Assert.Contains(text.Trim(), line.Text, StringComparison.Ordinal);
        Assert.Single(seen);
    }

    [Fact]
    public void After_the_fallback_the_default_text_is_not_registered_again()
    {
        var registrar = new FakeRegistrar();
        var hotkey = new QuickPanelHotkey(registrar);

        hotkey.Apply("Ctrl+V");
        hotkey.Apply("Ctrl+Alt+Space"); // what the shell saves back into the settings

        Assert.Equal(["Ctrl+Alt+Space"], registrar.Registered);
    }

    [Fact]
    public void A_valid_value_is_not_a_fallback()
    {
        var log = new ListLogger();
        var hotkey = new QuickPanelHotkey(new FakeRegistrar(), log);

        hotkey.Apply("Ctrl+Alt+F12");

        Assert.Null(hotkey.Status.FellBackFrom);
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void When_the_default_is_taken_by_another_program_the_message_says_so()
    {
        var hotkey = new QuickPanelHotkey(new FakeRegistrar { Accept = false });

        hotkey.Apply("Ctrl+V");

        Assert.False(hotkey.Status.Registered);
        Assert.Equal("Ctrl+Alt+Space", hotkey.Status.Text);
        Assert.Contains("usado por outro programa", hotkey.Status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_text_is_not_registered_twice_and_a_new_text_replaces_the_old()
    {
        var registrar = new FakeRegistrar();
        var hotkey = new QuickPanelHotkey(registrar);
        var changes = 0;
        hotkey.StatusChanged += _ => changes++;

        hotkey.Apply("Ctrl+Alt+Space");
        hotkey.Apply("Ctrl+Alt+Space");
        hotkey.Apply("Ctrl+Alt+F12");

        Assert.Equal(["Ctrl+Alt+Space", "Ctrl+Alt+F12"], registrar.Registered);
        Assert.Equal(2, changes);
        Assert.Equal("Ctrl+Alt+F12", hotkey.Status.Display);
    }

    [Fact]
    public void Retry_registers_again_after_the_other_program_let_go()
    {
        var registrar = new FakeRegistrar { Accept = false };
        var hotkey = new QuickPanelHotkey(registrar);
        var changes = new List<HotkeyStatus>();
        hotkey.StatusChanged += changes.Add;
        hotkey.Apply("Ctrl+Alt+Space");

        hotkey.Retry(); // still taken: nothing to announce again
        Assert.Single(changes);
        Assert.Equal(2, registrar.Registered.Count);

        registrar.Accept = true;
        hotkey.Retry();

        Assert.True(hotkey.Status.Registered);
        Assert.Equal(2, changes.Count);

        hotkey.Retry(); // working: nothing to do
        Assert.Equal(3, registrar.Registered.Count);
    }

    [Fact]
    public void Retry_before_anything_was_applied_does_nothing()
    {
        var registrar = new FakeRegistrar();
        var hotkey = new QuickPanelHotkey(registrar);

        hotkey.Retry();

        Assert.Empty(registrar.Registered);
        Assert.Equal(HotkeyStatus.None, hotkey.Status);
    }

    [Fact]
    public void Describe_writes_the_key_in_portuguese()
    {
        Assert.True(HotkeyGesture.TryParse("ctrl+alt+space", out var space));
        Assert.True(HotkeyGesture.TryParse("Ctrl+Shift+F9", out var f9));

        Assert.Equal("Ctrl+Alt+Espa\U000000E7o", QuickPanelHotkey.Describe(space));
        Assert.Equal("Ctrl+Shift+F9", QuickPanelHotkey.Describe(f9));
    }
}
