using AlertaIBT.App.Platform;
using AlertaIBT.App.Tests.ViewModels;
using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Services;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.App.Tests.Platform;

// "Idle" for the update: nothing queued, nothing on the bar, no sermon pause, no send in flight; and the signals that say it may have changed.
public sealed class AppUpdateHostTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 22, 14, 30, 0, TimeSpan.Zero));
    private readonly AlertQueue _queue;
    private readonly StatusHub _hub = new();
    private readonly FakeGateway _gateway;
    private readonly QuickPanelViewModel _panel;
    private readonly QuickPanelViewModel _windowPanel;
    private readonly List<string> _calls = [];
    private readonly AppUpdateHost _host;
    private int _signals;

    public AppUpdateHostTests()
    {
        _gateway = new FakeGateway(_time);
        _queue = _gateway.Queue;
        _panel = new QuickPanelViewModel(_gateway, _hub, clock: _time);
        _windowPanel = new QuickPanelViewModel(_gateway, _hub, clock: _time);
        _host = new AppUpdateHost(_queue, _hub, () => [_panel, _windowPanel], () => _calls.Add("save"), () => _calls.Add("exit"));
    }

    private static QueueItem Item(string id) =>
        new(id, "GOL BRANCO | PLACA: BRA-2E19", "BRA-2E19", AlertSource.Pc, false, new DateTimeOffset(2026, 10, 22, 14, 30, 0, TimeSpan.Zero), null, null);

    [Fact]
    public void An_empty_quiet_app_is_idle()
    {
        Assert.True(_host.IsIdle);
    }

    [Fact]
    public void A_waiting_alert_is_busy()
    {
        _queue.Enqueue(Item("1"), DuplicatePolicy.Block);

        Assert.False(_host.IsIdle);
    }

    [Fact]
    public void An_alert_on_the_bar_is_busy_and_the_app_is_idle_again_when_it_finishes()
    {
        _queue.Enqueue(Item("1"), DuplicatePolicy.Block);
        _queue.StartNext();
        _hub.SetShowing(true);

        Assert.False(_host.IsIdle);

        _queue.FinishCurrent();
        _hub.SetShowing(false);
        Assert.True(_host.IsIdle);
    }

    [Fact]
    public void The_sermon_pause_is_busy_because_a_restart_would_end_it()
    {
        _hub.SetPaused(true);

        Assert.False(_host.IsIdle);

        _hub.SetPaused(false);
        Assert.True(_host.IsIdle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_send_in_flight_in_either_panel_is_busy(bool windowPanel)
    {
        var gate = new TaskCompletionSource<SubmitOutcome>();
        _gateway.OnSubmit = (_, _) => gate.Task;
        var panel = windowPanel ? _windowPanel : _panel;
        panel.InputText = "gol branco abc1234";

        var sending = panel.SendAsync(false);
        Assert.False(_host.IsIdle);

        gate.SetResult(new SubmitOutcome(SubmitKind.Queued, "X", [], 1));
        await sending;
        _queue.Clear();
        Assert.True(_host.IsIdle);
    }

    [Fact]
    public async Task The_signal_fires_when_the_queue_the_bar_the_pause_or_a_send_changes()
    {
        _host.Attach(() => _signals++);

        _queue.Enqueue(Item("1"), DuplicatePolicy.Block);
        Assert.True(_signals >= 1);

        var before = _signals;
        _hub.SetShowing(true);
        Assert.True(_signals > before);

        before = _signals;
        _hub.SetPaused(true);
        Assert.True(_signals > before);

        var gate = new TaskCompletionSource<SubmitOutcome>();
        _gateway.OnSubmit = (_, _) => gate.Task;
        _panel.InputText = "gol branco abc1234";
        before = _signals;
        var sending = _panel.SendAsync(false);
        Assert.True(_signals > before);

        before = _signals;
        gate.SetResult(new SubmitOutcome(SubmitKind.Queued, "X", [], 1));
        await sending;
        Assert.True(_signals > before);
    }

    [Fact]
    public void A_change_that_does_not_matter_does_not_signal_and_attaching_twice_does_not_double_the_signals()
    {
        _host.Attach(() => _signals++);
        _host.Attach(() => _signals += 100);

        _hub.SetLedPresent(true);
        Assert.Equal(0, _signals);

        _queue.Enqueue(Item("1"), DuplicatePolicy.Block);
        Assert.True(_signals is >= 1 and < 100);
    }

    [Fact]
    public void Saving_and_leaving_go_to_the_apps_own_actions()
    {
        _host.PrepareForRestart();
        _host.Exit();

        Assert.Equal(["save", "exit"], _calls);
    }
}
