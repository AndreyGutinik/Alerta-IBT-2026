using System.Net;
using System.Text;
using AlertaIBT.Core.Telegram;

namespace AlertaIBT.Core.Tests.Telegram;

// deleteMessage, getChat (the pinned message) and the id that sendMessage returns, against a fake HTTP handler.
public sealed class TelegramClientDeleteTests
{
    private const string Token = "123456:ABC-def_GHI";

    private sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Uri, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.RequestUri!, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (TelegramClient Client, FakeHandler Handler) Make(Func<HttpRequestMessage, string, HttpResponseMessage> respond, string? token = Token)
    {
        var handler = new FakeHandler(respond);
        return (new TelegramClient(new HttpClient(handler), () => token), handler);
    }

    [Fact]
    public async Task Delete_posts_the_chat_and_the_message_to_deleteMessage()
    {
        var (client, handler) = Make((_, _) => Json("""{"ok":true,"result":true}"""));

        await client.DeleteMessageAsync(-100123, 77, TestContext.Current.CancellationToken);

        var call = Assert.Single(handler.Calls);
        Assert.Equal($"/bot{Token}/deleteMessage", call.Uri.AbsolutePath);
        using var body = System.Text.Json.JsonDocument.Parse(call.Body);
        Assert.Equal(-100123, body.RootElement.GetProperty("chat_id").GetInt64());
        Assert.Equal(77, body.RootElement.GetProperty("message_id").GetInt64());
    }

    [Theory]
    [InlineData(400, "Bad Request: message can be deleted only within 48 hours")]
    [InlineData(400, "Bad Request: message to delete not found")]
    [InlineData(403, "Forbidden: bot was kicked from the supergroup chat")]
    [InlineData(429, "Too Many Requests: retry after 5")]
    public async Task Delete_refused_by_telegram_is_a_TelegramException_with_the_code(int code, string description)
    {
        var (client, _) = Make((_, _) => Json(
            "{\"ok\":false,\"error_code\":" + code + ",\"description\":\"" + description + "\"}",
            (HttpStatusCode)code));

        var ex = await Assert.ThrowsAsync<TelegramException>(() => client.DeleteMessageAsync(-1, 1, TestContext.Current.CancellationToken));

        Assert.Equal(code, ex.StatusCode);
        Assert.Contains(description, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_network_error_is_a_TelegramException_without_a_code_and_without_the_token()
    {
        var (client, _) = Make((request, _) => throw new HttpRequestException($"No such host is known ({request.RequestUri})"));

        var ex = await Assert.ThrowsAsync<TelegramException>(() => client.DeleteMessageAsync(-1, 1, TestContext.Current.CancellationToken));

        Assert.Null(ex.StatusCode);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("123456", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_that_echoes_the_token_does_not_leak_it()
    {
        var (client, _) = Make((_, _) => Json(
            $"{{\"ok\":false,\"error_code\":401,\"description\":\"Unauthorized {Token} https://api.telegram.org/bot{Token}/deleteMessage\"}}",
            HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<TelegramException>(() => client.DeleteMessageAsync(-1, 1, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ABC-def_GHI", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_without_a_token_does_not_call_telegram()
    {
        var (client, handler) = Make((_, _) => Json("""{"ok":true,"result":true}"""), token: null);

        await Assert.ThrowsAsync<TelegramException>(() => client.DeleteMessageAsync(-1, 1, TestContext.Current.CancellationToken));

        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Delete_cancelled_by_the_caller_throws_OperationCanceled_not_TelegramException()
    {
        var (client, _) = Make((_, _) => Json("""{"ok":true,"result":true}"""));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DeleteMessageAsync(-1, 1, cts.Token));
    }

    [Fact]
    public async Task The_pinned_message_id_is_read_from_getChat()
    {
        var (client, handler) = Make((_, _) => Json("""{"ok":true,"result":{"id":-100,"type":"supergroup","pinned_message":{"message_id":55,"date":1}}}"""));

        var pinned = await client.GetPinnedMessageIdAsync(-100, TestContext.Current.CancellationToken);

        Assert.Equal(55, pinned);
        Assert.Equal($"/bot{Token}/getChat", Assert.Single(handler.Calls).Uri.AbsolutePath);
    }

    [Fact]
    public async Task No_pinned_message_is_null()
    {
        var (client, _) = Make((_, _) => Json("""{"ok":true,"result":{"id":-100,"type":"supergroup"}}"""));

        Assert.Null(await client.GetPinnedMessageIdAsync(-100, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Send_returns_the_id_of_the_new_message()
    {
        var (client, _) = Make((_, _) => Json("""{"ok":true,"result":{"message_id":321,"date":1}}"""));

        Assert.Equal(321, await client.SendMessageAsync(-100, "oi", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Send_returns_null_when_the_answer_has_no_id()
    {
        var (client, _) = Make((_, _) => Json("""{"ok":true,"result":true}"""));

        Assert.Null(await client.SendMessageAsync(-100, "oi", TestContext.Current.CancellationToken));
    }
}
