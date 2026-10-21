using System.Net;
using System.Text;
using AlertaIBT.Core.Telegram;

namespace AlertaIBT.Core.Tests.Telegram;

public class TelegramPollerTests
{
    private const long Chat = -1001234567890;
    private const string Token = "123456:ABC-def_GHI";

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // A scripted Telegram: each request takes the next scripted answer; when the script is empty the request hangs like
    // a long poll that finds nothing, and the harness knows the poller has consumed everything.
    private sealed class Harness
    {
        private readonly Queue<Func<HttpResponseMessage>> _script = new();
        private readonly TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Harness(long initialOffset = 0)
        {
            Handler = new ScriptedHandler(this);
            Client = new TelegramClient(new HttpClient(Handler), () => TokenValue);
            Poller = new TelegramPoller(
                Client,
                () => ChatId,
                offset => Saved.Add(offset),
                initialOffset,
                async (delay, ct) =>
                {
                    Delays.Add(delay);
                    await Task.Delay(1, ct);
                });
            Poller.MessageReceived += (text, message) =>
            {
                if (OnMessage is not null)
                {
                    OnMessage(text, message);
                }

                Received.Add((text, message));
            };
            Poller.StateChanged += state => States.Add(state);
        }

        public string? TokenValue { get; set; } = Token;

        public long ChatId { get; set; } = Chat;

        public Action<string, TgMessage>? OnMessage { get; set; }

        public ScriptedHandler Handler { get; }

        public TelegramClient Client { get; }

        public TelegramPoller Poller { get; }

        public List<(string Text, TgMessage Message)> Received { get; } = [];

        public List<long> Saved { get; } = [];

        public List<TimeSpan> Delays { get; } = [];

        public List<TelegramState> States { get; } = [];

        public List<Uri> Requests { get; } = [];

        public Harness Then(Func<HttpResponseMessage> answer)
        {
            _script.Enqueue(answer);
            return this;
        }

        public Harness ThenUpdates(string updatesJson) =>
            Then(() => Json($$"""{"ok":true,"result":[{{updatesJson}}]}"""));

        public Harness ThenEmpty() => ThenUpdates(string.Empty);

        public Harness ThenFailure(HttpStatusCode status = HttpStatusCode.BadGateway) =>
            Then(() => Json($$"""{"ok":false,"error_code":{{(int)status}},"description":"boom"}""", status));

        public Harness ThenConflict() => ThenFailure(HttpStatusCode.Conflict);

        // Runs the poller until the whole script has been consumed, then stops it.
        public async Task RunToTheEndOfTheScript()
        {
            using var cts = new CancellationTokenSource();
            var run = Poller.RunAsync(cts.Token);
            var finished = await Task.WhenAny(_idle.Task, run, Task.Delay(Wait, TestContext.Current.CancellationToken));
            Assert.True(finished == _idle.Task, "the poller did not reach the end of the script");
            await cts.CancelAsync();
            await run.WaitAsync(Wait, TestContext.Current.CancellationToken);
        }

        public sealed class ScriptedHandler(Harness owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                owner.Requests.Add(request.RequestUri!);
                if (owner._script.TryDequeue(out var answer))
                {
                    return answer();
                }

                owner._idle.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            }
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string MessageUpdate(long updateId, long chatId, string text, bool isBot = false, long messageId = 1) =>
        $$$"""{"update_id":{{{updateId}}},"message":{"message_id":{{{messageId}}},"from":{"id":5,"is_bot":{{{(isBot ? "true" : "false")}}},"first_name":"Ana"},"chat":{"id":{{{chatId}}},"type":"supergroup"},"date":1700000000,"text":"{{{text}}}"}}""";

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "the condition was not met in time");
    }

    private static string QueryOf(Uri uri, string name) => System.Web.HttpUtility.ParseQueryString(uri.Query)[name] ?? string.Empty;

    [Fact]
    public async Task Two_updates_raise_two_events_and_the_offset_is_the_last_id_plus_one()
    {
        var h = new Harness().ThenUpdates(MessageUpdate(10, Chat, "#gol branco") + "," + MessageUpdate(11, Chat, "#onix preto"));

        await h.RunToTheEndOfTheScript();

        Assert.Equal(["gol branco", "onix preto"], h.Received.Select(r => r.Text));
        Assert.Equal(12, h.Poller.Offset);
        Assert.Equal([11L, 12L], h.Saved);
        Assert.Equal(TelegramState.Online, h.Poller.State);
    }

    [Fact]
    public async Task The_next_poll_asks_from_the_new_offset_with_a_25_second_long_poll()
    {
        var h = new Harness().ThenUpdates(MessageUpdate(10, Chat, "#gol branco")).ThenEmpty();

        await h.RunToTheEndOfTheScript();

        Assert.Equal(3, h.Requests.Count);
        Assert.Equal(string.Empty, QueryOf(h.Requests[0], "offset"));
        Assert.Equal("25", QueryOf(h.Requests[0], "timeout"));
        Assert.Equal("11", QueryOf(h.Requests[1], "offset"));
        Assert.Equal("11", QueryOf(h.Requests[2], "offset"));
    }

    [Fact]
    public async Task A_saved_offset_is_where_the_poller_resumes()
    {
        var h = new Harness(500).ThenEmpty();

        await h.RunToTheEndOfTheScript();

        Assert.Equal("500", QueryOf(h.Requests[0], "offset"));
        Assert.Equal(500, h.Poller.Offset);
        Assert.Empty(h.Saved);
    }

    [Fact]
    public async Task A_message_from_another_chat_is_ignored_but_moves_the_offset()
    {
        var h = new Harness().ThenUpdates(MessageUpdate(20, 777, "#gol branco"));

        await h.RunToTheEndOfTheScript();

        Assert.Empty(h.Received);
        Assert.Equal(21, h.Poller.Offset);
        Assert.Equal([21L], h.Saved);
    }

    [Fact]
    public async Task Messages_that_are_not_alerts_move_the_offset_without_an_event()
    {
        var h = new Harness().ThenUpdates(string.Join(
            ",",
            MessageUpdate(1, Chat, "bom dia"),
            MessageUpdate(2, Chat, "#oi", isBot: true),
            MessageUpdate(3, Chat, "#"),
            """{"update_id":4,"edited_message":{"message_id":9}}""",
            MessageUpdate(5, Chat, "#corolla prata")));

        await h.RunToTheEndOfTheScript();

        Assert.Equal(["corolla prata"], h.Received.Select(r => r.Text));
        Assert.Equal(6, h.Poller.Offset);
    }

    [Fact]
    public async Task The_event_carries_the_original_message()
    {
        var h = new Harness().ThenUpdates(MessageUpdate(7, Chat, "#gol branco", messageId: 99));

        await h.RunToTheEndOfTheScript();

        var (text, message) = Assert.Single(h.Received);
        Assert.Equal("gol branco", text);
        Assert.Equal(99, message.MessageId);
        Assert.Equal(Chat, message.Chat.Id);
    }

    [Fact]
    public async Task An_update_older_than_the_offset_is_not_processed_again()
    {
        var h = new Harness(50).ThenUpdates(MessageUpdate(49, Chat, "#gol branco") + "," + MessageUpdate(50, Chat, "#onix preto"));

        await h.RunToTheEndOfTheScript();

        Assert.Equal(["onix preto"], h.Received.Select(r => r.Text));
        Assert.Equal(51, h.Poller.Offset);
    }

    [Fact]
    public async Task Http_409_is_a_conflict_and_nothing_is_processed()
    {
        var h = new Harness().ThenConflict();

        await h.RunToTheEndOfTheScript();

        Assert.Equal(TelegramState.Conflict, h.Poller.State);
        Assert.Empty(h.Received);
        Assert.Equal(0, h.Poller.Offset);
        Assert.NotNull(h.Poller.LastError);
        Assert.Contains("409", h.Poller.LastError);
    }

    [Fact]
    public async Task A_conflict_that_keeps_happening_stays_a_conflict_and_waits_between_tries()
    {
        var h = new Harness().ThenConflict().ThenConflict().ThenConflict().ThenConflict();

        await h.RunToTheEndOfTheScript();

        Assert.Equal(TelegramState.Conflict, h.Poller.State);
        Assert.Equal(4, h.Delays.Count);
        Assert.All(h.Delays, d => Assert.True(d >= TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task When_the_other_consumer_goes_away_the_poller_goes_online_and_processes()
    {
        var h = new Harness().ThenConflict().ThenUpdates(MessageUpdate(5, Chat, "#gol branco"));

        await h.RunToTheEndOfTheScript();

        Assert.Equal(TelegramState.Online, h.Poller.State);
        Assert.Equal(["gol branco"], h.Received.Select(r => r.Text));
        Assert.Null(h.Poller.LastError);
        Assert.Equal([TelegramState.Connecting, TelegramState.Conflict, TelegramState.Online], h.States);
    }

    [Fact]
    public async Task Three_failures_in_a_row_make_it_offline_and_the_first_two_are_reconnecting()
    {
        var h = new Harness().ThenFailure();
        await h.RunToTheEndOfTheScript();
        Assert.Equal(TelegramState.Reconnecting, h.Poller.State);

        var h2 = new Harness().ThenFailure().ThenFailure();
        await h2.RunToTheEndOfTheScript();
        Assert.Equal(TelegramState.Reconnecting, h2.Poller.State);

        var h3 = new Harness().ThenFailure().ThenFailure().ThenFailure();
        await h3.RunToTheEndOfTheScript();
        Assert.Equal(TelegramState.Offline, h3.Poller.State);
        Assert.Equal(
            [TelegramState.Connecting, TelegramState.Reconnecting, TelegramState.Offline],
            h3.States);
    }

    [Fact]
    public async Task A_success_after_failures_goes_back_online_and_clears_the_error()
    {
        var h = new Harness().ThenFailure().ThenFailure().ThenFailure().ThenEmpty();

        await h.RunToTheEndOfTheScript();

        Assert.Equal(TelegramState.Online, h.Poller.State);
        Assert.Null(h.Poller.LastError);
        Assert.Equal(
            [TelegramState.Connecting, TelegramState.Reconnecting, TelegramState.Offline, TelegramState.Online],
            h.States);
    }

    [Fact]
    public async Task The_wait_grows_from_one_second_to_thirty_and_starts_over_after_a_success()
    {
        var h = new Harness();
        for (var i = 0; i < 8; i++)
        {
            h.ThenFailure();
        }

        h.ThenEmpty().ThenFailure();

        await h.RunToTheEndOfTheScript();

        Assert.Equal(
            new[] { 1, 2, 4, 8, 16, 30, 30, 30, 1 }.Select(seconds => TimeSpan.FromSeconds(seconds)),
            h.Delays);
    }

    [Fact]
    public async Task A_network_error_counts_as_a_failure_and_never_shows_the_token()
    {
        var h = new Harness().Then(() => throw new HttpRequestException($"No such host: https://api.telegram.org/bot{Token}/getUpdates"));

        await h.RunToTheEndOfTheScript();

        Assert.Equal(TelegramState.Reconnecting, h.Poller.State);
        Assert.DoesNotContain(Token, h.Poller.LastError);
    }

    [Fact]
    public async Task Without_a_token_it_is_not_configured_and_asks_nothing()
    {
        var h = new Harness { TokenValue = null };

        using var cts = new CancellationTokenSource();
        var run = h.Poller.RunAsync(cts.Token);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await run.WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Equal(TelegramState.NotConfigured, h.Poller.State);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Without_a_chat_id_it_is_not_configured_and_asks_nothing()
    {
        var h = new Harness { ChatId = 0 };

        using var cts = new CancellationTokenSource();
        var run = h.Poller.RunAsync(cts.Token);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await run.WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Equal(TelegramState.NotConfigured, h.Poller.State);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Configuring_it_while_running_starts_the_polling()
    {
        var h = new Harness { TokenValue = null };
        h.ThenUpdates(MessageUpdate(3, Chat, "#gol branco"));
        // The recheck delay is where the "user" fills the token in.
        var poller = new TelegramPoller(
            h.Client,
            () => h.ChatId,
            delay: (_, _) =>
            {
                h.TokenValue = Token;
                return Task.CompletedTask;
            });
        var received = new List<string>();
        poller.MessageReceived += (text, _) => received.Add(text);

        using var cts = new CancellationTokenSource();
        var run = poller.RunAsync(cts.Token);
        await WaitUntil(() => received.Count > 0);
        await cts.CancelAsync();
        await run.WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Equal(["gol branco"], received);
        Assert.Equal(TelegramState.Online, poller.State);
    }

    [Fact]
    public async Task Changing_the_token_starts_again_from_zero()
    {
        var h = new Harness();
        h.ThenUpdates(MessageUpdate(900, Chat, "#gol branco"))
            .Then(() =>
            {
                h.TokenValue = "999:NEW";
                return Json("""{"ok":true,"result":[]}""");
            })
            .ThenEmpty();

        await h.RunToTheEndOfTheScript();

        Assert.Equal(string.Empty, QueryOf(h.Requests[^1], "offset"));
        Assert.Equal(0, h.Poller.Offset);
        Assert.Equal(0, h.Saved[^1]);
        Assert.Contains("/bot999:NEW/", h.Requests[^1].AbsolutePath);
    }

    [Fact]
    public async Task A_handler_that_throws_does_not_stop_the_polling_and_the_update_is_not_repeated()
    {
        var h = new Harness { OnMessage = (text, _) => { if (text == "gol branco") { throw new InvalidOperationException("bug"); } } };
        h.ThenUpdates(MessageUpdate(1, Chat, "#gol branco") + "," + MessageUpdate(2, Chat, "#onix preto")).ThenEmpty();

        await h.RunToTheEndOfTheScript();

        Assert.Equal(["onix preto"], h.Received.Select(r => r.Text));
        Assert.Equal(3, h.Poller.Offset);
        Assert.Equal(TelegramState.Online, h.Poller.State);
        Assert.Equal("3", QueryOf(h.Requests[1], "offset"));
    }

    [Fact]
    public async Task A_failing_offset_save_does_not_stop_the_polling()
    {
        var handler = new Harness().ThenUpdates(MessageUpdate(1, Chat, "#gol branco")).ThenEmpty();
        var poller = new TelegramPoller(handler.Client, () => Chat, _ => throw new IOException("disk full"));
        var received = new List<string>();
        poller.MessageReceived += (text, _) => received.Add(text);

        using var cts = new CancellationTokenSource();
        var run = poller.RunAsync(cts.Token);
        await WaitUntil(() => handler.Requests.Count >= 2);
        await cts.CancelAsync();
        await run.WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Equal(["gol branco"], received);
        Assert.Equal(2, poller.Offset);
    }

    [Fact]
    public async Task Cancelling_ends_the_run_quietly_in_the_middle_of_a_poll()
    {
        var h = new Harness();

        using var cts = new CancellationTokenSource();
        var run = h.Poller.RunAsync(cts.Token);
        await WaitUntil(() => h.Requests.Count > 0);
        await cts.CancelAsync();
        await run.WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.True(run.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Cancelling_while_waiting_to_retry_ends_the_run_quietly()
    {
        using var cts = new CancellationTokenSource();
        var h = new Harness().ThenFailure();
        var poller = new TelegramPoller(
            h.Client,
            () => Chat,
            delay: async (d, ct) => await Task.Delay(Timeout.Infinite, ct));

        var run = poller.RunAsync(cts.Token);
        await WaitUntil(() => h.Requests.Count >= 1);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await run.WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.True(run.IsCompletedSuccessfully);
    }

    [Fact]
    public void A_poller_starts_not_configured_until_it_runs()
    {
        var h = new Harness();

        Assert.Equal(TelegramState.NotConfigured, h.Poller.State);
        Assert.Null(h.Poller.LastError);
    }
}
