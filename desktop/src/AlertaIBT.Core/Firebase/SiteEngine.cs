using System.ComponentModel;
using System.Threading.Channels;
using System.Text.Json.Nodes;
using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Firebase;

/// <summary>What the engine needs to connect: the project and the PC account. Printing it never shows the password.</summary>
/// <param name="DatabaseUrl">The Realtime Database address.</param>
/// <param name="ApiKey">The web API key.</param>
/// <param name="Email">The e-mail of the PC account.</param>
/// <param name="Password">The password of the PC account.</param>
/// <param name="AuthEndpoints">Where the Auth services are; the real ones, or the emulator for a loopback database, when <c>null</c>.</param>
public sealed record SiteConfig(string DatabaseUrl, string ApiKey, string Email, string Password, FirebaseAuthEndpoints? AuthEndpoints = null)
{
    /// <summary>The Auth services to use: the given ones, the emulator on this computer for a loopback database, or the real ones.</summary>
    public FirebaseAuthEndpoints ResolveEndpoints()
    {
        if (AuthEndpoints is not null)
        {
            return AuthEndpoints;
        }

        return Uri.TryCreate(DatabaseUrl, UriKind.Absolute, out var uri) && uri.IsLoopback
            ? FirebaseAuthEndpoints.Emulator(uri.Host)
            : new FirebaseAuthEndpoints();
    }

    /// <inheritdoc />
    public override string ToString() => "SiteConfig { password hidden }";
}

/// <summary>A running connection to the site; <see cref="SiteConnector"/> starts one and stops it when the settings change.</summary>
public interface ISiteEngine
{
    /// <summary>Raised when the state changes (a short detail for a problem); on any thread. A handler must not throw.</summary>
    event Action<SiteState, string?>? StateChanged;

    /// <summary>Starts connecting in the background. Never throws.</summary>
    void Start();

    /// <summary>Stops everything and waits a little for it. Never throws.</summary>
    Task StopAsync();
}

/// <summary>
/// The PC side of the site (spec 9): signs in with the PC account, reads <c>/requests</c> by streaming, runs each pending request
/// through <see cref="AlertService.SubmitAsync"/> again from its raw text (the preview in the request is only a suggestion and is
/// never trusted), and writes back what happened to it: <c>queued</c> with its place, <c>showing</c>, <c>shown</c>,
/// <c>rejected</c> with the reason, <c>cancelled</c>, <c>expired</c>. It also publishes <c>/live</c> (what the status tab shows) on
/// every change and a heartbeat every 30 seconds, answers cancellations, and deletes requests older than 24 hours.
/// </summary>
/// <remarks>
/// <para>
/// The queue item gets the id of its request, so every later event of the queue (started, shown, expired, cancelled by the sermon,
/// removed at the PC) is found again and written to the same request. A request is handled once, however many times the stream
/// shows it. One that waited more than 30 minutes (the PC was off) is marked expired instead of shown. Nothing here blocks the
/// Telegram or the PC path, and an exception never leaves the engine: it goes to the log and to the status hub.
/// </para>
/// <para>The rules let only the PC write <c>status</c> and <c>live</c>, and only the owner or an admin set <c>cancel</c>; the PC trusts that.</para>
/// </remarks>
public sealed class SiteEngine : ISiteEngine, IDisposable
{
    /// <summary>How often the heartbeat is written (the site shows "PC offline" after 90 s without one).</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    /// <summary>The state turns from "reconnecting" to "offline" after this long without a connection.</summary>
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(60);

    /// <summary>A pending request older than this is expired, not shown (the people have gone).</summary>
    public static readonly TimeSpan MaxRequestAge = TimeSpan.FromMinutes(30);

    /// <summary>Requests older than this are deleted.</summary>
    public static readonly TimeSpan RequestRetention = TimeSpan.FromHours(24);

    /// <summary>Changes of the queue closer together than this are written once.</summary>
    public static readonly TimeSpan SyncDebounce = TimeSpan.FromMilliseconds(250);

    /// <summary>The wait before trying a refused login again (a wrong password does not fix itself, but the account may be fixed in the console).</summary>
    public static readonly TimeSpan LoginRetry = TimeSpan.FromMinutes(5);

    /// <summary>Notices per person per hour.</summary>
    public const int MaxPerHour = 10;

    private const int LiveQueueItems = 12;
    private const int LiveRecentItems = 10;

    private static readonly TimeSpan LimiterWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan CleanupEvery = TimeSpan.FromHours(1);

    private readonly SiteConfig _config;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly AlertService _alerts;
    private readonly StatusHub _hub;
    private readonly Func<AppSettings> _settings;
    private readonly string _version;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly TimeSpan _syncDebounce;
    private readonly PerPersonLimiter _limiter = new(MaxPerHour, LimiterWindow);

    private readonly Channel<string?> _work = Channel.CreateUnbounded<string?>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<bool> _sync = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly object _gate = new();
    private readonly HashSet<string> _processed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cancelWanted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Tracked> _tracked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StatusValue> _retry = new(StringComparer.Ordinal);

    private FirebaseAuthClient? _auth;
    private RealtimeDbClient? _db;
    private RtdbListener? _listener;
    private CancellationTokenSource? _stop;
    private Task? _run;
    private ITimer? _offlineTimer;
    private bool _offline;
    private SiteState _state = SiteState.NotConfigured;
    private int _started;
    private int _disposed;
    private bool _booted;
    private string? _lastLiveJson;
    private string? _nowId;
    private long _nowStartedAt;
    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;

    /// <param name="config">The project and the PC account.</param>
    /// <param name="http">The HTTP client; no timeout of its own (streams last for hours), redirects either way.</param>
    /// <param name="alerts">The alert service every request goes through.</param>
    /// <param name="hub">Where the LED, pause and Gemini state come from, and where errors are reported.</param>
    /// <param name="settings">Gives the current settings (the bar shown in the status tab).</param>
    /// <param name="version">The app version shown in <c>/live</c>.</param>
    /// <param name="logger">Receives what happened, without secrets.</param>
    /// <param name="clock">The source of time.</param>
    /// <param name="ownsHttp">Whether to dispose <paramref name="http"/> with the engine.</param>
    /// <param name="syncDebounce">How long changes are gathered before they are written; <see cref="SyncDebounce"/> by default.</param>
    public SiteEngine(
        SiteConfig config,
        HttpClient http,
        AlertService alerts,
        StatusHub hub,
        Func<AppSettings> settings,
        string version,
        ILogger logger,
        TimeProvider? clock = null,
        bool ownsHttp = false,
        TimeSpan? syncDebounce = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        _config = config;
        _http = http;
        _ownsHttp = ownsHttp;
        _alerts = alerts;
        _hub = hub;
        _settings = settings;
        _version = version ?? string.Empty;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _syncDebounce = syncDebounce ?? SyncDebounce;
    }

    /// <inheritdoc />
    public event Action<SiteState, string?>? StateChanged;

    /// <summary>Where the connection stands.</summary>
    public SiteState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <inheritdoc />
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _stop = new CancellationTokenSource();
        var stop = _stop.Token;
        _run = Task.Run(() => RunAsync(stop), CancellationToken.None);
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        var stop = _stop;
        if (stop is null)
        {
            return;
        }

        try
        {
            await stop.CancelAsync().ConfigureAwait(false);
            if (_run is { } run)
            {
                await run.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _logger.LogWarning("O site demorou para parar");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao parar o site");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        StopAsync().GetAwaiter().GetResult();
        _offlineTimer?.Dispose();
        _auth?.Dispose();
        _stop?.Dispose();
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private sealed class Tracked(string uid)
    {
        public string Uid { get; } = uid;

        public string? LastKey { get; set; }
    }

    // ---- connection ----

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            SetState(SiteState.Connecting, null);
            _auth = new FirebaseAuthClient(_http, _config.ApiKey, _config.ResolveEndpoints(), _clock, _logger);
            await SignInAsync(ct).ConfigureAwait(false);
            await RunSessionAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopping.
        }
        catch (Exception ex)
        {
            Fail("a conexao com o site", ex);
            SetState(SiteState.Offline, "Erro inesperado no site; veja o registro (log).");
        }
    }

    private async Task SignInAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                await _auth!.SignInAsync(_config.Email, _config.Password, ct).ConfigureAwait(false);
                return;
            }
            catch (FirebaseAuthException ex)
            {
                TimeSpan wait;
                if (!ex.IsTransient)
                {
                    DisarmOffline();
                    SetState(SiteState.AuthError, ex.Message);
                    _logger.LogWarning("Login do site recusado ({Failure})", ex.Failure);
                    wait = LoginRetry;
                }
                else
                {
                    MarkReconnecting(ex.Message);
                    _logger.LogWarning("Login do site falhou ({Failure}); tentando de novo", ex.Failure);
                    var backoff = RtdbListener.Backoff[Math.Min(attempt++, RtdbListener.Backoff.Count - 1)];
                    wait = ex.Failure == AuthFailure.RateLimited && backoff < TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : backoff;
                }

                await Task.Delay(wait, _clock, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task RunSessionAsync(CancellationToken ct)
    {
        _db = new RealtimeDbClient(_http, _config.DatabaseUrl, _auth!, _logger);
        _listener = new RtdbListener(_db, "requests", _clock, _logger);
        _listener.StateChanged += OnListenerState;
        _listener.Changed += OnRequestsChanged;
        _alerts.Queue.Changed += OnQueueChanged;
        _hub.PropertyChanged += OnHubChanged;
        try
        {
            // Publishes /live first of all: it clears whatever the last session left in "now".
            SignalSync();
            await Task.WhenAll(
                _listener.RunAsync(ct),
                WorkLoopAsync(ct),
                SyncLoopAsync(ct),
                HeartbeatLoopAsync(ct)).ConfigureAwait(false);
        }
        finally
        {
            _listener.StateChanged -= OnListenerState;
            _listener.Changed -= OnRequestsChanged;
            _alerts.Queue.Changed -= OnQueueChanged;
            _hub.PropertyChanged -= OnHubChanged;
            DisarmOffline();
        }
    }

    private void OnListenerState(ListenerState state, string? detail)
    {
        switch (state)
        {
            case ListenerState.Connecting:
                SetState(SiteState.Connecting, null);
                break;
            case ListenerState.Connected:
                DisarmOffline();
                SetState(SiteState.Online, null);
                SignalSync();
                break;
            case ListenerState.Reconnecting:
                MarkReconnecting(detail is null ? null : "Sem resposta do site (" + detail + "); tentando de novo.");
                break;
            case ListenerState.PermissionDenied:
                DisarmOffline();
                SetState(
                    SiteState.AuthError,
                    "O Firebase n\U000000E3o deixa o PC ler os pedidos. A conta do PC est\U000000E1 em /devices e as regras foram publicadas? Veja docs/SITE-COMO-PUBLICAR.md.");
                break;
        }
    }

    // The dot turns yellow and, if no connection comes back within a minute, red; a later failed attempt does not turn it yellow again.
    private void MarkReconnecting(string? detail)
    {
        bool offline;
        lock (_gate)
        {
            offline = _offline;
        }

        if (!offline)
        {
            SetState(SiteState.Reconnecting, detail);
            ArmOffline();
        }
    }

    private void ArmOffline()
    {
        lock (_gate)
        {
            if (_offlineTimer is not null || _offline)
            {
                return;
            }

            _offlineTimer = _clock.CreateTimer(_ => GoOffline(), null, OfflineAfter, Timeout.InfiniteTimeSpan);
        }
    }

    private void DisarmOffline()
    {
        lock (_gate)
        {
            _offlineTimer?.Dispose();
            _offlineTimer = null;
            _offline = false;
        }
    }

    private void GoOffline()
    {
        lock (_gate)
        {
            if (_offlineTimer is null)
            {
                return;
            }

            _offlineTimer.Dispose();
            _offlineTimer = null;
            _offline = true;
        }

        SetState(SiteState.Offline, null);
    }

    private void SetState(SiteState state, string? detail)
    {
        lock (_gate)
        {
            _state = state;
        }

        try
        {
            StateChanged?.Invoke(state, detail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao tratar o estado do site");
        }
    }

    // ---- requests ----

    // Called on the listener thread: only hands the ids over.
    private void OnRequestsChanged(RtdbChange change)
    {
        if (change.Reset)
        {
            _work.Writer.TryWrite(null);
            return;
        }

        foreach (var id in change.Children.Keys)
        {
            _work.Writer.TryWrite(id);
        }
    }

    private async Task WorkLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var id in _work.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    if (id is null)
                    {
                        await SweepAsync(ct).ConfigureAwait(false);
                    }
                    else if (_listener!.GetChild(id) is { } node)
                    {
                        await HandleAsync(id, node, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Fail("tratar um pedido do site", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    // Looks at every request there is: the first look after a connection, and the retry of what failed before.
    private async Task SweepAsync(CancellationToken ct)
    {
        var snapshot = _listener!.Snapshot();
        if (!_booted)
        {
            _booted = true;
            await BootAsync(snapshot, ct).ConfigureAwait(false);
        }

        foreach (var (id, node) in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            await HandleAsync(id, node, ct).ConfigureAwait(false);
        }

        lock (_gate)
        {
            // A request that is gone (deleted after 24 hours) is forgotten.
            _processed.RemoveWhere(id => !snapshot.ContainsKey(id));
        }
    }

    // First look at the requests of a session: rebuilds the hourly counts, picks up what the last session left half way, and
    // deletes what is old.
    private async Task BootAsync(IReadOnlyDictionary<string, JsonNode?> snapshot, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        foreach (var (id, node) in snapshot)
        {
            if (!SiteRequest.TryParse(id, node, out var request) || request.State == "pending")
            {
                continue;
            }

            if (request.CreatedAt > 0 && !string.Equals(ReasonOf(node), SiteContract.RateLimited, StringComparison.Ordinal))
            {
                _limiter.Seed(request.Uid, DateTimeOffset.FromUnixTimeMilliseconds(request.CreatedAt), now);
            }

            if (request.State is SiteContract.Queued or SiteContract.Showing)
            {
                await RecoverAsync(request, ct).ConfigureAwait(false);
            }
        }

        await CleanupAsync(snapshot, ct).ConfigureAwait(false);
    }

    private static string? ReasonOf(JsonNode? node) =>
        node?["status"]?["reason"] is JsonValue value && value.TryGetValue<string>(out var reason) ? reason : null;

    // A request the last session left "queued" or "showing": the queue is saved on disk, so it may still be there.
    private async Task RecoverAsync(SiteRequest request, CancellationToken ct)
    {
        var snap = _alerts.Queue.ToSnapshot();
        var known = snap.Pending.Any(i => i.Id == request.Id) || snap.History.Any(h => h.Item.Id == request.Id);
        if (known)
        {
            Track(request.Id, request.Uid);
            SignalSync();
            return;
        }

        await WriteStatusAsync(request.Id, new StatusValue(SiteContract.Expired), ct).ConfigureAwait(false);
    }

    private async Task HandleAsync(string id, JsonNode? node, CancellationToken ct)
    {
        if (!SiteRequest.TryParse(id, node, out var request))
        {
            _logger.LogDebug("Pedido {Id} do site ignorado: formato inesperado", id);
            return;
        }

        // A cancellation asked while the request is queued.
        if (request.Cancel)
        {
            lock (_gate)
            {
                if (_inFlight.Contains(id))
                {
                    _cancelWanted.Add(id);
                }
            }

            if (_alerts.Queue.Cancel(id))
            {
                _logger.LogInformation("Pedido {Id} cancelado pelo site", id);
            }
        }

        if (request.State != "pending")
        {
            if (request.State is SiteContract.Queued or SiteContract.Showing && !IsTracked(id) && _booted)
            {
                await RecoverAsync(request, ct).ConfigureAwait(false);
            }

            return;
        }

        lock (_gate)
        {
            if (!_processed.Add(id))
            {
                return;
            }

            _inFlight.Add(id);
        }

        var finished = false;
        try
        {
            finished = await ProcessAsync(request, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _inFlight.Remove(id);
                _cancelWanted.Remove(id);
                if (!finished)
                {
                    // Not answered (no connection, or stopping): the next look at the requests tries again.
                    _processed.Remove(id);
                }
            }
        }
    }

    // True when the request was answered (or queued), false when it should be tried again.
    private async Task<bool> ProcessAsync(SiteRequest request, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (request.Cancel)
        {
            await WriteStatusAsync(request.Id, new StatusValue(SiteContract.Cancelled), ct).ConfigureAwait(false);
            return true;
        }

        if (request.CreatedAt > 0 && now - DateTimeOffset.FromUnixTimeMilliseconds(request.CreatedAt) > MaxRequestAge)
        {
            _logger.LogInformation("Pedido {Id} do site expirou antes de passar", request.Id);
            await WriteStatusAsync(request.Id, new StatusValue(SiteContract.Expired), ct).ConfigureAwait(false);
            return true;
        }

        JsonNode? user;
        try
        {
            user = await _db!.GetAsync("users/" + request.Uid, ct).ConfigureAwait(false);
        }
        catch (RtdbException ex)
        {
            _logger.LogWarning("Nao foi possivel ler o usuario do pedido {Id}: {Failure}", request.Id, ex.Failure);
            return false;
        }

        if (user is not JsonObject record || (record["blocked"] is JsonValue blocked && blocked.TryGetValue<bool>(out var isBlocked) && isBlocked))
        {
            await WriteStatusAsync(request.Id, new StatusValue(SiteContract.Rejected, Reason: SiteContract.AccessRemoved), ct).ConfigureAwait(false);
            return true;
        }

        var role = record["role"] is JsonValue r && r.TryGetValue<string>(out var roleName) && roleName == "admin" ? SenderRole.Admin : SenderRole.Member;
        if (role == SenderRole.Member && !_limiter.TryAcquire(request.Uid, now))
        {
            _logger.LogInformation("Pedido {Id} recusado: limite por hora", request.Id);
            await WriteStatusAsync(request.Id, new StatusValue(SiteContract.Rejected, Reason: SiteContract.RateLimited), ct).ConfigureAwait(false);
            return true;
        }

        SubmitOutcome outcome;
        try
        {
            // The pipeline starts from the raw text: whatever "preview" says is ignored.
            outcome = await _alerts.SubmitAsync(request.Raw, AlertSource.Site, role, request.Urgent, request.Uid, ct, request.Id).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            Fail("corrigir um aviso do site", ex);
            await WriteStatusAsync(request.Id, new StatusValue(SiteContract.Rejected, Reason: SiteContract.PcError), ct).ConfigureAwait(false);
            return true;
        }

        switch (outcome.Kind)
        {
            case SubmitKind.Queued:
                Track(request.Id, request.Uid);
                bool cancel;
                lock (_gate)
                {
                    cancel = _cancelWanted.Remove(request.Id);
                }

                if (cancel)
                {
                    _alerts.Queue.Cancel(request.Id);
                }

                SignalSync();
                break;

            case SubmitKind.Rejected:
                await WriteStatusAsync(
                    request.Id,
                    new StatusValue(SiteContract.Rejected, Reason: HistoryReasons.MissingFields(outcome.Missing), Text: OrNull(outcome.Text)),
                    ct).ConfigureAwait(false);
                break;

            case SubmitKind.Refused:
                await WriteStatusAsync(request.Id, new StatusValue(SiteContract.Rejected, Reason: HistoryReasons.ImproperContent), ct).ConfigureAwait(false);
                break;

            case SubmitKind.TooLong:
                await WriteStatusAsync(request.Id, new StatusValue(SiteContract.Rejected, Reason: HistoryReasons.TooLong, Text: OrNull(outcome.Text)), ct).ConfigureAwait(false);
                break;

            case SubmitKind.Duplicate:
                await WriteStatusAsync(request.Id, new StatusValue(SiteContract.Rejected, Reason: SiteContract.Duplicate, Text: OrNull(outcome.Text)), ct).ConfigureAwait(false);
                break;

            case SubmitKind.PausedRefused:
                await WriteStatusAsync(request.Id, new StatusValue(SiteContract.Rejected, Reason: HistoryReasons.Sermon), ct).ConfigureAwait(false);
                break;
        }

        return true;
    }

    private static string? OrNull(string text) => string.IsNullOrEmpty(text) ? null : text;

    private bool IsTracked(string id)
    {
        lock (_gate)
        {
            return _tracked.ContainsKey(id);
        }
    }

    private void Track(string id, string uid)
    {
        lock (_gate)
        {
            _tracked.TryAdd(id, new Tracked(uid));
        }
    }

    // Writes a status; a failure is kept and tried again at the next heartbeat (never lost, never thrown).
    private async Task WriteStatusAsync(string id, StatusValue status, CancellationToken ct)
    {
        try
        {
            await _db!.PutAsync($"requests/{id}/status", status.ToJson(_clock.GetUtcNow().ToUnixTimeMilliseconds()), ct).ConfigureAwait(false);
            lock (_gate)
            {
                _retry.Remove(id);
            }
        }
        catch (RtdbException ex) when (ex.Failure == RtdbFailure.PermissionDenied)
        {
            // The request was deleted meanwhile (the rules refuse a status without a request) or the PC is not registered: no use in trying again.
            _logger.LogWarning("O banco recusou o status do pedido {Id}", id);
            lock (_gate)
            {
                _retry.Remove(id);
            }
        }
        catch (RtdbException ex)
        {
            _logger.LogWarning("Nao foi possivel escrever o status do pedido {Id}: {Failure}", id, ex.Failure);
            lock (_gate)
            {
                _retry[id] = status;
            }
        }
    }

    // ---- statuses and /live ----

    private void OnQueueChanged(object? sender, EventArgs e) => SignalSync();

    private void OnHubChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StatusHub.Led) or nameof(StatusHub.LedPresent) or nameof(StatusHub.Paused) or nameof(StatusHub.Gemini)
            or nameof(StatusHub.GeminiState))
        {
            SignalSync();
        }
    }

    private void SignalSync() => _sync.Writer.TryWrite(true);

    private async Task SyncLoopAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await _sync.Reader.ReadAsync(ct).ConfigureAwait(false);

                // A burst of changes is written once.
                await Task.Delay(_syncDebounce, _clock, ct).ConfigureAwait(false);
                _sync.Reader.TryRead(out _);
                try
                {
                    await SyncStatusesAsync(ct).ConfigureAwait(false);
                    await PublishLiveAsync(heartbeatOnly: false, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Fail("publicar o estado no site", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    // Brings the status of every request that is in the queue (or just left it) up to date.
    private async Task SyncStatusesAsync(CancellationToken ct)
    {
        KeyValuePair<string, Tracked>[] tracked;
        lock (_gate)
        {
            tracked = _tracked.ToArray();
        }

        if (tracked.Length == 0)
        {
            return;
        }

        var snap = _alerts.Queue.ToSnapshot();
        var currentId = _alerts.Queue.Current?.Id;
        var firstIsCurrent = snap.Pending.Count > 0 && currentId is not null && snap.Pending[0].Id == currentId;
        foreach (var (id, track) in tracked)
        {
            var (status, terminal) = DesiredStatus(id, snap, firstIsCurrent);
            if (status.Key == track.LastKey)
            {
                continue;
            }

            try
            {
                await _db!.PutAsync($"requests/{id}/status", status.ToJson(_clock.GetUtcNow().ToUnixTimeMilliseconds()), ct).ConfigureAwait(false);
                track.LastKey = status.Key;
                if (terminal)
                {
                    lock (_gate)
                    {
                        _tracked.Remove(id);
                    }
                }
            }
            catch (RtdbException ex) when (ex.Failure == RtdbFailure.PermissionDenied)
            {
                _logger.LogWarning("O banco recusou o status do pedido {Id}; deixando de acompanhar", id);
                lock (_gate)
                {
                    _tracked.Remove(id);
                }
            }
            catch (RtdbException ex)
            {
                // Left as it was: the next change or heartbeat writes it again.
                _logger.LogWarning("Nao foi possivel escrever o status do pedido {Id}: {Failure}", id, ex.Failure);
            }
        }
    }

    private static (StatusValue Status, bool Terminal) DesiredStatus(string id, QueueSnapshot snap, bool firstIsCurrent)
    {
        var index = -1;
        for (var i = 0; i < snap.Pending.Count; i++)
        {
            if (snap.Pending[i].Id == id)
            {
                index = i;
                break;
            }
        }

        if (index >= 0)
        {
            var item = snap.Pending[index];
            if (firstIsCurrent && index == 0)
            {
                return (new StatusValue(SiteContract.Showing, Text: item.Text), false);
            }

            return (new StatusValue(SiteContract.Queued, Position: firstIsCurrent ? index : index + 1, Text: item.Text), false);
        }

        var entry = snap.History.FirstOrDefault(h => h.Item.Id == id);
        if (entry is not null)
        {
            return (
                new StatusValue(
                    SiteContract.StateOf(entry.State),
                    Reason: SiteContract.ReasonOf(entry),
                    Text: entry.State is HistoryState.Shown or HistoryState.Rejected ? OrNull(entry.Item.Text) : null,
                    At: entry.At.ToUnixTimeMilliseconds()),
                true);
        }

        // In neither place: pushed out of the history, or lost. It will not pass any more.
        return (new StatusValue(SiteContract.Expired), true);
    }

    private JsonObject BuildLive()
    {
        var snap = _alerts.Queue.ToSnapshot();
        var currentId = _alerts.Queue.Current?.Id;
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var pending = snap.Pending.ToList();
        QueueItem? current = null;
        if (pending.Count > 0 && currentId is not null && pending[0].Id == currentId)
        {
            current = pending[0];
            pending.RemoveAt(0);
        }

        if (current?.Id != _nowId)
        {
            _nowId = current?.Id;
            _nowStartedAt = now;
        }

        var bar = _settings().Bar;
        var barJson = new JsonObject { ["position"] = bar.Position, ["height"] = bar.Height, ["speed"] = bar.Speed };
        if (bar.FontSize is { } fontSize)
        {
            barJson["fontSize"] = fontSize;
        }

        var queue = new JsonArray();
        foreach (var item in pending.Take(LiveQueueItems))
        {
            var entry = new JsonObject { ["id"] = item.Id, ["text"] = Clip(item.Text), ["source"] = SourceName(item.Source) };
            if (item.Urgent)
            {
                entry["urgent"] = true;
            }

            if (item.Plate is not null)
            {
                entry["plate"] = item.Plate;
            }

            queue.Add(entry);
        }

        var recent = new JsonArray();
        foreach (var history in snap.History.Take(LiveRecentItems))
        {
            var entry = new JsonObject
            {
                ["id"] = history.Item.Id,
                ["text"] = Clip(history.Item.Text),
                ["state"] = SiteContract.StateOf(history.State),
                ["at"] = history.At.ToUnixTimeMilliseconds(),
            };
            if (history.Item.Plate is not null)
            {
                entry["plate"] = history.Item.Plate;
            }

            recent.Add(entry);
        }

        var live = new JsonObject
        {
            ["heartbeatAt"] = ServerTimestamp(),
            ["version"] = _version,
            ["led"] = _hub.LedPresent,
            ["paused"] = _alerts.Queue.IsPaused,
            ["gemini"] = _hub.GeminiState.ToString().ToLowerInvariant(),
            ["queue"] = queue,
            ["recent"] = recent,
            ["bar"] = barJson,
        };
        if (current is not null)
        {
            var nowJson = new JsonObject
            {
                ["id"] = current.Id,
                ["text"] = Clip(current.Text),
                ["source"] = SourceName(current.Source),
                ["startedAt"] = _nowStartedAt,
            };
            if (current.Plate is not null)
            {
                nowJson["plate"] = current.Plate;
            }

            live["now"] = nowJson;
        }

        return live;
    }

    private static JsonObject ServerTimestamp() => new() { [".sv"] = "timestamp" };

    private static string Clip(string text) => text.Length <= 400 ? text : text[..400];

    private static string SourceName(AlertSource source) => source.ToString().ToLowerInvariant();

    // The whole /live when something changed (or it was never written); otherwise only the heartbeat.
    private async Task PublishLiveAsync(bool heartbeatOnly, CancellationToken ct)
    {
        var live = BuildLive();
        var withoutHeartbeat = live.DeepClone().AsObject();
        withoutHeartbeat.Remove("heartbeatAt");
        var json = withoutHeartbeat.ToJsonString();
        try
        {
            if (_lastLiveJson is not null && json == _lastLiveJson)
            {
                if (heartbeatOnly)
                {
                    await _db!.PatchAsync("live", new JsonObject { ["heartbeatAt"] = ServerTimestamp() }, ct).ConfigureAwait(false);
                }

                return;
            }

            await _db!.PutAsync("live", live, ct).ConfigureAwait(false);
            _lastLiveJson = json;
        }
        catch (RtdbException ex)
        {
            _lastLiveJson = null;
            _logger.LogWarning("Nao foi possivel publicar o estado no site: {Failure}", ex.Failure);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(HeartbeatInterval, _clock);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await PublishLiveAsync(heartbeatOnly: true, ct).ConfigureAwait(false);
                    await RetryStatusesAsync(ct).ConfigureAwait(false);

                    // The requests are looked at again: what could not be answered earlier is tried again.
                    _work.Writer.TryWrite(null);
                    SignalSync();
                    await CleanupIfDueAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Fail("o batimento do site", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private async Task RetryStatusesAsync(CancellationToken ct)
    {
        KeyValuePair<string, StatusValue>[] retry;
        lock (_gate)
        {
            retry = _retry.ToArray();
        }

        foreach (var (id, status) in retry)
        {
            await WriteStatusAsync(id, status, ct).ConfigureAwait(false);
        }
    }

    private async Task CleanupIfDueAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (_listener is null || !_booted || now - _lastCleanup < CleanupEvery)
        {
            return;
        }

        await CleanupAsync(_listener.Snapshot(), ct).ConfigureAwait(false);
    }

    // Deletes the requests older than 24 hours.
    private async Task CleanupAsync(IReadOnlyDictionary<string, JsonNode?> snapshot, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        _lastCleanup = now;
        foreach (var (id, node) in snapshot)
        {
            if (!SiteRequest.TryParse(id, node, out var request) || request.CreatedAt <= 0
                || now - DateTimeOffset.FromUnixTimeMilliseconds(request.CreatedAt) <= RequestRetention || IsTracked(id))
            {
                continue;
            }

            try
            {
                await _db!.DeleteAsync("requests/" + id, ct).ConfigureAwait(false);
                _logger.LogInformation("Pedido antigo {Id} apagado", id);
            }
            catch (RtdbException ex)
            {
                _logger.LogWarning("Nao foi possivel apagar o pedido antigo {Id}: {Failure}", id, ex.Failure);
            }
        }
    }

    private void Fail(string what, Exception ex)
    {
        try
        {
            _logger.LogError(ex, "Erro ao tratar {What}", what);
            _hub.ReportError("Site", ex, _clock.GetUtcNow());
        }
        catch (Exception)
        {
            // Reporting an error must never raise another one.
        }
    }
}
