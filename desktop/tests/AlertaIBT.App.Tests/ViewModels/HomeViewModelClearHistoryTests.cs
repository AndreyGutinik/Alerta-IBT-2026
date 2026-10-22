using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Services;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.App.Tests.ViewModels;

// The "Limpar historico" button of the history: no question, the result line, and how long it stays.
public sealed class HomeViewModelClearHistoryTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly StatusHub _hub = new();
    private readonly FakeGateway _gateway;
    private readonly QuickPanelViewModel _composer;
    private readonly HomeViewModel _vm;

    public HomeViewModelClearHistoryTests()
    {
        _gateway = new FakeGateway(_clock);
        _composer = new QuickPanelViewModel(_gateway, _hub, clock: _clock);
        _vm = new HomeViewModel(_gateway, _composer, clock: _clock);
    }

    public void Dispose()
    {
        _vm.Dispose();
        _composer.Dispose();
    }

    private QueueItem Item(string id) => new(id, "TEXT " + id, null, AlertSource.Pc, false, _clock.GetUtcNow(), null, "x");

    private void Reject(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _gateway.Queue.AddRejected(Item("h" + i), HistoryReasons.TooLong);
        }
    }

    [Fact]
    public void Pressing_the_button_clears_the_history_once_and_asks_nothing()
    {
        Reject(12);
        Assert.Equal(12, _vm.History.Count);

        _vm.ClearHistoryCommand.Execute(null);

        Assert.Equal(1, _gateway.HistoryClears);
        Assert.Empty(_vm.History);
        Assert.True(_vm.HistoryIsEmpty);
    }

    [Fact]
    public void The_result_line_says_how_much_went_from_the_pc_the_site_and_telegram()
    {
        Reject(12);

        _vm.ClearHistoryCommand.Execute(null);

        Assert.Equal("Hist\U000000F3rico limpo: 12 no PC, 9 no site, 7 no Telegram.", _vm.HistoryNoticeText);
        Assert.Equal(FeedbackTone.Ok, _vm.HistoryNoticeTone);
        Assert.True(_vm.HasHistoryNotice);
    }

    [Fact]
    public void With_the_site_offline_the_line_says_it_will_be_cleaned_when_it_is_back()
    {
        Reject(3);
        _gateway.OnClearHistory = pc => Task.FromResult(new HistoryClearResult(pc, SiteClearOutcome.Pending, 0, 4, 0));

        _vm.ClearHistoryCommand.Execute(null);

        Assert.Equal("Hist\U000000F3rico limpo: 3 no PC, 4 no Telegram; o site ser\U000000E1 limpo quando voltar.", _vm.HistoryNoticeText);
        Assert.Equal(FeedbackTone.Warn, _vm.HistoryNoticeTone);
    }

    [Fact]
    public void Without_a_site_the_line_does_not_talk_about_it()
    {
        _gateway.OnClearHistory = pc => Task.FromResult(new HistoryClearResult(pc, SiteClearOutcome.NotConfigured, 0, 0, 0));

        _vm.ClearHistoryCommand.Execute(null);

        Assert.Equal("Hist\U000000F3rico limpo: 0 no PC, 0 no Telegram.", _vm.HistoryNoticeText);
        Assert.Equal(FeedbackTone.Ok, _vm.HistoryNoticeTone);
    }

    [Theory]
    [InlineData(1, " 1 mensagem do Telegram ficou (mais de 48 h ou sem permiss\U000000E3o).")]
    [InlineData(5, " 5 mensagens do Telegram ficaram (mais de 48 h ou sem permiss\U000000E3o).")]
    public void Telegram_messages_that_stay_are_told_with_why(int failed, string expectedEnd)
    {
        _gateway.OnClearHistory = pc => Task.FromResult(new HistoryClearResult(pc, SiteClearOutcome.Cleared, 2, 6, failed));

        _vm.ClearHistoryCommand.Execute(null);

        Assert.Equal("Hist\U000000F3rico limpo: 0 no PC, 2 no site, 6 no Telegram." + expectedEnd, _vm.HistoryNoticeText);
        Assert.Equal(FeedbackTone.Warn, _vm.HistoryNoticeTone);
    }

    [Fact]
    public void The_line_goes_away_after_a_few_seconds_and_not_before()
    {
        _vm.ClearHistoryCommand.Execute(null);

        _clock.Advance(HomeViewModel.HistoryNoticeTime - TimeSpan.FromSeconds(1));
        Assert.True(_vm.HasHistoryNotice);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(_vm.HasHistoryNotice);
        Assert.Equal(FeedbackTone.None, _vm.HistoryNoticeTone);
    }

    [Fact]
    public void A_second_press_restarts_the_time_of_the_line()
    {
        _vm.ClearHistoryCommand.Execute(null);
        _clock.Advance(TimeSpan.FromSeconds(5));
        _vm.ClearHistoryCommand.Execute(null);

        _clock.Advance(TimeSpan.FromSeconds(5));

        Assert.True(_vm.HasHistoryNotice);
    }

    [Fact]
    public void The_button_is_not_offered_again_while_a_clear_is_running()
    {
        var gate = new TaskCompletionSource<HistoryClearResult>();
        _gateway.OnClearHistory = _ => gate.Task;

        _vm.ClearHistoryCommand.Execute(null);

        Assert.False(_vm.ClearHistoryCommand.CanExecute(null)); // the button of the window is disabled meanwhile
        Assert.Equal(1, _gateway.HistoryClears);

        gate.SetResult(new HistoryClearResult(0, SiteClearOutcome.NotConfigured, 0, 0, 0));
        Assert.True(_vm.ClearHistoryCommand.CanExecute(null));
    }

    [Fact]
    public void A_failure_is_an_error_line_and_never_an_exception()
    {
        _gateway.OnClearHistory = _ => throw new InvalidOperationException("boom");

        _vm.ClearHistoryCommand.Execute(null);

        Assert.Equal("N\U000000E3o foi poss\U000000EDvel limpar o hist\U000000F3rico. Veja o registro (log).", _vm.HistoryNoticeText);
        Assert.Equal(FeedbackTone.Error, _vm.HistoryNoticeTone);
    }

    [Fact]
    public void The_queue_and_the_alert_on_the_bar_are_not_touched()
    {
        _gateway.Queue.Enqueue(Item("on-bar"), DuplicatePolicy.Warn);
        _gateway.Queue.StartNext();
        _gateway.Queue.Enqueue(Item("waiting"), DuplicatePolicy.Warn);
        Reject(2);

        _vm.ClearHistoryCommand.Execute(null);

        Assert.Equal(["waiting"], _vm.Queue.Select(q => q.Id));
        Assert.Equal("on-bar", _gateway.Queue.Current?.Id);
    }
}
