using System.Diagnostics;
using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Telegram;

namespace AlertaIBT.Core.Tests.Services;

// The member cap is checked on the cleaned text before any correction: a huge message never reaches Gemini or the dictionary.
public class AlertServiceEarlyCapTests
{
    private static ServiceHarness Quiet()
    {
        var h = new ServiceHarness(start: false);
        h.Display.SetLed(false);
        h.Service.Start();
        return h;
    }

    [Fact]
    public async Task A_4000_character_telegram_message_is_rejected_without_asking_any_corrector()
    {
        using var h = Quiet();
        var raw = string.Join(' ', Enumerable.Repeat("farrol", 570));
        Assert.True(raw.Length > 3900);

        var watch = Stopwatch.StartNew();
        var outcome = await h.Telegram(raw);
        watch.Stop();

        Assert.Equal(SubmitKind.TooLong, outcome.Kind);
        Assert.Equal(0, h.Gemini.Calls);
        Assert.Equal(0, h.Offline.Calls);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.Elapsed}");
        Assert.Empty(h.Queue.Pending);
        var entry = Assert.Single(h.Queue.History);
        Assert.Equal(HistoryState.Rejected, entry.State);
        Assert.Equal("texto grande demais", entry.Reason);
        Assert.Equal([TelegramReplies.TooLong(200)], h.Notifier.Sent);
    }

    [Fact]
    public async Task A_member_text_of_201_cleaned_letters_is_rejected_before_correction()
    {
        using var h = Quiet();
        var raw = "corolla prata abc1d23 " + new string('x', 180);

        var outcome = await h.Telegram(raw);

        Assert.Equal(SubmitKind.TooLong, outcome.Kind);
        Assert.Equal(0, h.Gemini.Calls);
        Assert.Equal(0, h.Offline.Calls);
    }

    [Fact]
    public async Task Typed_whitespace_and_emoji_do_not_count_toward_the_early_cap()
    {
        using var h = Quiet();
        var raw = "corolla prata abc1d23 " + new string('x', 140) + new string(' ', 60) + "\U0001F697\U0001F697";

        var outcome = await h.Telegram(raw);

        Assert.Equal(SubmitKind.Queued, outcome.Kind);
        Assert.Equal(1, h.Offline.Calls);
    }

    [Fact]
    public async Task The_post_correction_check_still_applies()
    {
        using var h = Quiet();
        // 189 typed letters: under the early cap; the formatted vehicle text (separators added) is 201.
        var raw = "corolla prata abc1d23 " + new string('x', 167);

        var outcome = await h.Telegram(raw);

        Assert.Equal(SubmitKind.TooLong, outcome.Kind);
        Assert.Equal(201, outcome.Text.Length);
        Assert.Equal(1, h.Offline.Calls);
    }

    [Fact]
    public async Task The_operator_is_not_limited_and_is_still_corrected()
    {
        using var h = Quiet();

        var outcome = await h.Pc("corolla prata abc1d23 " + new string('x', 3000));

        Assert.Equal(SubmitKind.Queued, outcome.Kind);
        Assert.Equal(1, h.Offline.Calls);
    }

    [Fact]
    public async Task Preview_of_a_huge_member_message_reports_too_long_without_correcting()
    {
        using var h = Quiet();

        var preview = await h.Service.PreviewAsync(new string('x', 4000), SenderRole.Member, TestContext.Current.CancellationToken);

        Assert.True(preview.TooLong);
        Assert.Equal(RuleKind.Reject, preview.Kind);
        Assert.Equal(0, h.Gemini.Calls);
        Assert.Equal(0, h.Offline.Calls);
        Assert.Empty(h.Queue.History);
    }
}
