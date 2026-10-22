using AlertaIBT.Core.Firebase;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Telegram;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Tests.Services;

// "Limpar historico": the PC, the site and Telegram, with fakes for the last two.
public sealed class HistoryCleanerTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task It_clears_the_pc_history_the_site_and_telegram_and_reports_each()
    {
        var rig = new HistoryClearRig();
        rig.SiteIsOnline();
        rig.Site.Result = new SiteClearResult(9, 0);
        rig.Telegram.Result = new TelegramClearResult(7, 2);
        rig.FillHistory(12);

        var result = await rig.Cleaner.ClearAllAsync(Ct);

        Assert.Equal(new HistoryClearResult(12, SiteClearOutcome.Cleared, 9, 7, 2), result);
        Assert.Empty(rig.Queue.History);
        Assert.Equal(1, rig.Site.Calls);
        Assert.Equal(1, rig.Telegram.Calls);
    }

    [Fact]
    public async Task It_records_when_the_history_was_cleared()
    {
        var rig = new HistoryClearRig();
        rig.SiteIsOnline();

        await rig.Cleaner.ClearAllAsync(Ct);

        Assert.Equal(rig.Clock.GetUtcNow(), rig.Settings.System.LastHistoryClearAt);
    }

    [Fact]
    public async Task The_queue_and_the_duplicate_memory_are_not_touched()
    {
        var rig = new HistoryClearRig();
        rig.SiteIsOnline();
        rig.Queue.Enqueue(rig.Item("shown", "ABC-1D23"), DuplicatePolicy.Warn);
        rig.Queue.StartNext();
        rig.Queue.FinishCurrent();
        rig.Queue.Enqueue(rig.Item("on-screen"), DuplicatePolicy.Warn);
        rig.Queue.StartNext();
        rig.Queue.Enqueue(rig.Item("waiting"), DuplicatePolicy.Warn);

        await rig.Cleaner.ClearAllAsync(Ct);

        Assert.Equal("on-screen", rig.Queue.Current?.Id);
        Assert.Equal(["waiting"], rig.Queue.Pending.Select(p => p.Id));
        Assert.True(rig.Queue.CheckDuplicate("ABC-1D23").IsDuplicate);
    }

    [Fact]
    public async Task With_the_site_offline_the_pc_is_cleared_now_and_the_site_is_remembered_for_later()
    {
        var rig = new HistoryClearRig();
        rig.SiteIsOffline();
        rig.FillHistory(5);

        var result = await rig.Cleaner.ClearAllAsync(Ct);

        Assert.Equal(5, result.PcEntries);
        Assert.Equal(SiteClearOutcome.Pending, result.Site);
        Assert.Equal(0, rig.Site.Calls);
        Assert.True(rig.Settings.System.SitePendingClear);
        Assert.Empty(rig.Queue.History);
    }

    [Fact]
    public async Task When_the_site_comes_back_only_the_site_is_cleared_and_the_pc_is_not_cleared_again()
    {
        var rig = new HistoryClearRig();
        rig.SiteIsOffline();
        rig.FillHistory(5);
        await rig.Cleaner.ClearAllAsync(Ct);
        rig.FillHistory(2); // new lines made after the clear must survive the late site clear
        var telegramCalls = rig.Telegram.Calls;

        rig.SiteIsOnline();
        var ran = await rig.Cleaner.RunPendingSiteClearAsync(Ct);

        Assert.True(ran);
        Assert.Equal(1, rig.Site.Calls);
        Assert.False(rig.Settings.System.SitePendingClear);
        Assert.Equal(2, rig.Queue.History.Count);
        Assert.Equal(telegramCalls, rig.Telegram.Calls);
    }

    [Fact]
    public async Task Nothing_is_pending_means_the_site_is_left_alone_when_it_comes_online()
    {
        var rig = new HistoryClearRig();
        rig.SiteIsOnline();

        Assert.False(await rig.Cleaner.RunPendingSiteClearAsync(Ct));
        Assert.Equal(0, rig.Site.Calls);
    }

    [Fact]
    public async Task A_pending_site_that_is_still_offline_stays_pending()
    {
        var rig = new HistoryClearRig();
        rig.SiteIsOffline();
        await rig.Cleaner.ClearAllAsync(Ct);

        Assert.False(await rig.Cleaner.RunPendingSiteClearAsync(Ct));

        Assert.True(rig.Settings.System.SitePendingClear);
        Assert.Equal(0, rig.Site.Calls);
    }

    [Fact]
    public async Task A_configured_site_with_no_connection_object_yet_is_pending_too()
    {
        var rig = new HistoryClearRig();
        rig.CurrentSite = null;

        var result = await rig.Cleaner.ClearAllAsync(Ct);

        Assert.Equal(SiteClearOutcome.Pending, result.Site);
        Assert.True(rig.Settings.System.SitePendingClear);
    }

    [Fact]
    public async Task A_site_that_is_not_configured_is_skipped_and_never_pending()
    {
        var rig = new HistoryClearRig(siteConfigured: false);
        rig.SiteIsOnline();
        rig.FillHistory(3);

        var result = await rig.Cleaner.ClearAllAsync(Ct);

        Assert.Equal(SiteClearOutcome.NotConfigured, result.Site);
        Assert.Equal(3, result.PcEntries);
        Assert.Equal(0, rig.Site.Calls);
        Assert.False(rig.Settings.System.SitePendingClear);
    }

    [Fact]
    public async Task A_site_that_fails_does_not_stop_the_pc_or_telegram_and_nothing_is_thrown()
    {
        var rig = new HistoryClearRig();
        rig.SiteIsOnline();
        rig.Site.Throw = new RtdbException(RtdbFailure.Network, "down");
        rig.FillHistory(4);

        var result = await rig.Cleaner.ClearAllAsync(Ct);

        Assert.Equal(4, result.PcEntries);
        Assert.Equal(SiteClearOutcome.Pending, result.Site);
        Assert.Equal(3, result.TelegramDeleted);
        Assert.True(rig.Settings.System.SitePendingClear);
    }

    [Fact]
    public async Task Some_site_requests_that_stay_keep_the_site_pending_with_the_count_that_went()
    {
        var rig = new HistoryClearRig();
        rig.SiteIsOnline();
        rig.Site.Result = new SiteClearResult(5, 2);

        var result = await rig.Cleaner.ClearAllAsync(Ct);

        Assert.Equal(SiteClearOutcome.Pending, result.Site);
        Assert.Equal(5, result.SiteRequests);
        Assert.True(rig.Settings.System.SitePendingClear);
    }

    [Fact]
    public async Task A_telegram_that_throws_still_leaves_the_rest_done()
    {
        var rig = new HistoryClearRig();
        rig.SiteIsOnline();
        rig.Telegram.Throw = new InvalidOperationException("boom");
        rig.FillHistory(2);

        var result = await rig.Cleaner.ClearAllAsync(Ct);

        Assert.Equal(new HistoryClearResult(2, SiteClearOutcome.Cleared, 4, 0, 0), result);
        Assert.Contains(rig.Log.Entries, e => e.Level == LogLevel.Error);
        Assert.NotNull(rig.Settings.System.LastHistoryClearAt);
    }

    [Fact]
    public async Task Telegram_partial_failure_is_reported_as_is()
    {
        var rig = new HistoryClearRig();
        rig.SiteIsOnline();
        rig.Telegram.Result = new TelegramClearResult(5, 3);

        var result = await rig.Cleaner.ClearAllAsync(Ct);

        Assert.Equal(5, result.TelegramDeleted);
        Assert.Equal(3, result.TelegramFailed);
    }

    [Fact]
    public async Task Two_clears_asked_at_once_are_one_and_share_the_result()
    {
        var rig = new HistoryClearRig();
        rig.SiteIsOnline();
        rig.FillHistory(6);
        rig.Telegram.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = rig.Cleaner.ClearAllAsync(Ct);
        await Wait.Until(() => rig.Telegram.Calls == 1, "the first clear reached Telegram");
        var second = rig.Cleaner.ClearAllAsync(Ct);
        await Wait.Settle();
        rig.Telegram.Hold.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, rig.Telegram.Calls);
        Assert.Equal(1, rig.Site.Calls);
        Assert.Equal(6, results[0].PcEntries);
        Assert.Same(results[0], results[1]);
    }

    [Fact]
    public async Task A_clear_after_the_first_one_finished_runs_again()
    {
        var rig = new HistoryClearRig();
        rig.SiteIsOnline();
        rig.FillHistory(1);
        await rig.Cleaner.ClearAllAsync(Ct);
        rig.FillHistory(2);

        var again = await rig.Cleaner.ClearAllAsync(Ct);

        Assert.Equal(2, again.PcEntries);
        Assert.Equal(2, rig.Telegram.Calls);
    }
}
