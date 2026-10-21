using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Correction;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Telegram;

namespace AlertaIBT.Core.Tests.Services;

public sealed class TelegramEngineTests : IDisposable
{
    private readonly FakeTelegramApi _api = new();
    private readonly List<long> _savedOffsets = [];
    private TelegramEngine? _engine;
    private ServiceHarness? _h;
    private TelegramPoller? _poller;

    private ServiceHarness Harness(Func<string, CancellationToken, Task<ParsedAlert?>>? gemini = null)
    {
        _h = new ServiceHarness(gemini);
        _poller = new TelegramPoller(_api.Client(), () => FakeTelegramApi.Chat, _savedOffsets.Add, 0, (_, ct) => Task.Delay(1, ct));
        _engine = new TelegramEngine(_poller, _h.Service, _h.Notifier, _h.Hub, _h.Logger, _h.Clock);
        return _h;
    }

    private long Now(TimeSpan? age = null) => (_h!.Clock.GetUtcNow() - (age ?? TimeSpan.Zero)).ToUnixTimeSeconds();

    public void Dispose()
    {
        _engine?.Dispose();
        _h?.Dispose();
    }

    [Fact]
    public async Task A_fresh_message_becomes_an_alert_is_shown_and_answered_in_the_group()
    {
        var h = Harness();
        _api.Updates(FakeTelegramApi.Message(10, "#corolla prata abc1d23 vidro aberto", Now()));

        _engine!.Start();
        var pass = await h.Display.NextPass(1);

        Assert.Equal("COROLLA PRATA | PLACA: ABC-1D23 | VIDRO ABERTO", pass.Text);
        var item = h.Queue.Current!;
        Assert.Equal(AlertSource.Telegram, item.Source);
        Assert.Equal("5", item.SenderId); // the Telegram user id
        pass.Complete(true);
        await Wait.Until(() => h.Notifier.Sent.Length == 1);
        Assert.Equal(TelegramReplies.Shown("COROLLA PRATA | PLACA: ABC-1D23 | VIDRO ABERTO"), h.Notifier.Sent[0]);
        await Wait.Until(() => _poller!.Offset == 11);
        Assert.Equal(TelegramState.Online, h.Hub.TelegramState);
        Assert.Equal(StatusLight.Green, h.Hub.Telegram.Light);
    }

    [Fact]
    public async Task A_message_without_a_plate_is_answered_with_what_is_missing()
    {
        var h = Harness();
        _api.Updates(FakeTelegramApi.Message(10, "#gol branco farol aceso", Now()));

        _engine!.Start();

        await Wait.Until(() => h.Notifier.Sent.Length == 1);
        Assert.Equal(TelegramReplies.Missing(["placa"]), h.Notifier.Sent[0]);
        Assert.Empty(h.Display.Texts);
    }

    [Fact]
    public async Task A_message_older_than_30_minutes_never_reaches_the_LED_but_its_sender_is_told_and_the_offset_moves_on()
    {
        var h = Harness();
        _api.Updates(
            FakeTelegramApi.Message(10, "#corolla prata abc1d23 vidro aberto", Now(TimeSpan.FromMinutes(45)))
            + "," + FakeTelegramApi.Message(11, "#gol branco aaa1111", Now(TimeSpan.FromMinutes(31)))
            + "," + FakeTelegramApi.Message(12, "#onix preto bbb2222", Now(TimeSpan.FromMinutes(29))));

        _engine!.Start();
        var pass = await h.Display.NextPass(1);

        Assert.Equal("ONIX PRETO | PLACA: BBB-2222", pass.Text); // only the fresh one
        await Wait.Until(() => _poller!.Offset == 13);
        await Wait.Settle();
        Assert.Single(h.Display.Texts);
        Assert.Equal([TelegramReplies.Expired, TelegramReplies.Expired], h.Notifier.Sent);
        Assert.Empty(h.Queue.History);
    }

    [Fact]
    public async Task A_message_from_yesterday_is_dropped_silently_but_confirmed()
    {
        var h = Harness();
        _api.Updates(FakeTelegramApi.Message(20, "#corolla prata abc1d23 vidro aberto", Now(TimeSpan.FromHours(20))));

        _engine!.Start();

        await Wait.Until(() => _poller!.Offset == 21);
        await Wait.Settle();
        Assert.Empty(h.Display.Texts);
        Assert.Empty(h.Notifier.Sent);
        Assert.Empty(h.Queue.History);
        Assert.Contains(21, _savedOffsets);
    }

    [Fact]
    public async Task Messages_from_another_chat_or_without_the_hash_are_ignored_but_confirmed()
    {
        var h = Harness();
        _api.Updates(
            FakeTelegramApi.Message(30, "#corolla prata abc1d23", Now(), chat: -999)
            + "," + FakeTelegramApi.Message(31, "bom dia a todos", Now())
            + "," + FakeTelegramApi.Message(32, "#", Now()));

        _engine!.Start();

        await Wait.Until(() => _poller!.Offset == 33);
        await Wait.Settle();
        Assert.Empty(h.Display.Texts);
        Assert.Empty(h.Notifier.Sent);
    }

    [Fact]
    public async Task A_409_conflict_shows_the_conflict_status_and_processes_nothing_twice()
    {
        var h = Harness();
        var seen = new System.Collections.Concurrent.ConcurrentQueue<StatusIndicator>();
        h.Hub.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StatusHub.Telegram))
            {
                seen.Enqueue(h.Hub.Telegram);
            }
        };
        _api.Conflict().Conflict().Updates(FakeTelegramApi.Message(40, "#corolla prata abc1d23 vidro aberto", Now()));

        _engine!.Start();

        await Wait.Until(() => seen.Any(i => i.Light == StatusLight.Red && i.Detail.Contains("antigo")), "the conflict status with its hint");

        var pass = await h.Display.NextPass(1); // once the other consumer is gone the message arrives, exactly once
        Assert.Equal("COROLLA PRATA | PLACA: ABC-1D23 | VIDRO ABERTO", pass.Text);
        await Wait.Until(() => h.Hub.TelegramState == TelegramState.Online);
        await Wait.Settle();
        Assert.Single(h.Display.Texts);
        Assert.Single(h.Logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error && e.Message.Contains("409")); // two 409s, one line
    }

    [Fact]
    public async Task Messages_are_handled_in_order_and_a_slow_correction_does_not_hold_up_the_poll()
    {
        var gate = new TaskCompletionSource<ParsedAlert?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        var h = Harness((raw, _) =>
        {
            if (first)
            {
                first = false;
                return gate.Task;
            }

            return Task.FromResult<ParsedAlert?>(null);
        });
        _api.Updates(
            FakeTelegramApi.Message(50, "#gol branco aaa1111", Now())
            + "," + FakeTelegramApi.Message(51, "#onix preto bbb2222", Now()));

        _engine!.Start();

        await Wait.Until(() => _poller!.Offset == 52, "the poller moved past both while the first is still being corrected");
        Assert.Empty(h.Display.Texts);
        gate.SetResult(null);
        var one = await h.Display.NextPass(1);
        Assert.Equal("GOL BRANCO | PLACA: AAA-1111", one.Text);
        one.Complete(true);
        var two = await h.Display.NextPass(2);
        Assert.Equal("ONIX PRETO | PLACA: BBB-2222", two.Text);
    }

    [Fact]
    public async Task A_message_that_makes_the_service_throw_is_reported_and_the_next_one_still_works()
    {
        var h = Harness();
        var calls = 0;
        h.SettingsProvider = () => ++calls == 1 ? throw new InvalidOperationException("settings broke") : h.Settings;
        _api.Updates(
            FakeTelegramApi.Message(60, "#gol branco aaa1111", Now())
            + "," + FakeTelegramApi.Message(61, "#onix preto bbb2222", Now()));

        _engine!.Start();
        var pass = await h.Display.NextPass(1);

        Assert.Equal("ONIX PRETO | PLACA: BBB-2222", pass.Text);
        Assert.Contains("settings broke", h.Hub.LastError);
    }

    [Fact]
    public async Task Without_token_or_chat_the_state_is_not_configured_and_stopping_is_quick()
    {
        _h = new ServiceHarness();
        var client = new TelegramClient(new HttpClient(_api), () => null);
        _poller = new TelegramPoller(client, () => FakeTelegramApi.Chat, null, 0, (_, ct) => Task.Delay(1, ct));
        _engine = new TelegramEngine(_poller, _h.Service, _h.Notifier, _h.Hub, _h.Logger, _h.Clock);

        _engine.Start();
        await Wait.Settle();

        Assert.Equal(TelegramState.NotConfigured, _h.Hub.TelegramState);
        Assert.Equal(StatusLight.Gray, _h.Hub.Telegram.Light);
        Assert.Equal(0, _api.Polls);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await _engine.StopAsync();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Stopping_while_a_long_poll_is_hanging_returns_promptly_and_is_safe_to_repeat()
    {
        var h = Harness();
        _engine!.Start();
        await Wait.Until(() => _api.Polls >= 1, "the poll started");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await _engine.StopAsync();
        await _engine.StopAsync();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
        Assert.NotNull(h);
    }
}
