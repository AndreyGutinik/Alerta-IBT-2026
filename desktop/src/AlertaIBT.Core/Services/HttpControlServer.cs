using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AlertaIBT.Core.Http;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Services;

/// <summary>
/// The local control API that Holyrics calls when the sermon slide comes up: <c>127.0.0.1:8787</c>, the routes, the fallback
/// and the answers of the old Electron app (<c>startHttpControlServer</c> in <c>src/main.js</c> in the tag v7.0.0), so the Holyrics trigger keeps
/// working untouched. <c>/overlay/off</c> pauses (the sermon), <c>/overlay/on</c> resumes, <c>/overlay/status</c> reports.
/// </summary>
/// <remarks>
/// <para>
/// Answers are JSON <c>{"success":true,"overlayEnabled":true}</c> (<c>matched</c> is added by the fallback); an unknown route is
/// 404 <c>{"success":false,"error":"not_found","received":{...}}</c>; <c>OPTIONS</c> is 204 for any path (the preflight is answered
/// before the route is looked up). Every answer carries the CORS headers of the old app.
/// </para>
/// <para>
/// Listens only on the address <c>127.0.0.1</c>, like the old Node server. The registration is bound to that IP, so a request is
/// matched by the address it arrives at and not by its Host header: <c>http://localhost:8787/...</c> works too. As a second lock,
/// a request that does not come from the loopback address is refused with 403.
/// </para>
/// <para>
/// When the port is taken (usually by the old Alerta-IBT, still open) the error is logged with that hint, the status turns
/// to "porta ocupada", and the server tries again every <see cref="RetryInterval"/>, so closing the old app is enough.
/// </para>
/// </remarks>
public sealed class HttpControlServer : IDisposable
{
    /// <summary>The port of the old app, which Holyrics already calls.</summary>
    public const int DefaultPort = 8787;

    /// <summary>How long to wait before trying to take the port again.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly IOverlayControl _overlay;
    private readonly StatusHub? _hub;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();

    private HttpListener? _listener;
    private ITimer? _retry;
    private bool _disposed;
    private bool _reportedBusy;

    /// <param name="overlay">What the routes drive.</param>
    /// <param name="hub">Receives the state of the API; may be <c>null</c>.</param>
    /// <param name="logger">Receives the requests and the errors.</param>
    /// <param name="port">The port; 8787 in the app.</param>
    /// <param name="clock">Times the retries.</param>
    public HttpControlServer(IOverlayControl overlay, StatusHub? hub, ILogger logger, int port = DefaultPort, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        _overlay = overlay;
        _hub = hub;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        Port = port;
    }

    /// <summary>The port.</summary>
    public int Port { get; }

    /// <summary>Whether the server is listening.</summary>
    public bool IsListening
    {
        get
        {
            lock (_gate)
            {
                return _listener is { IsListening: true };
            }
        }
    }

    /// <summary>
    /// Starts listening. When the port is taken it logs the error with the hint and retries later by itself.
    /// </summary>
    /// <returns><c>true</c> when it is listening.</returns>
    public bool Start()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            if (_listener is { IsListening: true })
            {
                return true;
            }

            return TryListen();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        HttpListener? listener;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _retry?.Dispose();
            _retry = null;
            listener = _listener;
            _listener = null;
        }

        CloseQuietly(listener);
        _hub?.SetHttp(HttpApiState.Stopped);
    }

    // Caller holds _gate.
    private bool TryListen()
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            CloseQuietly(listener);
            OnStartFailed(ex);
            return false;
        }

        _listener = listener;
        _retry?.Dispose();
        _retry = null;
        _reportedBusy = false;
        _logger.LogInformation("API local ativa em http://127.0.0.1:{Port}", Port);
        _hub?.SetHttp(HttpApiState.Listening, $"127.0.0.1:{Port}");
        _ = Task.Run(() => AcceptLoopAsync(listener));
        return true;
    }

    // Caller holds _gate.
    private void OnStartFailed(HttpListenerException ex)
    {
        var detail = $"A porta {Port} esta ocupada ({ex.Message.Trim()}). Se o Alerta-IBT antigo (Electron) estiver aberto, feche-o: os dois juntos brigam pela porta e duplicam as mensagens do Telegram.";
        if (!_reportedBusy)
        {
            _reportedBusy = true;
            _logger.LogError("API local nao iniciou: {Detail}", detail);
        }

        _hub?.SetHttp(HttpApiState.PortBusy, detail);
        _retry ??= _clock.CreateTimer(_ => Retry(), null, RetryInterval, RetryInterval);
    }

    private void Retry()
    {
        try
        {
            if (Start())
            {
                _logger.LogInformation("A porta {Port} ficou livre: API local ativa", Port);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao tentar abrir a API local de novo");
        }
    }

    private async Task AcceptLoopAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                break; // closed (Dispose) or lost: Start() brings it back if it was not on purpose
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao aceitar um pedido da API local");
                continue;
            }

            _ = Task.Run(() => HandleAsync(context));
        }

        lock (_gate)
        {
            if (!_disposed && ReferenceEquals(_listener, listener))
            {
                // The listener died by itself (not through Dispose): forget it and try to open the port again.
                _listener = null;
                _hub?.SetHttp(HttpApiState.Stopped);
                _logger.LogWarning("A API local parou de ouvir; tentando abrir de novo");
                _retry ??= _clock.CreateTimer(_ => Retry(), null, TimeSpan.FromSeconds(1), RetryInterval);
            }
        }
    }

    private Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            if (!IsLocal(request.RemoteEndPoint))
            {
                _logger.LogWarning("Pedido recusado da API local: {Remote} nao e o proprio PC", request.RemoteEndPoint);
                Respond(context.Response, HttpStatusCode.Forbidden, new Dictionary<string, object?> { ["success"] = false, ["error"] = "forbidden" });
                return Task.CompletedTask;
            }

            var method = (request.HttpMethod ?? "GET").ToUpperInvariant();
            var rawUrl = request.RawUrl ?? "/";

            // The preflight is answered for any path, before the route is looked up.
            if (method == "OPTIONS")
            {
                Respond(context.Response, HttpStatusCode.NoContent, null);
                return Task.CompletedTask;
            }

            var intent = OverlayIntent.Parse(method, rawUrl);
            var matched = IsFallback(rawUrl, intent) ? intent == OverlayIntentKind.Off ? "fallback-off" : "fallback-on" : null;
            switch (intent)
            {
                case OverlayIntentKind.Off:
                    _logger.LogInformation("HTTP {Method} {Url}: pausar o overlay", method, rawUrl);
                    _overlay.Pause();
                    Respond(context.Response, HttpStatusCode.OK, Enabled(matched));
                    break;
                case OverlayIntentKind.On:
                    _logger.LogInformation("HTTP {Method} {Url}: retomar o overlay", method, rawUrl);
                    _overlay.Resume();
                    Respond(context.Response, HttpStatusCode.OK, Enabled(matched));
                    break;
                case OverlayIntentKind.Status:
                    _logger.LogDebug("HTTP {Method} {Url}", method, rawUrl);
                    Respond(context.Response, HttpStatusCode.OK, Enabled(null));
                    break;
                default:
                    _logger.LogInformation("HTTP {Method} {Url}: rota desconhecida", method, rawUrl);
                    Respond(
                        context.Response,
                        HttpStatusCode.NotFound,
                        new Dictionary<string, object?>
                        {
                            ["success"] = false,
                            ["error"] = "not_found",
                            ["received"] = new Dictionary<string, object?> { ["method"] = method, ["url"] = rawUrl },
                        });
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro na API local");
            _hub?.ReportError("API local", ex, _clock.GetUtcNow());
            try
            {
                Respond(context.Response, HttpStatusCode.InternalServerError, new Dictionary<string, object?> { ["success"] = false, ["error"] = ex.Message });
            }
            catch (Exception)
            {
                // The connection is gone; there is nobody left to answer.
            }
        }

        return Task.CompletedTask;
    }

    // Only the PC itself may call the API (a second lock: the listener is already bound to 127.0.0.1).
    internal static bool IsLocal(IPEndPoint? remote) => remote is not null && IPAddress.IsLoopback(remote.Address);

    // The exact routes of the old app are not "fallback"; anything else that still matched is.
    private static bool IsFallback(string rawUrl, OverlayIntentKind intent)
    {
        if (intent is OverlayIntentKind.Status or OverlayIntentKind.NotFound)
        {
            return false;
        }

        var path = rawUrl.Split('?')[0].TrimEnd('/');
        var exact = intent == OverlayIntentKind.Off ? "/overlay/off" : "/overlay/on";
        return path != exact;
    }

    private Dictionary<string, object?> Enabled(string? matched)
    {
        var body = new Dictionary<string, object?> { ["success"] = true, ["overlayEnabled"] = !_overlay.IsPaused };
        if (matched is not null)
        {
            body["matched"] = matched;
        }

        return body;
    }

    private static void Respond(HttpListenerResponse response, HttpStatusCode status, Dictionary<string, object?>? body)
    {
        try
        {
            response.StatusCode = (int)status;
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["Access-Control-Allow-Origin"] = "http://127.0.0.1";
            response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
            if (body is null)
            {
                response.ContentLength64 = 0;
            }
            else
            {
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, JsonOptions));
                response.ContentType = "application/json; charset=utf-8";
                response.ContentLength64 = bytes.Length;
                response.OutputStream.Write(bytes);
            }
        }
        finally
        {
            response.Close();
        }
    }

    private static void CloseQuietly(HttpListener? listener)
    {
        try
        {
            listener?.Close();
        }
        catch (Exception)
        {
            // Closing a listener that is already gone changes nothing.
        }
    }
}
