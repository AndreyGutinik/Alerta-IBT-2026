using System.Net;
using System.Text;
using System.Text.Json;
using AlertaIBT.Core.Telegram;

namespace AlertaIBT.Core.Tests.Telegram;

public class TelegramClientTests
{
    private const string Token = "123456:ABC-def_GHI";

    private sealed record Call(HttpMethod Method, Uri Uri, string Body);

    private sealed class FakeHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Calls.Add(new Call(request.Method, request.RequestUri!, body));
            return await respond(ct);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (TelegramClient Client, FakeHandler Handler) Make(
        Func<CancellationToken, Task<HttpResponseMessage>> respond,
        string? token = Token,
        TimeSpan? slack = null)
    {
        var handler = new FakeHandler(respond);
        return (new TelegramClient(new HttpClient(handler), () => token, slack), handler);
    }

    private static (TelegramClient Client, FakeHandler Handler) Answering(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        Make(_ => Task.FromResult(Json(json, status)));

    private const string TwoUpdates = """
        {"ok":true,"result":[
          {"update_id":10,"message":{"message_id":1,"from":{"id":5,"is_bot":false,"first_name":"Ana"},"chat":{"id":-100,"type":"supergroup","title":"G"},"date":1700000000,"text":"#gol branco"}},
          {"update_id":11,"edited_message":{"message_id":2}}
        ]}
        """;

    [Fact]
    public async Task GetUpdates_asks_for_messages_only_with_offset_and_long_poll_timeout()
    {
        var (client, handler) = Answering("""{"ok":true,"result":[]}""");

        await client.GetUpdatesAsync(42, 25, CancellationToken.None);

        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Get, call.Method);
        Assert.Equal("api.telegram.org", call.Uri.Host);
        Assert.Equal("https", call.Uri.Scheme);
        Assert.Equal($"/bot{Token}/getUpdates", call.Uri.AbsolutePath);
        var query = System.Web.HttpUtility.ParseQueryString(call.Uri.Query);
        Assert.Equal("42", query["offset"]);
        Assert.Equal("25", query["timeout"]);
        Assert.Equal("[\"message\"]", query["allowed_updates"]);
    }

    [Fact]
    public async Task GetUpdates_leaves_the_offset_out_when_there_is_none_yet()
    {
        var (client, handler) = Answering("""{"ok":true,"result":[]}""");

        await client.GetUpdatesAsync(0, 25, CancellationToken.None);

        var query = System.Web.HttpUtility.ParseQueryString(handler.Calls[0].Uri.Query);
        Assert.Null(query["offset"]);
    }

    [Fact]
    public async Task GetUpdates_reads_updates_and_keeps_those_that_are_not_messages()
    {
        var (client, _) = Answering(TwoUpdates);

        var updates = await client.GetUpdatesAsync(0, 25, CancellationToken.None);

        Assert.Equal(2, updates.Count);
        Assert.Equal(10, updates[0].UpdateId);
        Assert.Equal("#gol branco", updates[0].Message!.Text);
        Assert.Equal(-100, updates[0].Message!.Chat.Id);
        Assert.Equal(11, updates[1].UpdateId);
        Assert.Null(updates[1].Message);
    }

    [Fact]
    public async Task GetUpdates_reads_the_token_on_every_call()
    {
        string? token = Token;
        var handler = new FakeHandler(_ => Task.FromResult(Json("""{"ok":true,"result":[]}""")));
        var client = new TelegramClient(new HttpClient(handler), () => token);

        await client.GetUpdatesAsync(0, 0, CancellationToken.None);
        token = "999:XYZ";
        await client.GetUpdatesAsync(0, 0, CancellationToken.None);

        Assert.Equal($"/bot{Token}/getUpdates", handler.Calls[0].Uri.AbsolutePath);
        Assert.Equal("/bot999:XYZ/getUpdates", handler.Calls[1].Uri.AbsolutePath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Without_a_token_nothing_is_sent(string? token)
    {
        var (client, handler) = Answering("""{"ok":true,"result":[]}""", HttpStatusCode.OK);
        var unconfigured = new TelegramClient(new HttpClient(handler), () => token);

        Assert.False(unconfigured.IsConfigured);
        Assert.True(client.IsConfigured);
        await Assert.ThrowsAsync<TelegramException>(() => unconfigured.GetUpdatesAsync(0, 0, CancellationToken.None));
        await Assert.ThrowsAsync<TelegramException>(() => unconfigured.SendMessageAsync(1, "oi", CancellationToken.None));
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("123:abc/../x")]
    [InlineData("123:abc?x=1")]
    [InlineData("12 3:abc")]
    public async Task A_token_that_could_change_the_url_is_refused_without_a_request_and_without_being_echoed(string token)
    {
        var (client, handler) = Make(_ => Task.FromResult(Json("{}")), token);

        var ex = await Assert.ThrowsAsync<TelegramException>(() => client.GetUpdatesAsync(0, 0, CancellationToken.None));

        Assert.Empty(handler.Calls);
        Assert.DoesNotContain(token, ex.Message);
    }

    [Fact]
    public async Task Http_409_is_a_conflict()
    {
        var (client, _) = Answering(
            """{"ok":false,"error_code":409,"description":"Conflict: terminated by other getUpdates request"}""",
            HttpStatusCode.Conflict);

        var ex = await Assert.ThrowsAsync<TelegramException>(() => client.GetUpdatesAsync(0, 25, CancellationToken.None));

        Assert.True(ex.IsConflict);
        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("terminated by other getUpdates", ex.Message);
    }

    [Fact]
    public async Task A_409_inside_a_200_body_is_also_a_conflict()
    {
        var (client, _) = Answering("""{"ok":false,"error_code":409,"description":"Conflict"}""");

        var ex = await Assert.ThrowsAsync<TelegramException>(() => client.GetUpdatesAsync(0, 25, CancellationToken.None));

        Assert.True(ex.IsConflict);
    }

    [Fact]
    public async Task Other_http_errors_are_not_a_conflict_and_carry_the_status()
    {
        var (client, _) = Answering("""{"ok":false,"error_code":401,"description":"Unauthorized"}""", HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<TelegramException>(() => client.GetUpdatesAsync(0, 25, CancellationToken.None));

        Assert.False(ex.IsConflict);
        Assert.Equal(401, ex.StatusCode);
        Assert.Contains("Unauthorized", ex.Message);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("""{"ok":true}""")]
    [InlineData("""{"ok":false,"description":"Bad Request"}""")]
    public async Task A_body_that_is_not_a_good_answer_is_an_error(string body)
    {
        var (client, _) = Answering(body);

        await Assert.ThrowsAsync<TelegramException>(() => client.GetUpdatesAsync(0, 25, CancellationToken.None));
    }

    [Fact]
    public async Task A_network_failure_is_an_error_that_never_shows_the_token()
    {
        var (client, _) = Make(_ => throw new HttpRequestException($"No such host https://api.telegram.org/bot{Token}/getUpdates"));

        var ex = await Assert.ThrowsAsync<TelegramException>(() => client.GetUpdatesAsync(0, 25, CancellationToken.None));

        Assert.DoesNotContain(Token, ex.Message);
        Assert.DoesNotContain(Token, ex.ToString());
        Assert.Null(ex.StatusCode);
    }

    [Fact]
    public async Task An_error_body_that_repeats_the_token_never_shows_it()
    {
        var (client, _) = Answering($$"""{"ok":false,"error_code":404,"description":"bot{{Token}} not found"}""", HttpStatusCode.NotFound);

        var ex = await Assert.ThrowsAsync<TelegramException>(() => client.GetUpdatesAsync(0, 25, CancellationToken.None));

        Assert.DoesNotContain(Token, ex.Message);
    }

    [Fact]
    public async Task A_request_that_hangs_past_its_deadline_is_an_error_not_a_cancellation()
    {
        var (client, _) = Make(
            async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return Json("{}");
            },
            slack: TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<TelegramException>(() => client.GetUpdatesAsync(0, 0, CancellationToken.None));

        Assert.Null(ex.StatusCode);
    }

    [Fact]
    public async Task Cancelling_the_caller_token_throws_OperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        var (client, _) = Make(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Json("{}");
        });

        var pending = client.GetUpdatesAsync(0, 25, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task SendMessage_posts_json_without_link_previews()
    {
        var (client, handler) = Answering("""{"ok":true,"result":{"message_id":7}}""");

        await client.SendMessageAsync(-100, "linha 1\nlinha 2 \U00002705", CancellationToken.None);

        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal($"/bot{Token}/sendMessage", call.Uri.AbsolutePath);
        using var body = JsonDocument.Parse(call.Body);
        Assert.Equal(-100, body.RootElement.GetProperty("chat_id").GetInt64());
        Assert.Equal("linha 1\nlinha 2 \U00002705", body.RootElement.GetProperty("text").GetString());
        Assert.True(body.RootElement.GetProperty("link_preview_options").GetProperty("is_disabled").GetBoolean());
    }

    [Fact]
    public async Task SendMessage_cuts_a_text_longer_than_telegram_accepts()
    {
        var (client, handler) = Answering("""{"ok":true,"result":{}}""");

        await client.SendMessageAsync(1, new string('a', 5000), CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Calls[0].Body);
        Assert.Equal(4096, body.RootElement.GetProperty("text").GetString()!.Length);
    }

    [Fact]
    public async Task SendMessage_refuses_blank_text_without_a_request()
    {
        var (client, handler) = Answering("""{"ok":true,"result":{}}""");

        await Assert.ThrowsAsync<ArgumentException>(() => client.SendMessageAsync(1, "  ", CancellationToken.None));

        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task SendMessage_failure_is_an_error()
    {
        var (client, _) = Answering("""{"ok":false,"error_code":400,"description":"Bad Request: chat not found"}""", HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<TelegramException>(() => client.SendMessageAsync(1, "oi", CancellationToken.None));

        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("chat not found", ex.Message);
    }
}
