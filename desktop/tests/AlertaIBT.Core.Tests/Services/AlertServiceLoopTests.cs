using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Telegram;

namespace AlertaIBT.Core.Tests.Services;

public class AlertServiceLoopTests
{
    private const string Good = "corolla prata abc1d23 vidro aberto";
    private const string GoodText = "COROLLA PRATA | PLACA: ABC-1D23 | VIDRO ABERTO";

    // Moves the fake clock until the condition holds: the loop's retry delay lives on the fake clock.
    private static async Task AdvanceUntil(ServiceHarness h, Func<bool> condition, TimeSpan? step = null)
    {
        var deadline = DateTime.UtcNow + Wait.Limit;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            h.Clock.Advance(step ?? TimeSpan.FromSeconds(1));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "the condition was not met in time");
    }

    // ---- playing ----

    [Fact]
    public async Task A_queued_alert_plays_and_a_telegram_sender_hears_it_was_shown()
    {
        using var h = new ServiceHarness();

        await h.Telegram(Good);
        var pass = await h.Display.NextPass(1);
        Assert.Equal(GoodText, pass.Text);
        Assert.True(h.Hub.Showing);
        Assert.Equal(AlertPill.Showing, h.Hub.Pill);
        Assert.Equal(GoodText, h.Queue.Current?.Text);
        Assert.Empty(h.Notifier.Sent);

        pass.Complete(true);

        await Wait.Until(() => h.Queue.History.Count == 1, "the alert is in the history");
        Assert.Equal(HistoryState.Shown, h.Queue.History[0].State);
        Assert.Null(h.Queue.Current);
        Assert.Equal([TelegramReplies.Shown(GoodText)], h.Notifier.Sent);
        await Wait.Until(() => !h.Hub.Showing);
        Assert.Equal(AlertPill.Ready, h.Hub.Pill);
    }

    [Fact]
    public async Task A_pc_alert_is_shown_without_a_word_in_the_group_and_a_site_alert_is_confirmed_there()
    {
        using var h = new ServiceHarness();
        await h.Pc("gol branco aaa1111");
        (await h.Display.NextPass(1)).Complete(true);
        await Wait.Until(() => h.Queue.History.Count == 1);
        Assert.Empty(h.Notifier.Sent);

        await h.Service.SubmitAsync("onix preto bbb2222", AlertSource.Site, SenderRole.Member, false, "u1", TestContext.Current.CancellationToken);
        (await h.Display.NextPass(2)).Complete(true);

        await Wait.Until(() => h.Notifier.Sent.Length == 1);
        Assert.Equal(TelegramReplies.Shown("ONIX PRETO | PLACA: BBB-2222"), h.Notifier.Sent[0]);
    }

    [Fact]
    public async Task Alerts_play_one_at_a_time_in_queue_order()
    {
        using var h = new ServiceHarness();
        await h.Pc("gol branco aaa1111");
        await h.Pc("onix preto bbb2222");

        var first = await h.Display.NextPass(1);
        await Wait.Settle();
        Assert.Single(h.Display.Texts);
        Assert.Single(h.Queue.Pending);

        first.Complete(true);
        var second = await h.Display.NextPass(2);

        Assert.Equal("ONIX PRETO | PLACA: BBB-2222", second.Text);
    }

    [Fact]
    public async Task Skip_ends_the_current_alert_counts_it_as_shown_and_starts_the_next()
    {
        using var h = new ServiceHarness();
        await h.Pc("gol branco aaa1111");
        await h.Pc("onix preto bbb2222");
        var first = await h.Display.NextPass(1);

        h.Service.Skip();

        var second = await h.Display.NextPass(2);
        Assert.True(first.Cancelled);
        Assert.Equal(HistoryState.Shown, h.Queue.History[0].State);
        Assert.Equal("GOL BRANCO | PLACA: AAA-1111", h.Queue.History[0].Item.Text);
        Assert.Equal("ONIX PRETO | PLACA: BBB-2222", second.Text);
    }

    [Fact]
    public async Task Clear_drops_the_waiting_alerts_and_lets_the_current_one_finish()
    {
        using var h = new ServiceHarness();
        await h.Pc("gol branco aaa1111");
        await h.Pc("onix preto bbb2222");
        var first = await h.Display.NextPass(1);

        h.Service.Clear();

        Assert.Empty(h.Queue.Pending);
        Assert.False(first.Cancelled);
        first.Complete(true);
        await Wait.Until(() => h.Queue.History.Any(e => e.State == HistoryState.Shown));
        Assert.Single(h.Display.Texts);
    }

    // ---- the sermon ----

    [Fact]
    public async Task Pause_cancels_the_bar_and_the_queue_tells_the_group_once_and_refuses_new_alerts()
    {
        using var h = new ServiceHarness();
        await h.Telegram(Good);
        await h.Pc("onix preto bbb2222");
        var pass = await h.Display.NextPass(1);

        h.Service.Pause();

        Assert.True(pass.Cancelled);
        Assert.Null(h.Queue.Current);
        Assert.Empty(h.Queue.Pending);
        Assert.True(h.Service.IsPaused);
        Assert.True(h.Hub.Paused);
        Assert.Equal(AlertPill.Paused, h.Hub.Pill);
        Assert.Equal(StatusLight.Yellow, h.Hub.Bar.Light);
        Assert.Equal(2, h.Queue.History.Count(e => e.State == HistoryState.Cancelled && e.Reason == HistoryReasons.Sermon));
        Assert.Equal([TelegramReplies.Paused], h.Notifier.Sent);

        h.Service.Pause();
        h.Service.Pause();
        Assert.Equal([TelegramReplies.Paused], h.Notifier.Sent); // once

        var refused = await h.Telegram("gol branco ccc3333");
        Assert.Equal(SubmitKind.PausedRefused, refused.Kind);
        await Wait.Settle();
        Assert.Single(h.Display.Texts); // nothing new reached the bar
    }

    [Fact]
    public async Task A_pass_that_ends_ok_just_after_the_pause_is_not_reported_as_shown()
    {
        using var h = new ServiceHarness();
        h.Display.CancelEndsPass = false;
        await h.Telegram(Good);
        var pass = await h.Display.NextPass(1);

        h.Service.Pause();
        pass.Complete(true); // too late: the pause already cancelled the alert
        await Wait.Settle();

        Assert.DoesNotContain(h.Queue.History, e => e.State == HistoryState.Shown);
        Assert.Equal([TelegramReplies.Paused], h.Notifier.Sent);
    }

    [Fact]
    public async Task Resume_is_silent_accepts_alerts_again_and_starts_with_an_empty_queue()
    {
        using var h = new ServiceHarness();
        await h.Pc("gol branco aaa1111");
        await h.Display.NextPass(1);
        h.Service.Pause();
        var sent = h.Notifier.Sent.Length;

        h.Service.Resume();

        Assert.False(h.Service.IsPaused);
        Assert.False(h.Hub.Paused);
        Assert.Equal(sent, h.Notifier.Sent.Length);
        Assert.Empty(h.Queue.Pending);
        await h.Pc("onix preto bbb2222");
        var next = await h.Display.NextPass(2);
        Assert.Equal("ONIX PRETO | PLACA: BBB-2222", next.Text);
    }

    [Fact]
    public void A_pause_with_nothing_queued_still_tells_the_group_once()
    {
        using var h = new ServiceHarness();

        h.Service.Pause();

        Assert.Equal([TelegramReplies.Paused], h.Notifier.Sent);
    }

    [Fact]
    public async Task Pausing_and_resuming_from_many_threads_leaves_a_consistent_service()
    {
        using var h = new ServiceHarness();
        await h.Pc("gol branco aaa1111");
        await h.Display.NextPass(1);

        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            for (var n = 0; n < 50; n++)
            {
                if ((i + n) % 2 == 0)
                {
                    h.Service.Pause();
                }
                else
                {
                    h.Service.Resume();
                }
            }
        }, TestContext.Current.CancellationToken)).ToArray();
        await Task.WhenAll(tasks);
        h.Service.Resume();

        await h.Pc("onix preto bbb2222");

        await Wait.Until(() => h.Display.Texts.Contains("ONIX PRETO | PLACA: BBB-2222"), "the new alert reaches the bar");
        Assert.False(h.Service.IsPaused);
    }

    [Fact]
    public async Task A_pause_that_lands_between_taking_an_alert_and_starting_its_pass_keeps_it_off_the_bar()
    {
        using var h = new ServiceHarness(start: false);
        var fired = false;
        h.Queue.Changed += (_, _) =>
        {
            // StartNext raises Changed before it returns to the loop: this is the exact gap the loop must survive.
            if (!fired && h.Queue.Current is not null)
            {
                fired = true;
                h.Service.Pause();
            }
        };
        h.Service.Start();

        await h.Pc(Good);
        await Wait.Until(() => fired, "the alert was taken from the queue");
        await Wait.Settle();

        Assert.Empty(h.Display.Texts); // never reached the bar
        Assert.Null(h.Queue.Current);
        Assert.Equal(HistoryState.Cancelled, Assert.Single(h.Queue.History).State);
        Assert.Equal(AlertPill.Paused, h.Hub.Pill);
        h.Service.Resume();
        await h.Pc("onix preto bbb2222");
        Assert.Equal("ONIX PRETO | PLACA: BBB-2222", (await h.Display.NextPass(1)).Text); // and the loop is still alive
    }

    [Fact]
    public async Task Racing_submits_pauses_and_resumes_never_leave_an_alert_on_the_bar_while_paused()
    {
        using var h = new ServiceHarness();
        h.Display.AutoResult = null;
        using var stop = new CancellationTokenSource();

        var producer = Task.Run(async () =>
        {
            var n = 0;
            while (!stop.IsCancellationRequested)
            {
                await h.Service.SubmitAsync($"gol branco aa{n % 10}{n % 7}{n % 5}{n % 3}", AlertSource.Pc, SenderRole.Operator, false, null, CancellationToken.None);
                n++;
            }
        }, CancellationToken.None);
        var toggler = Task.Run(async () =>
        {
            for (var i = 0; i < 3000; i++)
            {
                h.Service.Pause();
                if (i % 3 == 0)
                {
                    await Task.Yield();
                }

                h.Service.Resume();
                if (i % 5 == 0)
                {
                    await Task.Yield();
                }
            }
        }, CancellationToken.None);
        var finisher = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                h.Display.Current?.Complete(true);
                await Task.Delay(1, CancellationToken.None);
            }
        }, CancellationToken.None);

        await toggler;
        await stop.CancelAsync();
        await Task.WhenAll(producer, finisher);

        h.Service.Pause();
        await Wait.Settle();

        Assert.True(h.Service.IsPaused);
        Assert.Null(h.Queue.Current);
        Assert.Empty(h.Queue.Pending);
        Assert.Null(h.Display.Current); // no pass is left running under the pause
        Assert.False(h.Hub.Showing);
        Assert.Equal(AlertPill.Paused, h.Hub.Pill);
    }

    // ---- the LED ----

    [Fact]
    public async Task With_the_LED_off_a_telegram_sender_is_told_once_per_alert_and_it_plays_when_the_LED_returns()
    {
        using var h = new ServiceHarness();
        h.Display.SetLed(false);
        Assert.Equal(AlertPill.LedOff, h.Hub.Pill);

        await h.Telegram(Good);
        await h.Telegram("gol branco aaa1111");
        await h.Pc("onix preto bbb2222"); // a PC alert is never told in the group
        await Wait.Settle();

        Assert.Equal([TelegramReplies.LedOff, TelegramReplies.LedOff], h.Notifier.Sent);
        Assert.Empty(h.Display.Texts);

        h.Display.SetLed(true);
        var pass = await h.Display.NextPass(1);

        Assert.Equal(GoodText, pass.Text);
        Assert.Equal([TelegramReplies.LedOff, TelegramReplies.LedOff], h.Notifier.Sent); // still once each
    }

    [Fact]
    public async Task The_LED_turning_off_tells_the_waiting_telegram_alerts_once_even_if_it_flickers()
    {
        using var h = new ServiceHarness();
        await h.Telegram(Good);
        await h.Display.NextPass(1);
        await h.Telegram("gol branco aaa1111");

        h.Display.SetLed(false);
        h.Display.SetLed(true);
        h.Display.SetLed(false);
        await Wait.Settle();

        Assert.Equal(2, h.Notifier.Sent.Count(m => m == TelegramReplies.LedOff));
    }

    [Fact]
    public async Task The_LED_vanishing_in_the_middle_of_a_scroll_puts_the_alert_back_at_the_front_without_loss()
    {
        using var h = new ServiceHarness();
        await h.Telegram(Good);
        await h.Pc("onix preto bbb2222");
        var first = await h.Display.NextPass(1);

        h.Display.SetLed(false);

        await Wait.Until(() => h.Queue.Current is null, "the alert left the screen");
        Assert.Equal([GoodText, "ONIX PRETO | PLACA: BBB-2222"], h.Queue.Pending.Select(p => p.Text));
        Assert.DoesNotContain(h.Queue.History, e => e.State == HistoryState.Shown);
        Assert.Equal(AlertPill.LedOff, h.Hub.Pill);
        Assert.Equal(StatusLight.Red, h.Hub.Led.Light);
        Assert.Equal([TelegramReplies.LedOff], h.Notifier.Sent);
        await Wait.Settle();
        Assert.Single(h.Display.Texts); // nothing is replayed while the LED is off

        h.Display.SetLed(true);
        var again = await h.Display.NextPass(2);

        Assert.Equal(GoodText, again.Text); // from the start, the same alert
        Assert.NotSame(first, again);
        again.Complete(true);
        await Wait.Until(() => h.Queue.History.Count == 1);
        Assert.Equal(2, h.Notifier.Sent.Length);
        Assert.Equal(TelegramReplies.Shown(GoodText), h.Notifier.Sent[1]);
    }

    [Fact]
    public async Task A_pass_cut_short_with_the_LED_still_there_is_retried_after_a_growing_pause_not_in_a_hot_loop()
    {
        using var h = new ServiceHarness();
        h.Display.AutoResult = false;
        await h.Pc(Good);
        await h.Display.NextPass(1);

        await Wait.Settle();
        Assert.Single(h.Display.Texts); // the clock did not move: no second try yet

        h.Clock.Advance(TimeSpan.FromMilliseconds(500));
        await h.Display.NextPass(2);
        await Wait.Settle();
        Assert.Equal(2, h.Display.Texts.Length);

        h.Clock.Advance(TimeSpan.FromMilliseconds(999)); // the second wait is one second
        await Wait.Settle();
        Assert.Equal(2, h.Display.Texts.Length);
        h.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await h.Display.NextPass(3);

        h.Display.AutoResult = null;
        await AdvanceUntil(h, () => h.Display.Current is not null);
        h.Display.Current!.Complete(true);
        await Wait.Until(() => h.Queue.History.Count == 1);
        Assert.Equal(GoodText, h.Queue.History[0].Item.Text); // the alert survived all the failures
    }

    [Fact]
    public async Task A_bar_that_throws_does_not_kill_the_loop_and_the_error_is_reported()
    {
        using var h = new ServiceHarness();
        h.Display.ThrowOnShow = new InvalidOperationException("window died");

        await h.Pc(Good);
        await Wait.Until(() => h.Hub.LastError is not null, "the error reaches the hub");

        Assert.Contains("window died", h.Hub.LastError);
        Assert.Contains(h.Logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error && e.Message.Contains("mostrar"));
        await AdvanceUntil(h, () => h.Display.Texts.Length == 1);
        h.Display.Current!.Complete(true);
        await Wait.Until(() => h.Queue.History.Count == 1);
        Assert.Equal(GoodText, h.Queue.History[0].Item.Text);
    }

    // ---- expiry ----

    [Fact]
    public async Task An_alert_waiting_more_than_30_minutes_expires_and_its_telegram_sender_is_told_once()
    {
        using var h = new ServiceHarness();
        h.Display.SetLed(false);
        await h.Telegram(Good);
        await h.Pc("onix preto bbb2222");
        Assert.Equal([TelegramReplies.LedOff], h.Notifier.Sent);

        h.Clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(2, h.Queue.Pending.Count);

        h.Clock.Advance(TimeSpan.FromMinutes(2));

        await Wait.Until(() => h.Queue.Pending.Count == 0, "the alerts expire");
        Assert.All(h.Queue.History, e => Assert.Equal(HistoryState.Expired, e.State));
        Assert.Equal([TelegramReplies.LedOff, TelegramReplies.Expired], h.Notifier.Sent);
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        await Wait.Settle();
        Assert.Equal(2, h.Notifier.Sent.Length);
    }

    [Fact]
    public async Task Alerts_restored_from_disk_that_are_stale_expire_at_start_and_only_recent_ones_are_announced()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero));
        var old = new QueueItem("old", "OLD", null, AlertSource.Telegram, false, clock.GetUtcNow() - TimeSpan.FromDays(3), "1", null);
        var stale = new QueueItem("stale", "STALE", null, AlertSource.Telegram, false, clock.GetUtcNow() - TimeSpan.FromMinutes(45), "1", null);
        var fresh = new QueueItem("fresh", "FRESH", null, AlertSource.Telegram, false, clock.GetUtcNow() - TimeSpan.FromMinutes(5), "1", null);
        var queue = AlertQueue.FromSnapshot(new QueueSnapshot([old, stale, fresh], []), clock);
        using var h = new ServiceHarness(start: false, queue: queue);
        h.Clock.SetUtcNow(clock.GetUtcNow());
        h.Display.SetLed(false);

        h.Service.Start();

        Assert.Equal(["fresh"], h.Queue.Pending.Select(p => p.Id));
        Assert.Equal([TelegramReplies.Expired], h.Notifier.Sent); // "stale" only; the 3 day old one is not worth a message
    }

    [Fact]
    public async Task Gemini_answering_turns_the_status_online_and_failing_turns_it_offline()
    {
        var ai = new ParsedAlert("Gol", "Branco", "AAA-1111", string.Empty, "Gol Branco AAA-1111");
        var online = true;
        using var h = new ServiceHarness((_, _) => Task.FromResult<ParsedAlert?>(online ? ai : null));
        h.Display.SetLed(false);
        Assert.Equal(GeminiState.Unknown, h.Hub.GeminiState);

        await h.Pc("gol branco aaa1111");
        Assert.Equal(GeminiState.Online, h.Hub.GeminiState);
        Assert.Equal(StatusLight.Green, h.Hub.Gemini.Light);

        online = false;
        await h.Pc("onix preto bbb2222");
        Assert.Equal(GeminiState.Offline, h.Hub.GeminiState);
        Assert.Equal(StatusLight.Blue, h.Hub.Gemini.Light);
    }

    [Fact]
    public async Task Replay_during_the_pause_is_refused_and_replay_is_not_a_telegram_reply()
    {
        using var h = new ServiceHarness();
        h.Service.Pause();

        var outcome = h.Service.Replay(new QueueItem("x", "GOL", null, AlertSource.Telegram, false, h.Clock.GetUtcNow(), "1", null));

        Assert.Equal(SubmitKind.PausedRefused, outcome.Kind);
        Assert.Equal([TelegramReplies.Paused], h.Notifier.Sent);
        await Task.CompletedTask;
    }

    [Fact]
    public void Starting_twice_and_disposing_without_a_start_are_harmless()
    {
        using var started = new ServiceHarness();
        started.Service.Start();
        started.Service.Dispose();
        started.Service.Dispose();

        var idle = new ServiceHarness(start: false);
        idle.Dispose();
    }

    // ---- shutdown ----

    [Fact]
    public async Task Stopping_the_service_puts_the_alert_on_the_bar_back_in_the_queue_to_be_saved()
    {
        using var h = new ServiceHarness();
        await h.Pc(Good);
        var pass = await h.Display.NextPass(1);

        await h.Service.StopAsync();

        Assert.True(pass.Cancelled);
        Assert.Null(h.Queue.Current);
        Assert.Equal([GoodText], h.Queue.Pending.Select(p => p.Text));
        Assert.Empty(h.Queue.History);
    }

    [Fact]
    public async Task Restored_alerts_play_after_a_restart_and_are_not_announced_again_for_the_LED()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero));
        var item = new QueueItem("a", "COROLLA PRATA", null, AlertSource.Telegram, false, clock.GetUtcNow(), "1", null);
        var queue = AlertQueue.FromSnapshot(new QueueSnapshot([item], []), clock);
        using var h = new ServiceHarness(start: false, queue: queue);
        h.Clock.SetUtcNow(clock.GetUtcNow());
        h.Display.SetLed(false);
        h.Service.Start();
        await Wait.Settle();
        Assert.Empty(h.Notifier.Sent); // it was told before the restart

        h.Display.SetLed(true);

        var pass = await h.Display.NextPass(1);
        Assert.Equal("COROLLA PRATA", pass.Text);
    }
}
