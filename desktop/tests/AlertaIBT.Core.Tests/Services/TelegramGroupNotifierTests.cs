using AlertaIBT.Core.Services;
using AlertaIBT.Core.Telegram;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Tests.Services;

public class TelegramGroupNotifierTests
{
    private readonly FakeTelegramApi _api = new();
    private readonly CapturingLogger _log = new();
    private long _chat = FakeTelegramApi.Chat;

    private TelegramGroupNotifier Notifier() => new(_api.Client(), () => _chat, _log, (_, _) => Task.CompletedTask);

    [Fact]
    public async Task Messages_go_to_the_group_in_the_order_they_were_given()
    {
        using var notifier = Notifier();

        notifier.Notify("one");
        notifier.Notify("two");
        notifier.Notify("three");

        await Wait.Until(() => _api.Sent.Length == 3, "three messages sent");
        Assert.Equal(["one", "two", "three"], _api.Sent.Select(s => s.Text));
        Assert.All(_api.Sent, s => Assert.Equal(FakeTelegramApi.Chat, s.Chat));
    }

    [Fact]
    public async Task Notify_never_blocks_even_when_telegram_hangs()
    {
        using var notifier = Notifier();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 500; i++)
        {
            notifier.Notify("message " + i);
        }

        Assert.True(watch.ElapsedMilliseconds < 2000);
        await Wait.Until(() => _api.Sent.Length > 0, "something was sent");
    }

    [Fact]
    public async Task A_failed_send_is_tried_once_more_and_then_the_next_message_still_goes()
    {
        _api.FailNextSends(1);
        using var notifier = Notifier();

        notifier.Notify("first");
        notifier.Notify("second");

        await Wait.Until(() => _api.Sent.Length == 2, "both sent");
        Assert.Equal(["first", "second"], _api.Sent.Select(s => s.Text));
        Assert.Equal(3, _api.SendAttempts);
    }

    [Fact]
    public async Task A_message_that_fails_twice_is_dropped_with_one_warning_and_later_ones_go_through()
    {
        _api.FailNextSends(4);
        using var notifier = Notifier();

        notifier.Notify("lost one");
        notifier.Notify("lost two");
        notifier.Notify("kept");

        await Wait.Until(() => _api.Sent.Length == 1, "the third message is sent");
        Assert.Equal("kept", _api.Sent[0].Text);
        Assert.Single(_log.Entries, e => e.Level == LogLevel.Warning); // the same error is logged once
    }

    [Fact]
    public async Task Nothing_is_sent_without_a_chat_or_a_token_and_nothing_throws()
    {
        _chat = 0;
        using var notifier = Notifier();
        notifier.Notify("nobody to tell");

        var noToken = new TelegramGroupNotifier(new TelegramClient(new HttpClient(_api), () => null), () => FakeTelegramApi.Chat, _log);
        noToken.Notify("no token");

        await Wait.Settle();
        Assert.Empty(_api.Sent);
        Assert.Equal(0, _api.SendAttempts);
        noToken.Dispose();
    }

    [Fact]
    public async Task The_chat_is_read_when_the_message_is_sent_so_a_change_in_the_settings_applies()
    {
        using var notifier = Notifier();
        _chat = -42;

        notifier.Notify("moved");

        await Wait.Until(() => _api.Sent.Length == 1);
        Assert.Equal(-42, _api.Sent[0].Chat);
    }

    [Fact]
    public void Blank_text_is_ignored_and_disposing_twice_is_fine()
    {
        var notifier = Notifier();

        notifier.Notify("   ");
        notifier.Notify(string.Empty);
        notifier.Dispose();
        notifier.Dispose();
        notifier.Notify("after dispose");

        Assert.Equal(0, _api.SendAttempts);
    }
}
