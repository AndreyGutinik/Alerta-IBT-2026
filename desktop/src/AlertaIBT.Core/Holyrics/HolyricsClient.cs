using System.Net;
using System.Text;
using System.Text.Json;
using AlertaIBT.Core.Diagnostics;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Holyrics;

/// <summary>What the app asks Holyrics to do.</summary>
public enum HolyricsActionKind
{
    /// <summary><c>ActionNext</c>: the next slide of the presentation on the screen.</summary>
    SlideNext,

    /// <summary><c>ActionPrevious</c>: the previous slide.</summary>
    SlidePrevious,

    /// <summary>Plays the video of the media player when it is not playing, pauses it when it is (<c>GetMediaPlayerInfo</c> then <c>MediaPlayerAction</c>).</summary>
    VideoPlayPause,

    /// <summary>Flips the logo (the app keeps the state: Holyrics has no way to read it).</summary>
    LogoToggle,

    /// <summary>Puts the logo ON or OFF (<see cref="HolyricsCommand.On"/>), whatever it was.</summary>
    LogoSet,
}

/// <summary>One request to Holyrics.</summary>
/// <param name="Kind">What to do.</param>
/// <param name="On">For <see cref="HolyricsActionKind.LogoSet"/>: the logo ON (true) or OFF (false).</param>
public readonly record struct HolyricsCommand(HolyricsActionKind Kind, bool On = false);

/// <summary>How a request ended.</summary>
public enum HolyricsOutcome
{
    /// <summary>Holyrics did it.</summary>
    Ok,

    /// <summary>The connection is not filled in (no token).</summary>
    NotConfigured,

    /// <summary>Holyrics did not answer (off, wrong address or port, network, or too slow).</summary>
    Unreachable,

    /// <summary>The token is wrong or may not run this action.</summary>
    Unauthorized,

    /// <summary>Holyrics answered that no presentation is on the screen (nothing to move; not a failure of the connection).</summary>
    NoPresentation,

    /// <summary>Holyrics answered with another error.</summary>
    Error,
}

/// <summary>The result of a request.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Message">A short pt-BR phrase for the reader, or null when it worked.</param>
public readonly record struct HolyricsResult(HolyricsOutcome Outcome, string? Message = null)
{
    /// <summary>Whether Holyrics did it.</summary>
    public bool Success => Outcome == HolyricsOutcome.Ok;
}

/// <summary>What the connection looks like after the last request.</summary>
public enum HolyricsLink
{
    /// <summary>Nothing was tried yet (or the settings changed since).</summary>
    Unknown,

    /// <summary>Holyrics answered and the token works.</summary>
    Connected,

    /// <summary>No answer.</summary>
    Unreachable,

    /// <summary>Holyrics answered, but the token is not accepted (for everything or for an action).</summary>
    Unauthorized,
}

/// <summary>The last known state of the connection, for the chip of the screen.</summary>
/// <param name="Link">The state.</param>
/// <param name="Message">The words to show (pt-BR): what is wrong, or "conectado".</param>
public sealed record HolyricsStatus(HolyricsLink Link, string Message);

/// <summary>What the "Testar" button found out.</summary>
/// <param name="Link">The state.</param>
/// <param name="Message">The chip words.</param>
/// <param name="MissingPermissions">The actions the token may not run (empty when none or unknown).</param>
/// <param name="Playing">Whether the media player was playing (null when not read).</param>
public sealed record HolyricsTestResult(HolyricsLink Link, string Message, IReadOnlyList<string> MissingPermissions, bool? Playing = null);

/// <summary>What the rest of the app may ask of Holyrics: send a command (it returns at once; nothing blocks the caller).</summary>
public interface IHolyricsRemote
{
    /// <summary>Whether the connection is filled in (a token is stored and the address is usable).</summary>
    bool IsConfigured { get; }

    /// <summary>Whether the arrows of the quick panel go to Holyrics: the switch is on and the connection is configured.</summary>
    bool ArrowsActive { get; }

    /// <summary>
    /// Queues <paramref name="command"/> to be sent in the background and returns at once. False when it cannot be sent (not configured); true when it was
    /// queued or dropped on purpose (the same action was already waiting, or came again within a few milliseconds).
    /// </summary>
    bool Post(HolyricsCommand command);
}

/// <summary>
/// The client of the HTTP API of Holyrics (<c>POST http://HOST:PORT/api/ACTION?token=TOKEN</c>, a JSON body, an answer of <c>{"status":"ok"}</c> or
/// <c>{"status":"error","error":"..."}</c>). Four things the app does with it: next and previous slide, play/pause of the media player, and the logo.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never blocks the caller.</b> <see cref="Post"/> only puts the command in a small queue; one background task sends them one after another (so a held button
/// cannot open a flood of connections). At most one command of each kind waits at a time (a logo state replaces the one waiting), and the same kind is not
/// accepted again within <see cref="MinInterval"/>. Each call has its own short timeout (2 s). There are no retries: the next press is the retry.
/// </para>
/// <para>
/// <b>The token</b> is read from the settings in its protected form only when a request is built, goes only in the URL query, and is never logged: every text
/// that can reach the log (an exception, a message from the server) first has it replaced by <c>***</c>.
/// </para>
/// <para>
/// <b>The logo.</b> Holyrics offers <c>SetLogo {enable, hide}</c> and no way to read the state, so this class keeps its own (<see cref="LogoShown"/>, hidden at
/// start). ON is <c>{"enable":true,"hide":false}</c>; OFF is <c>{"enable":false,"hide":true}</c>, the state the owner's Holyrics was found in.
/// </para>
/// </remarks>
public sealed class HolyricsClient : IHolyricsRemote, IDisposable
{
    /// <summary>The time a request may take.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The permissions the app needs, as <c>CheckPermissions</c> takes them.</summary>
    public const string RequiredActions = "ActionNext,ActionPrevious,MediaPlayerAction,GetMediaPlayerInfo,SetLogo";

    private static readonly TimeSpan WarnEvery = TimeSpan.FromSeconds(5);

    private readonly SettingsService _settings;
    private readonly ISecretProtector _protector;
    private readonly ILogger? _logger;
    private readonly TimeProvider _clock;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly object _gate = new();
    private readonly List<HolyricsCommand> _pending = [];
    private readonly Dictionary<HolyricsActionKind, long> _lastAccepted = [];
    private readonly Dictionary<string, long> _lastWarn = [];
    private bool _draining;
    private bool _disposed;
    private bool _logoShown;
    private HolyricsStatus _status = new(HolyricsLink.Unknown, string.Empty);

    /// <param name="settings">Where the connection settings live.</param>
    /// <param name="protector">Opens the protected token.</param>
    /// <param name="logger">Receives the log lines (never the token).</param>
    /// <param name="clock">Dates the debounce and the log throttle.</param>
    /// <param name="timeout">The time a request may take (<see cref="RequestTimeout"/> by default).</param>
    /// <param name="minInterval">The least time between two accepted commands of the same kind (<see cref="MinInterval"/> by default).</param>
    /// <param name="handler">The HTTP handler (a plain one by default).</param>
    public HolyricsClient(
        SettingsService settings,
        ISecretProtector protector,
        ILogger? logger = null,
        TimeProvider? clock = null,
        TimeSpan? timeout = null,
        TimeSpan? minInterval = null,
        HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(protector);

        _settings = settings;
        _protector = protector;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        MinInterval = minInterval ?? DefaultMinInterval;
        _http = handler is null ? new HttpClient { Timeout = timeout ?? RequestTimeout } : new HttpClient(handler) { Timeout = timeout ?? RequestTimeout };
        _ownsHttp = true;
        _lastSeen = settings.Current.Holyrics;
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>The default for <see cref="MinInterval"/>.</summary>
    public static readonly TimeSpan DefaultMinInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>The least time between two accepted commands of the same kind (a key held down or a flapping channel cannot flood Holyrics).</summary>
    public TimeSpan MinInterval { get; }

    /// <summary>Raised (from any thread) when <see cref="Status"/> may have changed.</summary>
    public event Action? StatusChanged;

    /// <summary>The state of the connection after the last request.</summary>
    public HolyricsStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>Whether the app believes the logo is shown (Holyrics cannot tell; it starts hidden and follows what the app sent last).</summary>
    public bool LogoShown
    {
        get
        {
            lock (_gate)
            {
                return _logoShown;
            }
        }
    }

    /// <inheritdoc />
    public bool IsConfigured
    {
        get
        {
            var holyrics = _settings.Current.Holyrics;
            return holyrics.TokenProtected is not null && HolyricsSettings.IsValidHost(holyrics.Host) && holyrics.Port is >= 1 and <= 65535;
        }
    }

    /// <inheritdoc />
    public bool ArrowsActive => _settings.Current.Holyrics.ArrowsEnabled && IsConfigured;

    /// <inheritdoc />
    public bool Post(HolyricsCommand command)
    {
        if (!IsConfigured)
        {
            return false;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            var now = _clock.GetTimestamp();
            if (command.Kind != HolyricsActionKind.LogoSet && _lastAccepted.TryGetValue(command.Kind, out var last) && _clock.GetElapsedTime(last, now) < MinInterval)
            {
                return true; // the same action came again within a blink: one is enough
            }

            var index = _pending.FindIndex(p => p.Kind == command.Kind);
            if (index >= 0)
            {
                if (command.Kind == HolyricsActionKind.LogoSet)
                {
                    _pending[index] = command; // the newest wish wins
                }

                return true; // one of this kind is already waiting
            }

            _lastAccepted[command.Kind] = now;
            _pending.Add(command);
            if (_draining)
            {
                return true;
            }

            _draining = true;
        }

        _ = Task.Run(DrainAsync);
        return true;
    }

    /// <summary>Sends one command now and says how it ended (the background worker and the tests use it; the app's own callers use <see cref="Post"/>).</summary>
    /// <param name="command">What to do.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<HolyricsResult> ExecuteAsync(HolyricsCommand command, CancellationToken cancellationToken = default)
    {
        HolyricsResult result;
        try
        {
            result = command.Kind switch
            {
                HolyricsActionKind.SlideNext => Simple(await CallAsync("ActionNext", "{}", cancellationToken).ConfigureAwait(false)),
                HolyricsActionKind.SlidePrevious => Simple(await CallAsync("ActionPrevious", "{}", cancellationToken).ConfigureAwait(false)),
                HolyricsActionKind.VideoPlayPause => await PlayPauseAsync(cancellationToken).ConfigureAwait(false),
                HolyricsActionKind.LogoToggle => await SetLogoAsync(!LogoShown, cancellationToken).ConfigureAwait(false),
                HolyricsActionKind.LogoSet => await SetLogoAsync(command.On, cancellationToken).ConfigureAwait(false),
                _ => new HolyricsResult(HolyricsOutcome.Error, "a\U000000E7\U000000E3o desconhecida"),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = new HolyricsResult(HolyricsOutcome.Error, "erro inesperado");
            _logger?.LogWarning("Holyrics: erro inesperado em {Action} ({Type})", command.Kind, ex.GetType().Name);
        }

        Report(command, result);
        return result;
    }

    /// <summary>
    /// The "Testar" button: asks Holyrics whether the token may run what the app needs (<c>CheckPermissions</c>) and reads the media player (<c>GetMediaPlayerInfo</c>).
    /// It changes nothing in Holyrics.
    /// </summary>
    /// <param name="cancellationToken">Cancels the requests.</param>
    public async Task<HolyricsTestResult> TestAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return SetStatus(new HolyricsTestResult(HolyricsLink.Unknown, "falta o token", []));
        }

        var check = await CallAsync("CheckPermissions", JsonSerializer.Serialize(new { actions = RequiredActions }), cancellationToken).ConfigureAwait(false);
        if (check.Outcome == HolyricsOutcome.Unreachable)
        {
            return SetStatus(new HolyricsTestResult(HolyricsLink.Unreachable, "sem resposta", []));
        }

        if (check.Outcome == HolyricsOutcome.Unauthorized)
        {
            var missing = check.Unauthorized.Count > 0 ? check.Unauthorized : RequiredActions.Split(',');
            var words = check.Unauthorized.Count > 0 ? "token sem permiss\U000000E3o para " + string.Join(", ", check.Unauthorized) : "token n\U000000E3o aceito";
            return SetStatus(new HolyricsTestResult(HolyricsLink.Unauthorized, words, missing));
        }

        if (check.Outcome != HolyricsOutcome.Ok)
        {
            return SetStatus(new HolyricsTestResult(HolyricsLink.Unreachable, check.Message ?? "erro", []));
        }

        var info = await CallAsync("GetMediaPlayerInfo", "{}", cancellationToken).ConfigureAwait(false);
        bool? playing = info is { Outcome: HolyricsOutcome.Ok, Data: { ValueKind: JsonValueKind.Object } data } && data.TryGetProperty("playing", out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? p.GetBoolean()
            : null;
        return SetStatus(new HolyricsTestResult(HolyricsLink.Connected, "conectado", [], playing));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pending.Clear();
        }

        _settings.Changed -= OnSettingsChanged;
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private static HolyricsResult Simple(Reply reply) => reply.ToResult();

    private async Task<HolyricsResult> PlayPauseAsync(CancellationToken ct)
    {
        var info = await CallAsync("GetMediaPlayerInfo", "{}", ct).ConfigureAwait(false);
        if (!info.Success)
        {
            return info.ToResult();
        }

        var playing = info.Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("playing", out var p) && p.ValueKind == JsonValueKind.True;
        var action = playing ? "pause" : "play";
        return (await CallAsync("MediaPlayerAction", "{\"action\":\"" + action + "\"}", ct).ConfigureAwait(false)).ToResult();
    }

    private async Task<HolyricsResult> SetLogoAsync(bool show, CancellationToken ct)
    {
        // ON = the functionality enabled and not hidden; OFF = disabled and hidden (how the owner's Holyrics was found).
        var body = show ? "{\"enable\":true,\"hide\":false}" : "{\"enable\":false,\"hide\":true}";
        var result = (await CallAsync("SetLogo", body, ct).ConfigureAwait(false)).ToResult();
        if (result.Success)
        {
            lock (_gate)
            {
                _logoShown = show;
            }
        }

        return result;
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            HolyricsCommand next;
            lock (_gate)
            {
                if (_pending.Count == 0 || _disposed)
                {
                    _draining = false;
                    return;
                }

                next = _pending[0];
                _pending.RemoveAt(0);
            }

            try
            {
                await ExecuteAsync(next).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("Holyrics: falha ao enviar {Action} ({Type})", next.Kind, ex.GetType().Name);
            }
        }
    }

    // One POST. Never throws (except a cancel the caller asked for); the token only lives in the local variables of this method.
    private async Task<Reply> CallAsync(string action, string body, CancellationToken ct)
    {
        var holyrics = _settings.Current.Holyrics;
        var token = _protector.Unprotect(holyrics.TokenProtected);
        if (string.IsNullOrEmpty(token) || !HolyricsSettings.IsValidHost(holyrics.Host))
        {
            return Reply.Failed(HolyricsOutcome.NotConfigured, "falta o token");
        }

        var host = holyrics.Host.Trim();
        if (host.Contains(':'))
        {
            host = "[" + host + "]"; // an IPv6 address
        }

        var url = $"http://{host}:{holyrics.Port}/api/{action}?token={Uri.EscapeDataString(token)}";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(response.StatusCode, text, token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Reply.Failed(HolyricsOutcome.Unreachable, "sem resposta (demorou mais de " + RequestTimeoutText() + ")");
        }
        catch (HttpRequestException ex)
        {
            return Reply.Failed(HolyricsOutcome.Unreachable, "sem resposta (" + (ex.InnerException?.GetType().Name ?? ex.GetType().Name) + ")");
        }
        catch (Exception ex)
        {
            return Reply.Failed(HolyricsOutcome.Unreachable, "sem resposta (" + ex.GetType().Name + ")");
        }
    }

    private string RequestTimeoutText() => (_http.Timeout.TotalSeconds < 10 ? _http.Timeout.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : "10") + " s";

    private static Reply Parse(HttpStatusCode code, string text, string token)
    {
        if (code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new Reply(HolyricsOutcome.Unauthorized, "token n\U000000E3o aceito", [.. ExtractUnauthorized(text)], null);
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var status = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return new Reply(HolyricsOutcome.Ok, null, [], root.TryGetProperty("data", out var data) ? data.Clone() : null);
            }

            var error = ErrorText(root, token);
            if (error.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
            {
                return new Reply(HolyricsOutcome.Unauthorized, "token sem permiss\U000000E3o", [.. ExtractUnauthorized(text)], null);
            }

            if (error.Contains("No presentation", StringComparison.OrdinalIgnoreCase))
            {
                return Reply.Failed(HolyricsOutcome.NoPresentation, "sem apresenta\U000000E7\U000000E3o aberta");
            }

            return Reply.Failed(HolyricsOutcome.Error, error.Length > 0 ? error : "erro do Holyrics");
        }
        catch (JsonException)
        {
            return code == HttpStatusCode.OK
                ? Reply.Failed(HolyricsOutcome.Error, "resposta que n\U000000E3o \U000000E9 do Holyrics (porta errada?)")
                : Reply.Failed(HolyricsOutcome.Error, "HTTP " + (int)code);
        }
    }

    private static string ErrorText(JsonElement root, string token)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error))
        {
            return string.Empty;
        }

        var text = error.ValueKind == JsonValueKind.String ? error.GetString() ?? string.Empty : error.ValueKind == JsonValueKind.Object ? error.GetRawText() : string.Empty;
        return Redact(text, token);
    }

    // CheckPermissions answers {"status":"error","error":{"unauthorized_actions":"A,B", ...}}.
    private static List<string> ExtractUnauthorized(string text)
    {
        var list = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("unauthorized_actions", out var actions)
                && actions.ValueKind == JsonValueKind.String)
            {
                list.AddRange((actions.GetString() ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }
        catch (JsonException)
        {
            // not JSON: nothing to list
        }

        return list;
    }

    private static string Redact(string text, string token) =>
        string.IsNullOrEmpty(token) ? text : text.Replace(token, "***", StringComparison.Ordinal).Replace(Uri.EscapeDataString(token), "***", StringComparison.Ordinal);

    private void Report(HolyricsCommand command, HolyricsResult result)
    {
        Breadcrumbs.Add("holyrics: " + command.Kind + " " + result.Outcome);
        switch (result.Outcome)
        {
            case HolyricsOutcome.Ok:
                SetLink(HolyricsLink.Connected, "conectado");
                _logger?.LogDebug("Holyrics: {Action} ok", command.Kind);
                break;
            case HolyricsOutcome.NoPresentation:
                SetLink(HolyricsLink.Connected, "conectado");
                _logger?.LogDebug("Holyrics: {Action} sem apresenta\U000000E7\U000000E3o aberta", command.Kind);
                break;
            case HolyricsOutcome.Unreachable:
                SetLink(HolyricsLink.Unreachable, "sem resposta");
                Warn("unreachable", command, result);
                break;
            case HolyricsOutcome.Unauthorized:
                SetLink(HolyricsLink.Unauthorized, result.Message ?? "token sem permiss\U000000E3o");
                Warn("unauthorized", command, result);
                break;
            default:
                Warn("error", command, result);
                break;
        }
    }

    // At most one warning of each kind every few seconds: a dead Holyrics must not fill the log while someone holds a key.
    private void Warn(string kind, HolyricsCommand command, HolyricsResult result)
    {
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            if (_lastWarn.TryGetValue(kind, out var last) && _clock.GetElapsedTime(last, now) < WarnEvery)
            {
                return;
            }

            _lastWarn[kind] = now;
        }

        _logger?.LogWarning("Holyrics: {Action} falhou ({Outcome}): {Message}", command.Kind, result.Outcome, result.Message);
    }

    private HolyricsTestResult SetStatus(HolyricsTestResult result)
    {
        SetLink(result.Link, result.Message);
        Breadcrumbs.Add("holyrics: teste " + result.Link);
        _logger?.LogInformation("Holyrics: teste da conex\U000000E3o: {Link}", result.Link);
        return result;
    }

    private void SetLink(HolyricsLink link, string message)
    {
        var next = new HolyricsStatus(link, message);
        lock (_gate)
        {
            if (_status == next)
            {
                return;
            }

            _status = next;
        }

        StatusChanged?.Invoke();
    }

    // A new address, port or token is a new connection: the old verdict (and the old test) no longer applies.
    private void OnSettingsChanged(AppSettings settings)
    {
        var next = settings.Holyrics;
        lock (_gate)
        {
            if (_lastSeen is { } seen && seen.Host == next.Host && seen.Port == next.Port && seen.TokenProtected == next.TokenProtected)
            {
                return;
            }

            _lastSeen = next;
            _status = new HolyricsStatus(HolyricsLink.Unknown, string.Empty);
        }

        StatusChanged?.Invoke();
    }

    private HolyricsSettings? _lastSeen;

    private sealed record Reply(HolyricsOutcome Outcome, string? Message, IReadOnlyList<string> Unauthorized, JsonElement? Data)
    {
        public bool Success => Outcome == HolyricsOutcome.Ok;

        public static Reply Failed(HolyricsOutcome outcome, string message) => new(outcome, message, [], null);

        public HolyricsResult ToResult() => new(Outcome, Message);
    }
}
