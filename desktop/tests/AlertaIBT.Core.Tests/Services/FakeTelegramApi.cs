using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using AlertaIBT.Core.Telegram;

namespace AlertaIBT.Core.Tests.Services;

/// <summary>A scripted Telegram Bot API: getUpdates answers come from a script (then the call hangs like an empty long poll), sendMessage is recorded.</summary>
internal sealed class FakeTelegramApi : HttpMessageHandler
{
    public const string Token = "123456:ABC-def_GHI";
    public const long Chat = -1001234567890;

    private readonly ConcurrentQueue<Func<HttpResponseMessage>> _updates = new();
    private readonly ConcurrentQueue<(long Chat, string Text)> _sent = new();
    private int _failSends;
    private int _sendAttempts;
    private int _pollCount;

    public (long Chat, string Text)[] Sent => _sent.ToArray();

    public int SendAttempts => Volatile.Read(ref _sendAttempts);

    public int Polls => Volatile.Read(ref _pollCount);

    public TelegramClient Client() => new(new HttpClient(this), () => Token);

    public FakeTelegramApi Updates(string updatesJson)
    {
        _updates.Enqueue(() => Json($$"""{"ok":true,"result":[{{updatesJson}}]}"""));
        return this;
    }

    public FakeTelegramApi Conflict()
    {
        _updates.Enqueue(() => Json("""{"ok":false,"error_code":409,"description":"Conflict: terminated by other getUpdates request"}""", HttpStatusCode.Conflict));
        return this;
    }

    public FakeTelegramApi FailNextSends(int count)
    {
        Volatile.Write(ref _failSends, count);
        return this;
    }

    public static string Message(long updateId, string text, long date, long chat = Chat, long from = 5) =>
        $$$"""{"update_id":{{{updateId}}},"message":{"message_id":{{{updateId}}},"from":{"id":{{{from}}},"is_bot":false,"first_name":"Ana"},"chat":{"id":{{{chat}}},"type":"supergroup"},"date":{{{date}}},"text":{{{JsonSerializer.Serialize(text)}}}}}""";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/sendMessage", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _sendAttempts);
            if (Interlocked.Decrement(ref _failSends) >= 0)
            {
                return Json("""{"ok":false,"error_code":502,"description":"Bad Gateway"}""", HttpStatusCode.BadGateway);
            }

            Interlocked.Exchange(ref _failSends, 0);
            var body = await request.Content!.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(body);
            _sent.Enqueue((document.RootElement.GetProperty("chat_id").GetInt64(), document.RootElement.GetProperty("text").GetString()!));
            return Json("""{"ok":true,"result":{"message_id":1}}""");
        }

        Interlocked.Increment(ref _pollCount);

        // An empty long poll: it stays open, but answers as soon as the test scripts something new.
        while (true)
        {
            if (_updates.TryDequeue(out var answer))
            {
                return answer();
            }

            await Task.Delay(5, ct);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
