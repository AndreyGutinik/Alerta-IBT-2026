using System.Net;
using System.Text;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// <param name="Message">A short pt-BR phrase for the reader (the reason, for a failure), or null when it worked.</param>
/// <param name="Detail">For a failure: the full account, one fact per line (action, host:port, HTTP status, exception, time); never the token. Null when it worked.</param>
public readonly record struct HolyricsResult(HolyricsOutcome Outcome, string? Message = null, string? Detail = null)
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

    /// <summary>The permission check is waiting: Holyrics may be asking the owner, in its own window, to approve the token.</summary>
    Waiting,

    /// <summary>Holyrics answered with an error (or with something that is not its API).</summary>
    Error,
}

/// <summary>The last known state of the connection, for the chip of the screen.</summary>
/// <param name="Link">The state.</param>
/// <param name="Message">The words to show (pt-BR): the reason of the failure, or "conectado".</param>
/// <param name="Detail">The full account of the last failure (for the tooltip); empty when none.</param>
public sealed record HolyricsStatus(HolyricsLink Link, string Message, string Detail = "");

/// <summary>What the "Testar" button found out.</summary>
/// <param name="Link">The state.</param>
/// <param name="Message">The chip words.</param>
/// <param name="MissingPermissions">The actions the token may not run (empty when none or unknown).</param>
/// <param name="Playing">Whether the media player was playing (null when not read).</param>
/// <param name="Detail">The full account of the failure (for the tooltip); empty when it worked.</param>
public sealed record HolyricsTestResult(HolyricsLink Link, string Message, IReadOnlyList<string> MissingPermissions, bool? Playing = null, string Detail = "");

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

    /// <summary>The time the permission check may take: Holyrics can wait for the owner to approve the token in its own window.</summary>
    public static readonly TimeSpan PermissionTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The permissions the app needs, as <c>CheckPermissions</c> takes them.</summary>
    public const string RequiredActions = "ActionNext,ActionPrevious,MediaPlayerAction,GetMediaPlayerInfo,SetLogo";

    private static readonly TimeSpan WarnEvery = TimeSpan.FromSeconds(5);

    private readonly SettingsService _settings;
    private readonly ISecretProtector _protector;
    private readonly ILogger? _logger;
    private readonly TimeProvider _clock;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _permissionTimeout;
    private readonly TimeSpan _waitingAfter;
    private readonly bool _ownsHttp;
    private readonly object _gate = new();
    private readonly List<HolyricsCommand> _pending = [];
    private readonly Dictionary<HolyricsActionKind, long> _lastAccepted = [];
    private readonly Dictionary<string, (long At, int Suppressed)> _lastWarn = [];
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
    /// <param name="handler">The HTTP handler (by default one that ignores the system proxy: this API is local or on the LAN).</param>
    /// <param name="permissionTimeout">The time the permission check of "Testar" may take (<see cref="PermissionTimeout"/> by default).</param>
    /// <param name="waitingAfter">After this long without an answer, "Testar" shows "aguardando voc\U000000EA aprovar" (the normal timeout by default).</param>
    public HolyricsClient(
        SettingsService settings,
        ISecretProtector protector,
        ILogger? logger = null,
        TimeProvider? clock = null,
        TimeSpan? timeout = null,
        TimeSpan? minInterval = null,
        HttpMessageHandler? handler = null,
        TimeSpan? permissionTimeout = null,
        TimeSpan? waitingAfter = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(protector);

        _settings = settings;
        _protector = protector;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        MinInterval = minInterval ?? DefaultMinInterval;
        _timeout = timeout ?? RequestTimeout;
        _permissionTimeout = permissionTimeout ?? PermissionTimeout;
        _waitingAfter = waitingAfter ?? _timeout;

        // The limit of each call is a token (a normal action 2 s, the permission check longer), so the client itself never times out. No proxy: a system
        // or WinHTTP proxy must not stand between the app and a Holyrics on this PC or the LAN.
        handler ??= new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false, ConnectCallback = ConnectAsync };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
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
    /// It changes nothing in Holyrics. The permission check may wait for the owner to approve the token in the Holyrics window, so it has a longer limit
    /// (<see cref="PermissionTimeout"/>), and while it waits the status says so. Every failure carries its reason.
    /// </summary>
    /// <param name="cancellationToken">Cancels the requests.</param>
    public async Task<HolyricsTestResult> TestAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return SetStatus(new HolyricsTestResult(HolyricsLink.Unknown, "falta o token", []));
        }

        // A timer, not a task: when the answer comes at once nothing hops threads (the screen's command may be waiting for this call on its own thread).
        var settled = false;
        var gate = new object();
        using var waitingTimer = new Timer(
            _ =>
            {
                lock (gate)
                {
                    if (!settled)
                    {
                        SetLink(
                            HolyricsLink.Waiting,
                            "aguardando voc\U000000EA aprovar no Holyrics",
                            "O Holyrics ainda n\U000000E3o respondeu \U000000E0 confer\U000000EAncia de permiss\U000000F5es. Ele pode estar mostrando, na tela dele, um pedido para voc\U000000EA aprovar o token: aprove l\U000000E1 (o app espera at\U000000E9 " + SecondsText(_permissionTimeout) + ").");
                    }
                }
            },
            null,
            _waitingAfter,
            Timeout.InfiniteTimeSpan);

        Reply check;
        try
        {
            check = await CallAsync("CheckPermissions", JsonSerializer.Serialize(new { actions = RequiredActions }), cancellationToken, _permissionTimeout).ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                settled = true; // a late "aguardando" can never overwrite the verdict below
            }
        }

        var detail = check.Detail ?? string.Empty;
        if (check.Outcome == HolyricsOutcome.Unreachable)
        {
            LogFailure("teste", check.Outcome, check.Message, check.Detail);
            return SetStatus(new HolyricsTestResult(HolyricsLink.Unreachable, check.Message ?? "sem resposta", [], Detail: detail));
        }

        if (check.Outcome == HolyricsOutcome.Unauthorized)
        {
            var missing = check.Unauthorized.Count > 0 ? check.Unauthorized : RequiredActions.Split(',');
            var list = string.Join(", ", check.Unauthorized);
            var words = check.Unauthorized.Count == 0
                ? check.Message ?? "token n\U000000E3o aceito"
                : check.RequestStatus switch
                {
                    "pending" => "permiss\U000000E3o pendente no Holyrics para " + list,
                    "denied" => "permiss\U000000E3o negada no Holyrics para " + list,
                    _ => "token sem permiss\U000000E3o para " + list,
                };
            LogFailure("teste", check.Outcome, words, check.Detail);
            return SetStatus(new HolyricsTestResult(HolyricsLink.Unauthorized, words, missing, Detail: detail));
        }

        if (check.Outcome != HolyricsOutcome.Ok)
        {
            LogFailure("teste", check.Outcome, check.Message, check.Detail);
            return SetStatus(new HolyricsTestResult(HolyricsLink.Error, check.Message ?? "erro do Holyrics", [], Detail: detail));
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

    // One POST. Never throws (except a cancel the caller asked for); the token only lives in the local variables of this method. Every failure comes back
    // with a short reason (Message) and the full account (Detail: action, host:port, HTTP status, exception, time), both free of the token.
    private async Task<Reply> CallAsync(string action, string body, CancellationToken ct, TimeSpan? limit = null)
    {
        var holyrics = _settings.Current.Holyrics;
        var token = _protector.Unprotect(holyrics.TokenProtected);
        if (string.IsNullOrEmpty(token) || !HolyricsSettings.IsValidHost(holyrics.Host))
        {
            return Reply.Failed(HolyricsOutcome.NotConfigured, "falta o token");
        }

        var host = holyrics.Host.Trim();
        var endpoint = host + ":" + holyrics.Port;
        if (host.Contains(':'))
        {
            host = "[" + host + "]"; // an IPv6 address
        }

        var maxTime = limit ?? _timeout;
        var started = _clock.GetTimestamp();
        var probe = new ConnectProbe();
        var url = $"http://{host}:{holyrics.Port}/api/{action}?token={Uri.EscapeDataString(token)}";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Connecting gets a little extra (Windows takes about 2 s to refuse a connection to a closed port, and the owner must read "conex\U000000E3o recusada", not
        // "demorou"); once connected, the answer has the normal limit counted from that moment.
        timeout.CancelAfter(maxTime + ConnectExtra);
        probe.Connected = () => timeout.CancelAfter(maxTime);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonBody(body) };
            request.Options.Set(ProbeKey, probe);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var reply = Parse(response.StatusCode, text, token, action);
            return reply.Outcome is HolyricsOutcome.Ok or HolyricsOutcome.NoPresentation
                ? reply
                : reply with { Detail = Detail(action, endpoint, (int)response.StatusCode, null, Elapsed(started), reply.Outcome == HolyricsOutcome.Error ? Snippet(text, token) : null) };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            var message = probe.Connecting
                ? "sem resposta: n\U000000E3o conectou em " + endpoint + " em " + SecondsText(maxTime + ConnectExtra) + " (porta fechada ou endere\U000000E7o errado)"
                : "sem resposta: demorou mais de " + SecondsText(maxTime);
            if (action == "CheckPermissions" && !probe.Connecting)
            {
                message += " (o Holyrics pode estar esperando voc\U000000EA aprovar a permiss\U000000E3o do token na tela dele)";
            }

            return Failed(message, action, endpoint, Describe(ex, token), started);
        }
        catch (HttpRequestException ex)
        {
            return Failed(ConnectionReason(ex, endpoint, maxTime), action, endpoint, Describe(ex, token), started);
        }
        catch (Exception ex)
        {
            return Failed("sem resposta: falha inesperada (" + ex.GetType().Name + ")", action, endpoint, Describe(ex, token), started);
        }
    }

    // Tells the timeout handler whether the app was still trying to connect (then the port is closed or the address wrong) or already connected (then Holyrics is slow).
    private sealed class ConnectProbe
    {
        public volatile bool Connecting;

        public Action? Connected { get; set; }
    }

    private static readonly HttpRequestOptionsKey<ConnectProbe> ProbeKey = new("ibt.holyrics.probe");

    // The least extra time to connect on top of the normal limit.
    private static readonly TimeSpan ConnectExtra = TimeSpan.FromSeconds(3);

    // Connects like the default handler would, but IPv4 first (a Holyrics listens on IPv4 and "localhost" would try ::1 first, a slow refusal on Windows) and
    // telling the probe when it is done.
    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        ConnectProbe? probe = null;
        context.InitialRequestMessage?.Options.TryGetValue(ProbeKey, out probe);
        if (probe is not null)
        {
            probe.Connecting = true;
        }

        var host = context.DnsEndPoint.Host;
        var addresses = System.Net.IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        SocketException? last = null;
        foreach (var address in addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new System.Net.IPEndPoint(address, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
                if (probe is not null)
                {
                    probe.Connecting = false;
                    probe.Connected?.Invoke();
                }

                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    // The real Holyrics answers 415 {} to "application/json; charset=utf-8" (what StringContent sends): the type must be exactly "application/json".
    private static ByteArrayContent JsonBody(string body)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return content;
    }

    private Reply Failed(string message, string action, string endpoint, string exception, long started) =>
        new(HolyricsOutcome.Unreachable, message, [], null, Detail(action, endpoint, null, exception, Elapsed(started), null));

    private double Elapsed(long started) => _clock.GetElapsedTime(started, _clock.GetTimestamp()).TotalMilliseconds;

    // The facts of a failure, one per line, in pt-BR. Never the token and never the URL with its query.
    private static string Detail(string action, string endpoint, int? http, string? exception, double milliseconds, string? answer)
    {
        var lines = new List<string> { "a\U000000E7\U000000E3o: " + action, "endere\U000000E7o: " + endpoint };
        if (http is not null)
        {
            lines.Add("HTTP: " + http);
        }

        if (!string.IsNullOrEmpty(exception))
        {
            lines.Add("exce\U000000E7\U000000E3o: " + exception);
        }

        if (!string.IsNullOrEmpty(answer))
        {
            lines.Add("resposta: " + answer);
        }

        lines.Add("tempo: " + Math.Round(milliseconds).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " ms");
        return string.Join('\n', lines);
    }

    // The reason a connection could not be made or kept, in words the owner can act on.
    private static string ConnectionReason(HttpRequestException ex, string endpoint, TimeSpan limit)
    {
        var socket = Chain(ex).OfType<SocketException>().FirstOrDefault();
        if (Chain(ex).Any(e => e is TimeoutException))
        {
            return "sem resposta: n\U000000E3o conectou em " + endpoint + " em " + SecondsText(limit);
        }

        switch (socket?.SocketErrorCode)
        {
            case SocketError.ConnectionRefused:
                return "sem resposta: conex\U000000E3o recusada em " + endpoint;
            case SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData:
                return "sem resposta: endere\U000000E7o n\U000000E3o encontrado";
            case SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown:
                return "sem resposta: rede inacess\U000000EDvel at\U000000E9 " + endpoint;
            case SocketError.TimedOut:
                return "sem resposta: n\U000000E3o conectou em " + endpoint;
            case SocketError.ConnectionReset or SocketError.ConnectionAborted:
                return "sem resposta: a conex\U000000E3o com " + endpoint + " foi interrompida";
        }

        return ex.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => "sem resposta: endere\U000000E7o n\U000000E3o encontrado",
            HttpRequestError.ConnectionError => "sem resposta: n\U000000E3o conectou em " + endpoint,
            HttpRequestError.InvalidResponse or HttpRequestError.ResponseEnded => "sem resposta: " + endpoint + " fechou a conex\U000000E3o sem responder como o Holyrics",
            _ => "sem resposta: falha de rede (" + (socket?.GetType().Name ?? ex.InnerException?.GetType().Name ?? ex.GetType().Name) + ")",
        };
    }

    private static List<Exception> Chain(Exception ex)
    {
        var list = new List<Exception>();
        for (var e = ex; e is not null && list.Count < 6; e = e.InnerException)
        {
            list.Add(e);
        }

        return list;
    }

    // "HttpRequestException: message > SocketException (ConnectionRefused): message", sanitized.
    private static string Describe(Exception ex, string token) =>
        string.Join(" > ", Chain(ex).Take(3).Select(e => e.GetType().Name + (e is SocketException s ? " (" + s.SocketErrorCode + ")" : string.Empty) + ": " + Sanitize(e.Message, token)));

    private static string SecondsText(TimeSpan time) =>
        Math.Round(time.TotalSeconds, 1).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s";

    private static string Snippet(string text, string token)
    {
        var flat = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        return Sanitize(flat.Length > 160 ? flat[..160] + "..." : flat, token);
    }

    private static Reply Parse(HttpStatusCode code, string text, string token, string action)
    {
        if (code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new Reply(HolyricsOutcome.Unauthorized, "token n\U000000E3o aceito (HTTP " + (int)code + ")", [.. ExtractUnauthorized(text)], null, null, ExtractRequestStatus(text));
        }

        if (code == HttpStatusCode.UnsupportedMediaType)
        {
            return Reply.Failed(HolyricsOutcome.Error, "o Holyrics recusou o formato do pedido (HTTP 415)");
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

            if (status is null)
            {
                return Reply.Failed(HolyricsOutcome.Error, "resposta inesperada (HTTP " + (int)code + ")");
            }

            var error = ErrorText(root, token);
            if (error.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
            {
                return new Reply(HolyricsOutcome.Unauthorized, "token sem permiss\U000000E3o para " + action, [.. ExtractUnauthorized(text)], null, null, ExtractRequestStatus(text));
            }

            if (error.Contains("No presentation", StringComparison.OrdinalIgnoreCase))
            {
                return Reply.Failed(HolyricsOutcome.NoPresentation, "sem apresenta\U000000E7\U000000E3o aberta");
            }

            return Reply.Failed(HolyricsOutcome.Error, error.Length > 0 ? "erro do Holyrics: " + error : "erro do Holyrics (HTTP " + (int)code + ", sem mensagem)");
        }
        catch (JsonException)
        {
            return code == HttpStatusCode.OK
                ? Reply.Failed(HolyricsOutcome.Error, "resposta que n\U000000E3o \U000000E9 do Holyrics (porta errada?)")
                : Reply.Failed(HolyricsOutcome.Error, "resposta inesperada (HTTP " + (int)code + ")");
        }
    }

    private static string ErrorText(JsonElement root, string token)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error))
        {
            return string.Empty;
        }

        var text = error.ValueKind == JsonValueKind.String ? error.GetString() ?? string.Empty : error.ValueKind == JsonValueKind.Object ? error.GetRawText() : string.Empty;
        return Sanitize(text, token);
    }

    // CheckPermissions answers {"status":"error","error":{"unauthorized_actions":"A,B","request_status":"pending"|"denied"}}.
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

    private static string? ExtractRequestStatus(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("request_status", out var status)
                && status.ValueKind == JsonValueKind.String
                    ? status.GetString()?.Trim().ToLowerInvariant()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly Regex UrlQuery = new(@"(https?://[^\s?""'<>]+)\?\S*", RegexOptions.Compiled);
    private static readonly Regex TokenParam = new(@"(?i)token=[^&\s""']*", RegexOptions.Compiled);
    private static readonly Regex LongSecret = new(@"[A-Za-z0-9_\-]{24,}", RegexOptions.Compiled);

    /// <summary>Makes a text safe for the screen and the log: no token (as is or percent-encoded), no query string of a URL, nothing that looks like a long secret.</summary>
    internal static string Sanitize(string? text, string token)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var safe = text;
        if (!string.IsNullOrEmpty(token))
        {
            safe = safe.Replace(token, "***", StringComparison.Ordinal).Replace(Uri.EscapeDataString(token), "***", StringComparison.Ordinal);
        }

        safe = UrlQuery.Replace(safe, "$1");
        safe = TokenParam.Replace(safe, "token=***");
        return LongSecret.Replace(safe, "***");
    }

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
                SetLink(HolyricsLink.Unreachable, result.Message ?? "sem resposta", result.Detail ?? string.Empty);
                LogFailure(command.Kind.ToString(), result.Outcome, result.Message, result.Detail);
                break;
            case HolyricsOutcome.Unauthorized:
                SetLink(HolyricsLink.Unauthorized, result.Message ?? "token sem permiss\U000000E3o", result.Detail ?? string.Empty);
                LogFailure(command.Kind.ToString(), result.Outcome, result.Message, result.Detail);
                break;
            case HolyricsOutcome.NotConfigured:
                break;
            default:
                SetLink(HolyricsLink.Error, result.Message ?? "erro do Holyrics", result.Detail ?? string.Empty);
                LogFailure(command.Kind.ToString(), result.Outcome, result.Message, result.Detail);
                break;
        }
    }

    // One line per failed call with the reason and the facts (already free of the token). The same line within a few seconds is not repeated (a held key must
    // not fill the log); the next one that does get through says how many were left out.
    private void LogFailure(string who, HolyricsOutcome outcome, string? message, string? detail)
    {
        var key = who + "|" + message;
        int suppressed;
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            if (_lastWarn.TryGetValue(key, out var last))
            {
                if (_clock.GetElapsedTime(last.At, now) < WarnEvery)
                {
                    _lastWarn[key] = (last.At, last.Suppressed + 1);
                    return;
                }

                suppressed = last.Suppressed;
            }
            else
            {
                suppressed = 0;
                if (_lastWarn.Count > 64)
                {
                    _lastWarn.Clear();
                }
            }

            _lastWarn[key] = (now, 0);
        }

        var facts = string.IsNullOrEmpty(detail) ? string.Empty : " [" + detail.Replace("\n", "; ", StringComparison.Ordinal) + "]";
        var repeats = suppressed > 0 ? " (repetido " + suppressed + " vezes)" : string.Empty;
        _logger?.LogWarning("Holyrics: {Who} falhou ({Outcome}): {Message}{Facts}{Repeats}", who, outcome, message, facts, repeats);
    }

    private HolyricsTestResult SetStatus(HolyricsTestResult result)
    {
        SetLink(result.Link, result.Message, result.Detail);
        Breadcrumbs.Add("holyrics: teste " + result.Link);
        _logger?.LogInformation("Holyrics: teste da conex\U000000E3o: {Link}: {Message}", result.Link, result.Message);
        return result;
    }

    private void SetLink(HolyricsLink link, string message, string detail = "")
    {
        var next = new HolyricsStatus(link, message, detail);
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

    private sealed record Reply(HolyricsOutcome Outcome, string? Message, IReadOnlyList<string> Unauthorized, JsonElement? Data, string? Detail = null, string? RequestStatus = null)
    {
        public bool Success => Outcome == HolyricsOutcome.Ok;

        public static Reply Failed(HolyricsOutcome outcome, string message) => new(outcome, message, [], null);

        public HolyricsResult ToResult() => new(Outcome, Message, Detail);
    }
}
