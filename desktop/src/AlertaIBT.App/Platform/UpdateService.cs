using System.Globalization;
using System.IO;
using System.Net.Http;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace AlertaIBT.App.Platform;

/// <summary>An update found on the release feed. <see cref="Handle"/> is whatever the client needs to download and apply it.</summary>
/// <param name="Version">The version, for logs and for the attempt counter.</param>
/// <param name="Handle">Opaque to everybody but the client that made it.</param>
public sealed record UpdateOffer(string Version, object Handle);

/// <summary>The update system behind <see cref="UpdateService"/> (Velopack in the app, a fake in tests).</summary>
public interface IUpdateClient
{
    /// <summary>The app runs from a Velopack install (a development build or a copied folder does not).</summary>
    bool IsInstalled { get; }

    /// <summary>The running version.</summary>
    string CurrentVersion { get; }

    /// <summary>An update that was downloaded before and is waiting to be applied, or null.</summary>
    UpdateOffer? Pending { get; }

    /// <summary>Asks the feed for a newer version; null when there is none.</summary>
    Task<UpdateOffer?> CheckAsync(CancellationToken cancellationToken);

    /// <summary>Downloads the package of <paramref name="offer"/> to the local packages folder, reporting 0 to 100.</summary>
    Task DownloadAsync(UpdateOffer offer, IProgress<int>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Starts the updater, which waits for this process to leave, installs <paramref name="offer"/> and starts the app again with
    /// <paramref name="restartArguments"/>. It does NOT exit: the caller leaves through the app's normal shutdown.
    /// </summary>
    void ApplyAndRestart(UpdateOffer offer, string[] restartArguments);
}

/// <summary>Velopack over the GitHub Releases of the project.</summary>
public sealed class VelopackUpdateClient : IUpdateClient
{
    /// <summary>The repository whose Releases carry the installer and the updates.</summary>
    public const string RepositoryUrl = "https://github.com/AndreyGutinik/Alerta-IBT-2026";

    /// <summary>
    /// Whether the app also sees the releases marked "pre-release" (every release of the v8 line is one for now). Velopack takes the
    /// highest version among what it sees (8.0.1-pre.1 is above 8.0.0-pre.5, and 8.0.1 above 8.0.1-pre.N). A stable-only channel
    /// later is this one value turned to <c>false</c>.
    /// </summary>
    public const bool IncludePreReleases = true;

    private readonly Lazy<UpdateManager?> _manager;

    /// <param name="repositoryUrl">The GitHub repository; <see cref="RepositoryUrl"/> in the app.</param>
    public VelopackUpdateClient(string repositoryUrl = RepositoryUrl)
    {
        _manager = new Lazy<UpdateManager?>(() =>
        {
            try
            {
                var manager = new UpdateManager(CreateSource(repositoryUrl));
                return manager.IsInstalled ? manager : null;
            }
            catch (Exception)
            {
                return null; // not an installed copy: nothing to update
            }
        });
    }

    /// <inheritdoc />
    public bool IsInstalled => _manager.Value is not null;

    /// <inheritdoc />
    public string CurrentVersion => _manager.Value?.CurrentVersion?.ToString()
        ?? System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
        ?? "0.0.0";

    /// <inheritdoc />
    public UpdateOffer? Pending => _manager.Value?.UpdatePendingRestart is { } asset
        ? new UpdateOffer(asset.Version.ToString(), asset)
        : null;

    /// <summary>The release feed: the GitHub Releases of <paramref name="repositoryUrl"/>, anonymous (the repository is public).</summary>
    /// <param name="repositoryUrl">The GitHub repository.</param>
    /// <param name="includePreReleases">Whether pre-releases count; <see cref="IncludePreReleases"/> in the app.</param>
    public static GithubSource CreateSource(string repositoryUrl, bool includePreReleases = IncludePreReleases) =>
        new(repositoryUrl, accessToken: null, prerelease: includePreReleases);

    /// <inheritdoc />
    public async Task<UpdateOffer?> CheckAsync(CancellationToken cancellationToken)
    {
        var manager = RequireManager();
        var info = await manager.CheckForUpdatesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        return info is null ? null : new UpdateOffer(info.TargetFullRelease.Version.ToString(), info);
    }

    /// <inheritdoc />
    public Task DownloadAsync(UpdateOffer offer, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);

        return RequireManager().DownloadUpdatesAsync((UpdateInfo)offer.Handle, progress is null ? null : progress.Report, cancellationToken);
    }

    /// <inheritdoc />
    public void ApplyAndRestart(UpdateOffer offer, string[] restartArguments)
    {
        ArgumentNullException.ThrowIfNull(offer);

        var asset = offer.Handle as VelopackAsset ?? ((UpdateInfo)offer.Handle).TargetFullRelease;

        // The updater waits for THIS process to leave (so the app shuts down the normal way and the single-instance lock is
        // released), installs, and starts the app again with the same arguments. Silent: no window of its own, which could land on the LED.
        RequireManager().WaitExitThenApplyUpdates(asset, silent: true, restart: true, restartArguments);
    }

    private UpdateManager RequireManager() => _manager.Value ?? throw new InvalidOperationException("Not an installed copy.");
}

/// <summary>What one look at the release feed ended in.</summary>
public enum UpdateCheckResult
{
    /// <summary>A development build or a copied folder: updates do not apply.</summary>
    NotInstalled,

    /// <summary>The automatic update is switched off and the look was not asked for by hand.</summary>
    Disabled,

    /// <summary>The newest release is the one running.</summary>
    UpToDate,

    /// <summary>A newer version is downloaded: installed at once when the app is idle, otherwise as soon as it becomes idle.</summary>
    Downloaded,

    /// <summary>The feed could not be read or the download failed (an offline church PC is normal); it is tried again later.</summary>
    Failed,
}

/// <summary>
/// Automatic updates from the GitHub Releases. The app opens, waits 5 seconds, looks at the feed and, when a newer version is
/// there, downloads it and installs it, restarting itself, as soon as it is idle (nothing queued, nothing on the bar, no sermon
/// pause, no send in a panel; see <see cref="IUpdateHost.IsIdle"/>). A busy app keeps the package pending and installs it at the next
/// idle moment (the host's signals, and a poll every 15 seconds as a net), and in any case at the next start. It looks again every
/// 30 minutes, so a PC left on for days still updates.
/// </summary>
/// <remarks>
/// Nothing in here can throw into the interface: offline is normal and only logged. A restart that keeps failing cannot loop: the
/// same version is attempted at most twice in 24 hours (<see cref="UpdateAttemptPolicy"/>, remembered in the settings). With the
/// switch off in Configurações nothing happens by itself; the "Verificar agora" button still works.
/// </remarks>
public sealed class UpdateService : IUpdateStatus, IDisposable
{
    /// <summary>The wait after the start before the first look, so the app settles first.</summary>
    public static readonly TimeSpan DefaultFirstCheckDelay = TimeSpan.FromSeconds(5);

    /// <summary>The wait between looks while the app runs.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(30);

    /// <summary>How often a pending update looks for the idle moment when no signal came (the signals are the fast path).</summary>
    public static readonly TimeSpan DefaultIdlePoll = TimeSpan.FromSeconds(15);

    /// <summary>The longest wait for the feed.</summary>
    public static readonly TimeSpan DefaultCheckTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The longest wait for the download.</summary>
    public static readonly TimeSpan DefaultDownloadTimeout = TimeSpan.FromMinutes(15);

    private readonly IUpdateClient _client;
    private readonly IUpdateHost _host;
    private readonly SettingsService _settings;
    private readonly ILogger<UpdateService> _logger;
    private readonly TimeProvider _time;
    private readonly string? _markerFile;
    private readonly TimeSpan _firstCheckDelay;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _idlePoll;
    private readonly TimeSpan _checkTimeout;
    private readonly TimeSpan _downloadTimeout;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _work = new(1, 1);
    private readonly object _gate = new();

    private string[] _arguments = [];
    private UpdateState? _state;
    private UpdateOffer? _pending;
    private bool _manualRequested;
    private bool _idleWatching;
    private bool _started;
    private bool _lastAutoUpdate;
    private string? _blockedVersion;
    private DateTimeOffset _blockedUntil;
    private int _applying;

    /// <param name="client">The update system.</param>
    /// <param name="host">The running app: idle or not, saving, leaving.</param>
    /// <param name="settings">The settings: the switch and the attempt counter.</param>
    /// <param name="logger">Where outcomes are logged.</param>
    /// <param name="time">The clock and the timers; a fake in tests.</param>
    /// <param name="restartMarkerFile">The file <see cref="UpdateRestartMarker"/> uses (null in tests that do not need it).</param>
    /// <param name="firstCheckDelay">Wait before the first look (default 5 seconds).</param>
    /// <param name="interval">Wait between looks (default 30 minutes).</param>
    /// <param name="idlePoll">Wait between looks for the idle moment of a pending update (default 15 seconds).</param>
    /// <param name="checkTimeout">Longest wait for the feed (default 30 seconds).</param>
    /// <param name="downloadTimeout">Longest wait for the download (default 15 minutes).</param>
    public UpdateService(
        IUpdateClient client,
        IUpdateHost host,
        SettingsService settings,
        ILogger<UpdateService> logger,
        TimeProvider? time = null,
        string? restartMarkerFile = null,
        TimeSpan? firstCheckDelay = null,
        TimeSpan? interval = null,
        TimeSpan? idlePoll = null,
        TimeSpan? checkTimeout = null,
        TimeSpan? downloadTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        _client = client;
        _host = host;
        _settings = settings;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _markerFile = restartMarkerFile;
        _firstCheckDelay = firstCheckDelay ?? DefaultFirstCheckDelay;
        _interval = interval ?? DefaultInterval;
        _idlePoll = idlePoll ?? DefaultIdlePoll;
        _checkTimeout = checkTimeout ?? DefaultCheckTimeout;
        _downloadTimeout = downloadTimeout ?? DefaultDownloadTimeout;
        _lastAutoUpdate = settings.Current.System.AutoUpdate;
    }

    /// <inheritdoc />
    public event Action<UpdateState>? StateChanged;

    /// <inheritdoc />
    public string CurrentVersion
    {
        get
        {
            try
            {
                return _client.CurrentVersion;
            }
            catch (Exception)
            {
                return "?";
            }
        }
    }

    /// <inheritdoc />
    public UpdateState State
    {
        get
        {
            lock (_gate)
            {
                if (_state is not null)
                {
                    return _state;
                }
            }

            return new UpdateState(UpdatePhase.Idle, null, InitialResult());
        }
    }

    private bool AutoUpdateOn => _settings.Current.System.AutoUpdate;

    /// <summary>
    /// Installs an update downloaded earlier: the updater is started and the caller must exit at once (the updater waits for that).
    /// Returns true when the restart is under way. Does nothing with the switch off. Never more than twice for one version in 24
    /// hours. Call it only after the single-instance lock is held, so a second launch of a running app never restarts it. Never throws.
    /// </summary>
    public bool TryApplyPendingAtStartup(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        try
        {
            _arguments = arguments;
            if (!_client.IsInstalled)
            {
                return false;
            }

            if (!AutoUpdateOn)
            {
                return false;
            }

            var pending = _client.Pending;
            if (pending is null)
            {
                return false;
            }

            if (!TryRecordAttempt(pending.Version))
            {
                _logger.LogWarning("A atualizacao {Version} ja foi tentada o maximo de vezes em 24 h; seguindo com a versao {Current}", pending.Version, _client.CurrentVersion);
                return false;
            }

            _logger.LogInformation("Aplicando a atualizacao {Version} baixada antes (versao atual {Current})", pending.Version, _client.CurrentVersion);
            WriteMarker();
            _client.ApplyAndRestart(pending, arguments);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nao foi possivel aplicar a atualizacao baixada; seguindo com a versao atual");
            return false;
        }
    }

    /// <summary>
    /// Starts the background work: the first look 5 seconds from now and then every 30 minutes, and the watch for the idle moment.
    /// Returns at once; never throws.
    /// </summary>
    /// <param name="arguments">The command line of this run, given back to the app when it is restarted.</param>
    public void Start(string[] arguments)
    {
        try
        {
            _arguments = arguments ?? [];
            _logger.LogInformation("IBT Controller {Version} (instalado: {Installed})", _client.CurrentVersion, _client.IsInstalled);
            if (!_client.IsInstalled)
            {
                return; // development build: there is nothing to update
            }

            lock (_gate)
            {
                if (_started)
                {
                    return;
                }

                _started = true;
            }

            _host.Attach(OnMaybeIdle);
            _settings.Changed += OnSettingsChanged;

            // A package kept from an earlier run (the app was busy, or offline now): no network needed to finish the job.
            if (AutoUpdateOn && _client.Pending is { } pending)
            {
                lock (_gate)
                {
                    _pending = pending;
                }

                Publish(UpdatePhase.WaitingForIdle, null, WaitingText(pending.Version));
                EnsureIdleWatch();
                OnMaybeIdle();
            }

            // Runs up to its first wait right here, so the timer exists when this returns.
            _ = RunAsync(_stop.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nao foi possivel iniciar a verificacao de atualizacoes");
        }
    }

    /// <inheritdoc />
    public Task CheckNowAsync(CancellationToken cancellationToken) => CheckAsync(cancellationToken);

    /// <summary>
    /// One look at the feed, then the download when something newer is there, then the installation when the app is idle (or as
    /// soon as it is). Never throws (except to honor <paramref name="cancellationToken"/>).
    /// </summary>
    /// <param name="cancellationToken">Stops the look.</param>
    /// <param name="manual">Asked for by the button: works with the switch off, and an update found is installed when idle.</param>
    public async Task<UpdateCheckResult> CheckOnceAsync(CancellationToken cancellationToken, bool manual = false)
    {
        try
        {
            if (!_client.IsInstalled)
            {
                Publish(UpdatePhase.Idle, null, NotInstalledText);
                return UpdateCheckResult.NotInstalled;
            }

            if (!manual && !AutoUpdateOn)
            {
                return UpdateCheckResult.Disabled;
            }

            if (Volatile.Read(ref _applying) != 0)
            {
                return UpdateCheckResult.Downloaded; // already leaving to install
            }
        }
        catch (Exception ex)
        {
            LogFailure(ex);
            return UpdateCheckResult.Failed;
        }

        await _work.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (manual)
            {
                lock (_gate)
                {
                    _manualRequested = true;
                }

                Publish(UpdatePhase.Checking, null, "Verificando\U00002026");
            }

            UpdateOffer? offer;
            try
            {
                offer = await CheckWithTimeoutAsync(cancellationToken).ConfigureAwait(false);
                if (offer is null)
                {
                    Publish(UpdatePhase.Idle, null, UpToDateText());
                    return UpdateCheckResult.UpToDate;
                }

                if (!string.Equals(_client.Pending?.Version, offer.Version, StringComparison.Ordinal))
                {
                    await DownloadAsync(offer, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Atualizacao {Version} baixada", offer.Version);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Offline, GitHub down, a half download: all normal. One line, no stack, tried again later.
                LogFailure(ex);
                UpdateOffer? waiting;
                lock (_gate)
                {
                    waiting = _pending;
                }

                if (Volatile.Read(ref _applying) == 0)
                {
                    // A package kept from before is still the news; otherwise say why the look failed.
                    if (waiting is not null)
                    {
                        Publish(UpdatePhase.WaitingForIdle, null, WaitingText(waiting.Version));
                    }
                    else
                    {
                        Publish(UpdatePhase.Idle, null, FailedText(ex));
                    }
                }

                return UpdateCheckResult.Failed;
            }

            lock (_gate)
            {
                _pending = offer;
            }

            if (Volatile.Read(ref _applying) == 0)
            {
                Publish(UpdatePhase.WaitingForIdle, null, WaitingText(offer.Version));
            }

            EnsureIdleWatch();
        }
        finally
        {
            _work.Release();
        }

        TryApplyIfIdle();
        return UpdateCheckResult.Downloaded;
    }

    /// <summary>Stops the background work.</summary>
    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _stop.Cancel();
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CheckOnceAsync(cancellationToken, manual: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller gave up: nothing to report.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erro ao verificar atualizacoes");
        }
    }

    private async Task<UpdateOffer?> CheckWithTimeoutAsync(CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_checkTimeout);
        try
        {
            return await _client.CheckAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("A busca por atualizacoes demorou demais.");
        }
    }

    private async Task DownloadAsync(UpdateOffer offer, CancellationToken cancellationToken)
    {
        Publish(UpdatePhase.Downloading, DownloadingNotice(offer.Version, 0));
        var progress = new InlineProgress(percent => Publish(UpdatePhase.Downloading, DownloadingNotice(offer.Version, percent)));

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_downloadTimeout);
        try
        {
            await _client.DownloadAsync(offer, progress, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("O download da atualizacao demorou demais.");
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_firstCheckDelay, _time, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                if (AutoUpdateOn)
                {
                    await CheckOnceAsync(cancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(_interval, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose: the app is closing.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A verificacao de atualizacoes parou");
        }
    }

    // While an update waits for the app to be idle, look at the idle state every few seconds in case no signal came.
    private void EnsureIdleWatch()
    {
        lock (_gate)
        {
            if (_idleWatching || _stop.IsCancellationRequested)
            {
                return;
            }

            _idleWatching = true;
        }

        _ = WatchIdleAsync(_stop.Token); // runs up to its first wait right here, so the timer exists when this returns
    }

    private async Task WatchIdleAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && ShouldKeepWatching())
            {
                await Task.Delay(_idlePoll, _time, cancellationToken).ConfigureAwait(false);
                TryApplyIfIdle();
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose: the app is closing.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A espera pelo momento ocioso parou");
        }
        finally
        {
            lock (_gate)
            {
                _idleWatching = false;
            }
        }
    }

    private bool ShouldKeepWatching()
    {
        lock (_gate)
        {
            return _pending is not null
                && Volatile.Read(ref _applying) == 0
                && !(string.Equals(_pending.Version, _blockedVersion, StringComparison.Ordinal) && _time.GetUtcNow() < _blockedUntil);
        }
    }

    // A signal from the host (the queue, the bar or a send changed): look at the idle state off the thread that raised it.
    private void OnMaybeIdle()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        _ = Task.Run(TryApplyIfIdle);
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        var auto = settings.System.AutoUpdate;
        bool changed;
        lock (_gate)
        {
            changed = _lastAutoUpdate != auto;
            _lastAutoUpdate = auto;
        }

        if (!changed)
        {
            return;
        }

        if (auto)
        {
            EnsureIdleWatch();
            OnMaybeIdle();
        }
        else if (Volatile.Read(ref _applying) == 0)
        {
            bool manual;
            lock (_gate)
            {
                manual = _manualRequested;
            }

            if (!manual && State.Phase is UpdatePhase.WaitingForIdle or UpdatePhase.Idle or UpdatePhase.GaveUp)
            {
                Publish(UpdatePhase.Idle, null, "Atualiza\U000000E7\U000000E3o autom\U000000E1tica desligada");
            }
        }
    }

    // Installs the pending update now when it is allowed and the app is idle. Safe to call from any thread, any number of times:
    // the install starts once.
    private void TryApplyIfIdle()
    {
        try
        {
            UpdateOffer? offer;
            lock (_gate)
            {
                offer = _pending;
                if (offer is null || Volatile.Read(ref _applying) != 0 || _stop.IsCancellationRequested)
                {
                    return;
                }

                if (!_manualRequested && !AutoUpdateOn)
                {
                    return;
                }

                if (string.Equals(offer.Version, _blockedVersion, StringComparison.Ordinal) && _time.GetUtcNow() < _blockedUntil)
                {
                    return;
                }
            }

            if (!_host.IsIdle)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _applying, 1, 0) != 0)
            {
                return;
            }

            ApplyNow(offer);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erro ao instalar a atualizacao");
        }
    }

    private void ApplyNow(UpdateOffer offer)
    {
        var leaving = false;
        try
        {
            if (!TryRecordAttempt(offer.Version))
            {
                GiveUp(offer.Version);
                return;
            }

            Publish(UpdatePhase.Installing, "Instalando e reiniciando\U00002026");
            _logger.LogInformation("Instalando a atualizacao {Version} (versao atual {Current}) e reiniciando", offer.Version, CurrentVersion);
            try
            {
                _host.PrepareForRestart();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Nao foi possivel salvar tudo antes de reiniciar; seguindo");
            }

            WriteMarker();
            _client.ApplyAndRestart(offer, _arguments);
            leaving = true;
            _host.Exit();
        }
        catch (Exception ex)
        {
            if (!leaving)
            {
                Volatile.Write(ref _applying, 0); // the app goes on; the attempt counter limits the tries
                _logger.LogWarning("Falha ao instalar a atualizacao {Version}: {Reason}", offer.Version, ex.Message);
                Publish(UpdatePhase.Idle, null, $"N\U000000E3o foi poss\U000000EDvel instalar a vers\U000000E3o {offer.Version} (veja o registro)");
            }
            else
            {
                // The updater is waiting for this process; if leaving failed it gives up after 60 seconds and the next start tries again.
                _logger.LogWarning(ex, "Nao foi possivel encerrar o app para a atualizacao {Version}", offer.Version);
            }
        }
    }

    // The same version failed twice in 24 hours: one log line, the notice, and no more tries until the day is over.
    private void GiveUp(string version)
    {
        var since = _settings.Current.System.UpdateAttemptSince ?? _time.GetUtcNow();
        bool first;
        lock (_gate)
        {
            first = !string.Equals(_blockedVersion, version, StringComparison.Ordinal);
            _blockedVersion = version;
            _blockedUntil = since + UpdateAttemptPolicy.Window;
        }

        Volatile.Write(ref _applying, 0);
        if (first)
        {
            _logger.LogWarning(
                "A atualizacao {Version} ja teve {Count} tentativas em 24 h sem dar certo; sem novas tentativas ate amanha",
                version,
                UpdateAttemptPolicy.MaxAttempts);
        }

        Publish(UpdatePhase.GaveUp, null, $"N\U000000E3o foi poss\U000000EDvel instalar a vers\U000000E3o {version}; nova tentativa amanh\U000000E3");
    }

    // Records the attempt in the settings and saves them now, so the count survives the restart it limits.
    private bool TryRecordAttempt(string version)
    {
        var now = _time.GetUtcNow();
        var allowed = false;
        _settings.Update(settings =>
        {
            allowed = UpdateAttemptPolicy.TryRecord(settings.System, version, now, out var next);
            return allowed ? settings with { System = next } : settings;
        });

        if (allowed)
        {
            _settings.Flush();
        }

        return allowed;
    }

    private void WriteMarker()
    {
        if (_markerFile is not null)
        {
            UpdateRestartMarker.Write(_markerFile, _time.GetUtcNow());
        }
    }

    private void LogFailure(Exception ex) =>
        _logger.LogInformation("Sem como verificar atualizacoes agora ({Reason})", ex.Message);

    private void Publish(UpdatePhase phase, string? notice, string? result = null)
    {
        UpdateState state;
        lock (_gate)
        {
            state = new UpdateState(phase, notice, result ?? _state?.Result ?? InitialResult());
            if (state == _state)
            {
                return;
            }

            _state = state;
        }

        var handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<UpdateState>>())
        {
            try
            {
                handler(state);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Um ouvinte do estado da atualizacao falhou");
            }
        }
    }

    private static string NotInstalledText => "Esta c\U000000F3pia n\U000000E3o foi instalada: sem atualiza\U000000E7\U000000E3o autom\U000000E1tica";

    private string InitialResult()
    {
        try
        {
            if (!_client.IsInstalled)
            {
                return NotInstalledText;
            }
        }
        catch (Exception)
        {
            return "Ainda n\U000000E3o verificado";
        }

        return AutoUpdateOn ? "Ainda n\U000000E3o verificado" : "Atualiza\U000000E7\U000000E3o autom\U000000E1tica desligada";
    }

    private string UpToDateText() =>
        $"Atualizado ({CurrentVersion}) \U000000E0s {_time.GetLocalNow().ToString("HH:mm", CultureInfo.InvariantCulture)}";

    private static string WaitingText(string version) =>
        $"Nova vers\U000000E3o {version} baixada, instalando quando ficar ocioso";

    private static string FailedText(Exception ex) => ex switch
    {
        HttpRequestException or TimeoutException or IOException or System.Net.Sockets.SocketException => "Sem conex\U000000E3o com o GitHub",
        _ => "N\U000000E3o foi poss\U000000EDvel verificar agora (veja o registro)",
    };

    private static string DownloadingNotice(string version, int percent) =>
        percent > 0
            ? $"Baixando a atualiza\U000000E7\U000000E3o {version}\U00002026 {percent}%"
            : $"Baixando a atualiza\U000000E7\U000000E3o {version}\U00002026";

    // Reports on the thread that reports (Progress<T> would post to a synchronization context, and there is none to wait for).
    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        private int _last = -1;

        public void Report(int value)
        {
            var percent = Math.Clamp(value, 0, 100);
            if (Interlocked.Exchange(ref _last, percent) != percent)
            {
                report(percent);
            }
        }
    }
}
