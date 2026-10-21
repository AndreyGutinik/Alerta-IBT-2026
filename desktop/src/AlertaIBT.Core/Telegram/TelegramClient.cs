using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AlertaIBT.Core.Telegram;

/// <summary>
/// The two Telegram Bot API calls the app needs: <c>getUpdates</c> (long polling) and <c>sendMessage</c>. The token
/// travels in the URL path, as Telegram requires, so it is checked for shape before use and scrubbed from every error
/// message: nothing thrown from here ever contains it.
/// </summary>
/// <remarks>
/// Every failure except the caller's own cancellation is a <see cref="TelegramException"/>. The client times each call
/// out by itself (the long-poll time plus a slack), so the <see cref="HttpClient"/> it gets must not have a shorter
/// <see cref="HttpClient.Timeout"/> than that: give it its own instance, not the one used for Gemini.
/// </remarks>
public sealed partial class TelegramClient
{
    private const string ApiRoot = "https://api.telegram.org/bot";
    private const int MaxTextLength = 4096;
    private const int UpdatesPerPoll = 100;

    private static readonly TimeSpan DefaultSlack = TimeSpan.FromSeconds(12);

    private readonly HttpClient _http;
    private readonly Func<string?> _token;
    private readonly TimeSpan _slack;

    /// <param name="http">Sends the requests.</param>
    /// <param name="token">Gives the bot token, read on every call; <c>null</c> or blank means there is none.</param>
    /// <param name="requestSlack">
    /// How long a call may take beyond the long-poll wait before it is given up as a timeout; 12 s by default (the
    /// limit of the old app).
    /// </param>
    public TelegramClient(HttpClient http, Func<string?> token, TimeSpan? requestSlack = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(token);

        _http = http;
        _token = token;
        _slack = requestSlack ?? DefaultSlack;
    }

    /// <summary>Whether there is a token to use (its shape is only checked when a call is made).</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_token());

    /// <summary>The current token, trimmed, or <c>null</c>. Internal: only the poller compares it, to notice a swap.</summary>
    internal string? ConfiguredToken
    {
        get
        {
            var token = _token()?.Trim();
            return string.IsNullOrEmpty(token) ? null : token;
        }
    }

    /// <summary>
    /// Asks Telegram for the messages after <paramref name="offset"/>, waiting up to <paramref name="timeoutSeconds"/>
    /// for one to arrive (long polling). Only <c>message</c> updates are requested.
    /// </summary>
    /// <param name="offset">The id of the first update wanted (the last id seen plus one); 0 or less asks from the start.</param>
    /// <param name="timeoutSeconds">The long-poll wait in seconds; 0 returns at once.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The updates, oldest first; empty when none arrived.</returns>
    /// <exception cref="TelegramException">No token, no internet, a timeout, an HTTP error or an unreadable answer.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled <paramref name="ct"/>.</exception>
    public async Task<IReadOnlyList<TgUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timeoutSeconds);

        var token = RequireToken();
        var url = new StringBuilder(ApiRoot).Append(token).Append("/getUpdates?timeout=")
            .Append(timeoutSeconds.ToString(CultureInfo.InvariantCulture))
            .Append("&limit=").Append(UpdatesPerPoll.ToString(CultureInfo.InvariantCulture))
            .Append("&allowed_updates=").Append(Uri.EscapeDataString("[\"message\"]"));
        if (offset > 0)
        {
            url.Append("&offset=").Append(offset.ToString(CultureInfo.InvariantCulture));
        }

        var result = await SendAsync<List<TgUpdate>>(
            token,
            () => new HttpRequestMessage(HttpMethod.Get, url.ToString()),
            TimeSpan.FromSeconds(timeoutSeconds) + _slack,
            ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>Sends <paramref name="text"/> to <paramref name="chatId"/>, without link previews. Text beyond 4096 characters is cut.</summary>
    /// <param name="chatId">The chat to write to.</param>
    /// <param name="text">The text; blank is refused.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <exception cref="ArgumentException"><paramref name="text"/> is blank.</exception>
    /// <exception cref="TelegramException">No token, no internet, a timeout, an HTTP error or an unreadable answer.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled <paramref name="ct"/>.</exception>
    public async Task SendMessageAsync(long chatId, string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("The text cannot be blank.", nameof(text));
        }

        var token = RequireToken();
        if (text.Length > MaxTextLength)
        {
            // Never split a surrogate pair in two.
            var length = char.IsHighSurrogate(text[MaxTextLength - 1]) ? MaxTextLength - 1 : MaxTextLength;
            text = text[..length];
        }

        var body = new JsonObject
        {
            ["chat_id"] = chatId,
            ["text"] = text,
            ["link_preview_options"] = new JsonObject { ["is_disabled"] = true },
        }.ToJsonString();

        await SendAsync<JsonNode>(
            token,
            () => new HttpRequestMessage(HttpMethod.Post, ApiRoot + token + "/sendMessage")
            {
                Content = new StringContent(body, Encoding.UTF8, MediaTypeHeaderValue.Parse("application/json")),
            },
            _slack,
            ct).ConfigureAwait(false);
    }

    private string RequireToken()
    {
        var token = ConfiguredToken;
        if (token is null)
        {
            throw new TelegramException("Token do Telegram n\U000000E3o configurado.");
        }

        // The token is part of the URL path: anything that is not the "digits:letters" shape could change the URL.
        if (!TokenShape().IsMatch(token))
        {
            throw new TelegramException("O token do Telegram parece inv\U000000E1lido (o formato correto \U000000E9 123456:ABC-def).");
        }

        return token;
    }

    // Sends one request and returns the "result" of the answer. Everything that can go wrong becomes a
    // TelegramException, except the caller's own cancellation.
    private async Task<T> SendAsync<T>(string token, Func<HttpRequestMessage> build, TimeSpan timeout, CancellationToken ct)
        where T : class
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            using var request = build();
            using var response = await _http.SendAsync(request, deadline.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            return ReadAnswer<T>((int)response.StatusCode, response.IsSuccessStatusCode, body, token);
        }
        catch (TelegramException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // The deadline above, or the HttpClient's own Timeout.
            throw new TelegramException("Tempo limite ao falar com o Telegram.");
        }
        catch (Exception ex)
        {
            throw new TelegramException("Sem conex\U000000E3o com o Telegram: " + Scrub(ex.Message, token));
        }
    }

    private static T ReadAnswer<T>(int status, bool httpOk, string body, string token)
        where T : class
    {
        TgResponse<T>? envelope = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                envelope = JsonSerializer.Deserialize<TgResponse<T>>(body);
            }
            catch (JsonException)
            {
                // Not the JSON Telegram sends (a proxy's HTML page, say): handled below.
            }
        }

        if (!httpOk || envelope is { Ok: false })
        {
            var code = httpOk ? envelope?.ErrorCode : status;
            var where = code is { } c ? $"HTTP {c}" : "Erro";
            var description = Scrub(envelope?.Description ?? string.Empty, token);
            throw new TelegramException(
                description.Length == 0 ? $"{where} ao falar com o Telegram." : $"{where} ao falar com o Telegram: {description}",
                code);
        }

        if (envelope?.Result is not { } result)
        {
            throw new TelegramException("Resposta inv\U000000E1lida do Telegram.");
        }

        return result;
    }

    // Removes the token (and anything shaped like one in a bot URL) and keeps the text short and on one line.
    private static string Scrub(string text, string token)
    {
        text = text.Replace(token, "<token>", StringComparison.Ordinal);
        text = BotUrlToken().Replace(text, "bot<token>");
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length > 200 ? text[..200] : text;
    }

    [GeneratedRegex(@"^\d+:[A-Za-z0-9_-]+$")]
    private static partial Regex TokenShape();

    [GeneratedRegex(@"bot\d+:[A-Za-z0-9_-]+")]
    private static partial Regex BotUrlToken();
}
