using System.Net;
using System.Net.Sockets;
using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Correction;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Services;

// The manual steps of Task 13 run for real, with only the outside world faked: a scripted Telegram, a fake bar, the real HTTP
// listener on a free port, the real queue file in a temporary folder.
public sealed class EndToEndTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeTelegramApi _api = new();
    private readonly FakeDisplay _display = new();
    private readonly StatusHub _hub = new();
    private readonly HttpClient _http = new();
    private readonly int _port;
    private readonly List<IDisposable> _disposables = [];
    private AlertService? _alerts;
    private TelegramEngine? _engine;
    private AlertQueue? _queue;
    private QueueStore? _store;

    public EndToEndTests()
    {
        Directory.CreateDirectory(_dir);
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        _port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Leftover temp folder.
        }
    }

    private long Now() => _clock.GetUtcNow().ToUnixTimeSeconds();

    // Builds the app's parts the way AppHost does and starts them. Calling it again after Stop() is "restarting the app".
    private void StartApp()
    {
        var gemini = new FakeCorrector((_, _) => Task.FromResult<ParsedAlert?>(null));
        var offline = new FakeCorrector((raw, _) => Task.FromResult<ParsedAlert?>(AlertParser.Parse(raw)));
        var pipeline = new CorrectionPipeline(gemini, offline, () => FixedRules.Empty, TimeSpan.FromSeconds(3), _clock);
        _store = new QueueStore(Path.Combine(_dir, "queue.json"), _clock);
        _queue = AlertQueue.FromSnapshot(_store.Load(), _clock);
        var notifier = new TelegramGroupNotifier(_api.Client(), () => FakeTelegramApi.Chat, NullLogger.Instance, (_, _) => Task.CompletedTask);
        _alerts = new AlertService(_queue, pipeline, _display, notifier, () => new AppSettings(), _hub, NullLogger.Instance, _clock);
        var poller = new TelegramPoller(_api.Client(), () => FakeTelegramApi.Chat, null, 0, (_, ct) => Task.Delay(1, ct));
        _engine = new TelegramEngine(poller, _alerts, notifier, _hub, NullLogger.Instance, _clock);
        var server = new HttpControlServer(_alerts, _hub, NullLogger.Instance, _port, _clock);
        _store.Attach(_queue);
        _alerts.Start();
        _engine.Start();
        Assert.True(server.Start());
        _disposables.AddRange([notifier, server]);
    }

    private void Stop()
    {
        _engine?.Dispose();
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _disposables.Clear();
        _alerts?.Dispose();
        _store?.Dispose();
        _engine = null;
        _alerts = null;
        _store = null;
    }

    private async Task<string> Get(string path)
    {
        using var response = await _http.GetAsync($"http://127.0.0.1:{_port}{path}", TestContext.Current.CancellationToken);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Step_1_status_off_and_on_through_the_local_api()
    {
        StartApp();
        Assert.Equal("""{"success":true,"overlayEnabled":true}""", await Get("/overlay/status"));
        await _alerts!.SubmitAsync("gol branco aaa1111", AlertSource.Pc, SenderRole.Operator, false, null, TestContext.Current.CancellationToken);
        var pass = await _display.NextPass(1);

        Assert.Equal("""{"success":true,"overlayEnabled":false}""", await Get("/overlay/off"));

        Assert.True(pass.Cancelled); // the bar stops at once
        Assert.Empty(_queue!.Pending);
        Assert.Null(_queue.Current);
        await Wait.Until(() => _api.Sent.Length == 1, "the pause message");
        Assert.Equal((FakeTelegramApi.Chat, TelegramReplies.Paused), _api.Sent[0]);
        Assert.Equal("""{"success":true,"overlayEnabled":false}""", await Get("/overlay/status"));
        Assert.Equal("""{"success":true,"overlayEnabled":false}""", await Get("/overlay/off")); // again: no second message
        await Wait.Settle();
        Assert.Single(_api.Sent);

        Assert.Equal("""{"success":true,"overlayEnabled":true}""", await Get("/overlay/on"));
        Assert.Single(_api.Sent); // resuming is silent
        await _alerts.SubmitAsync("onix preto bbb2222", AlertSource.Pc, SenderRole.Operator, false, null, TestContext.Current.CancellationToken);
        Assert.Equal("ONIX PRETO | PLACA: BBB-2222", (await _display.NextPass(2)).Text);
    }

    [Fact]
    public async Task Step_2_the_telegram_group_gets_shown_missing_and_repeated_answers()
    {
        StartApp();
        _api.Updates(FakeTelegramApi.Message(1, "#corolla prata abc1d23 vidro aberto", Now()));

        var pass = await _display.NextPass(1);
        Assert.Equal("COROLLA PRATA | PLACA: ABC-1D23 | VIDRO ABERTO", pass.Text);
        pass.Complete(true);
        await Wait.Until(() => _api.Sent.Length == 1, "the shown reply");
        Assert.Equal(TelegramReplies.Shown("COROLLA PRATA | PLACA: ABC-1D23 | VIDRO ABERTO"), _api.Sent[0].Text);
        Assert.StartsWith("\U00002705 Mensagem exibida no tel\U000000E3o.", _api.Sent[0].Text);

        _api.Updates(FakeTelegramApi.Message(2, "#gol branco farol aceso", Now()));
        await Wait.Until(() => _api.Sent.Length == 2, "the missing-plate reply");
        Assert.Equal(TelegramReplies.Missing(["placa"]), _api.Sent[1].Text);
        Assert.StartsWith("\U0000274C Aviso n\U000000E3o foi para o tel\U000000E3o. Faltou placa.", _api.Sent[1].Text);

        _api.Updates(FakeTelegramApi.Message(3, "#corolla prata abc1d23 vidro aberto", Now()));
        await Wait.Until(() => _api.Sent.Length == 3, "the repeated reply");
        Assert.Equal(TelegramReplies.DuplicateShown(0), _api.Sent[2].Text); // shown moments ago: told as one minute
        _clock.Advance(TimeSpan.FromMinutes(3));
        _api.Updates(FakeTelegramApi.Message(4, "#corolla prata abc1d23", Now()));
        await Wait.Until(() => _api.Sent.Length == 4, "the second repeated reply");
        Assert.Equal(TelegramReplies.DuplicateShown(3), _api.Sent[3].Text);
        Assert.Single(_display.Texts);
    }

    [Fact]
    public async Task Step_3_the_same_plate_twice_in_a_row_is_repeated_while_the_first_is_still_waiting()
    {
        StartApp();
        _display.SetLed(false);
        _api.Updates(
            FakeTelegramApi.Message(1, "#corolla prata abc1d23", Now())
            + "," + FakeTelegramApi.Message(2, "#corolla prata abc-1d23", Now()));

        await Wait.Until(() => _api.Sent.Length == 2, "two replies");

        Assert.Equal(TelegramReplies.LedOff, _api.Sent[0].Text);
        Assert.Equal(TelegramReplies.DuplicateQueued, _api.Sent[1].Text);
        Assert.Single(_queue!.Pending);
    }

    [Fact]
    public async Task A_restart_in_the_middle_of_the_service_loses_nothing_and_the_sermon_pause_is_forgotten()
    {
        StartApp();
        _display.SetLed(false);
        await _alerts!.SubmitAsync("gol branco aaa1111", AlertSource.Pc, SenderRole.Operator, false, null, TestContext.Current.CancellationToken);
        await _alerts.SubmitAsync("onix preto bbb2222", AlertSource.Pc, SenderRole.Operator, true, null, TestContext.Current.CancellationToken);
        Stop();
        Assert.True(File.Exists(Path.Combine(_dir, "queue.json")));

        StartApp(); // "the app was closed and opened again"

        Assert.Equal(["ONIX PRETO | PLACA: BBB-2222", "GOL BRANCO | PLACA: AAA-1111"], _queue!.Pending.Select(p => p.Text));
        Assert.False(_alerts!.IsPaused);
        _display.SetLed(true);
        Assert.Equal("ONIX PRETO | PLACA: BBB-2222", (await _display.NextPass(1)).Text);
    }

    [Fact]
    public async Task A_pause_is_never_carried_over_to_the_next_run()
    {
        StartApp();
        _alerts!.Pause();
        Stop();

        StartApp();

        Assert.False(_alerts!.IsPaused);
        Assert.Equal("""{"success":true,"overlayEnabled":true}""", await Get("/overlay/status"));
        var outcome = await _alerts.SubmitAsync("gol branco aaa1111", AlertSource.Pc, SenderRole.Operator, false, null, TestContext.Current.CancellationToken);
        Assert.Equal(SubmitKind.Queued, outcome.Kind);
    }
}
