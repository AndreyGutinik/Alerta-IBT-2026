using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Telegram;

namespace AlertaIBT.Core.Tests.Services;

// The parts the master switch starts and stops must be able to start again, cleanly, after a stop.
public sealed class AlertsRestartTests : IDisposable
{
    private const string Plate = "gol branco aaa1111";
    private const string PlateText = "GOL BRANCO | PLACA: AAA-1111";

    private readonly FakeTelegramApi _api = new();
    private ServiceHarness? _h;
    private TelegramEngine? _engine;
    private TelegramPoller? _poller;

    public void Dispose()
    {
        _engine?.Dispose();
        _h?.Dispose();
    }

    private long Now() => _h!.Clock.GetUtcNow().ToUnixTimeSeconds();

    [Fact]
    public async Task The_alert_service_stops_hides_the_bar_and_keeps_the_queue_then_plays_it_again_after_a_start()
    {
        _h = new ServiceHarness();
        await _h.Pc(Plate);
        await _h.Pc("onix preto bbb2222");
        var first = await _h.Display.NextPass(1);

        await _h.Service.StopAsync();

        Assert.True(first.Cancelled); // the bar ended
        Assert.Equal(2, _h.Queue.Pending.Count); // nothing was lost: the cut alert is back at the front
        Assert.Equal(PlateText, _h.Queue.Pending[0].Text);

        _h.Service.Start();
        var again = await _h.Display.NextPass(2);

        Assert.Equal(PlateText, again.Text);
        again.Complete(true);
        await Wait.Until(() => _h.Queue.History.Count == 1, "the alert is shown after the restart");
        var second = await _h.Display.NextPass(3);
        Assert.Equal("ONIX PRETO | PLACA: BBB-2222", second.Text);
    }

    [Fact]
    public async Task While_stopped_the_alert_service_plays_nothing_and_a_second_stop_is_harmless()
    {
        _h = new ServiceHarness();
        await _h.Service.StopAsync();
        await _h.Service.StopAsync();

        await _h.Pc(Plate);
        await Wait.Settle();

        Assert.Empty(_h.Display.Texts);
        Assert.Single(_h.Queue.Pending);
    }

    [Fact]
    public async Task The_alert_service_can_stop_and_start_several_times()
    {
        _h = new ServiceHarness();
        for (var i = 0; i < 3; i++)
        {
            await _h.Service.StopAsync();
            _h.Service.Start();
        }

        await _h.Pc(Plate);
        var pass = await _h.Display.NextPass(1);

        Assert.Equal(PlateText, pass.Text);
        pass.Complete(true);
        await Wait.Until(() => _h.Queue.History.Count == 1);
    }

    [Fact]
    public async Task The_telegram_engine_stops_then_starts_again_and_takes_the_messages_that_came_meanwhile()
    {
        _h = new ServiceHarness();
        _poller = new TelegramPoller(_api.Client(), () => FakeTelegramApi.Chat, null, 0, (_, ct) => Task.Delay(1, ct));
        _engine = new TelegramEngine(_poller, _h.Service, _h.Notifier, _h.Hub, _h.Logger, _h.Clock);
        _api.Updates(FakeTelegramApi.Message(10, "#" + Plate, Now()));

        _engine.Start();
        var first = await _h.Display.NextPass(1);
        Assert.Equal(PlateText, first.Text);
        first.Complete(true);
        await Wait.Until(() => _h.Queue.History.Count == 1);

        await _engine.StopAsync();
        _api.Updates(FakeTelegramApi.Message(11, "#onix preto bbb2222", Now()));
        await Wait.Settle();
        Assert.Single(_h.Display.Texts); // nothing is polled while it is stopped

        _engine.Start();
        var second = await _h.Display.NextPass(2);

        Assert.Equal("ONIX PRETO | PLACA: BBB-2222", second.Text);
        Assert.Equal(TelegramState.Online, _h.Hub.TelegramState);
    }
}
