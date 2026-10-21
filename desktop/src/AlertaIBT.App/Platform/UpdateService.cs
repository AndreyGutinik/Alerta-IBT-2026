using System.IO;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace AlertaIBT.App.Platform;

/// <summary>An update found on the release feed. <see cref="Handle"/> is whatever the client needs to download and apply it.</summary>
/// <param name="Version">The version, for logs and for the "already tried" marker.</param>
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

    /// <summary>Downloads the package of <paramref name="offer"/> to the local packages folder.</summary>
    Task DownloadAsync(UpdateOffer offer, CancellationToken cancellationToken);

    /// <summary>Starts the updater and exits this process; the updater relaunches the app with <paramref name="restartArguments"/>.</summary>
    void ApplyAndRestart(UpdateOffer offer, string[] restartArguments);
}

/// <summary>Velopack over the GitHub Releases of the project.</summary>
public sealed class VelopackUpdateClient : IUpdateClient
{
    /// <summary>The repository whose Releases carry the installer and the updates.</summary>
    public const string RepositoryUrl = "https://github.com/AndreyGutinik/Alerta-IBT-2026";

    private readonly Lazy<UpdateManager?> _manager;

    /// <param name="repositoryUrl">The GitHub repository; <see cref="RepositoryUrl"/> in the app.</param>
    public VelopackUpdateClient(string repositoryUrl = RepositoryUrl)
    {
        _manager = new Lazy<UpdateManager?>(() =>
        {
            try
            {
                var manager = new UpdateManager(new GithubSource(repositoryUrl, accessToken: null, prerelease: false));
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

    /// <inheritdoc />
    public async Task<UpdateOffer?> CheckAsync(CancellationToken cancellationToken)
    {
        var manager = RequireManager();
        var info = await manager.CheckForUpdatesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        return info is null ? null : new UpdateOffer(info.TargetFullRelease.Version.ToString(), info);
    }

    /// <inheritdoc />
    public Task DownloadAsync(UpdateOffer offer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);

        return RequireManager().DownloadUpdatesAsync((UpdateInfo)offer.Handle, null, cancellationToken);
    }

    /// <inheritdoc />
    public void ApplyAndRestart(UpdateOffer offer, string[] restartArguments)
    {
        ArgumentNullException.ThrowIfNull(offer);

        var asset = offer.Handle as VelopackAsset ?? ((UpdateInfo)offer.Handle).TargetFullRelease;
        RequireManager().ApplyUpdatesAndRestart(asset, restartArguments);
    }

    private UpdateManager RequireManager() => _manager.Value ?? throw new InvalidOperationException("Not an installed copy.");
}

/// <summary>What one look at the release feed ended in.</summary>
public enum UpdateCheckResult
{
    /// <summary>A development build or a copied folder: updates do not apply.</summary>
    NotInstalled,

    /// <summary>The newest release is the one running.</summary>
    UpToDate,

    /// <summary>A newer version is downloaded and waits for the next start.</summary>
    Downloaded,

    /// <summary>The feed could not be read or the download failed (an offline church PC is normal); it is tried again later.</summary>
    Failed,
}

/// <summary>
/// Automatic updates from the GitHub Releases (spec 13 and decision 18). Three promises:
/// the check runs in the background a couple of minutes after the start and never delays it; nothing it does can throw into
/// the interface (offline is normal and only logged); and an update is never applied while the app is serving: it is downloaded
/// quietly and installed at the NEXT start of the app (<see cref="TryApplyPendingAtStartup"/>), when the owner reopens it or the
/// PC boots.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private readonly IUpdateClient _client;
    private readonly ILogger<UpdateService> _logger;
    private readonly string _attemptFile;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _firstCheckDelay;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _checkTimeout;
    private readonly TimeSpan _downloadTimeout;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    /// <param name="client">The update system.</param>
    /// <param name="logger">Where outcomes are logged.</param>
    /// <param name="attemptFile">A file that remembers the version an apply was last tried for (no apply loop if it fails).</param>
    /// <param name="delay">Waits; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> in the app, a fake in tests.</param>
    /// <param name="firstCheckDelay">Wait before the first look (default 2 minutes).</param>
    /// <param name="interval">Wait between looks (default 6 hours).</param>
    /// <param name="checkTimeout">Longest wait for the feed (default 30 seconds).</param>
    /// <param name="downloadTimeout">Longest wait for the download (default 15 minutes).</param>
    public UpdateService(
        IUpdateClient client,
        ILogger<UpdateService> logger,
        string attemptFile,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? firstCheckDelay = null,
        TimeSpan? interval = null,
        TimeSpan? checkTimeout = null,
        TimeSpan? downloadTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptFile);

        _client = client;
        _logger = logger;
        _attemptFile = attemptFile;
        _delay = delay ?? ((span, token) => Task.Delay(span, token));
        _firstCheckDelay = firstCheckDelay ?? TimeSpan.FromMinutes(2);
        _interval = interval ?? TimeSpan.FromHours(6);
        _checkTimeout = checkTimeout ?? TimeSpan.FromSeconds(30);
        _downloadTimeout = downloadTimeout ?? TimeSpan.FromMinutes(15);
    }

    /// <summary>
    /// Applies an update downloaded earlier: the updater installs it and starts the app again with <paramref name="arguments"/>.
    /// Returns true when the restart is under way and the caller must exit at once. It tries each version once: if the apply
    /// failed the last time, the app just starts. Call it only after the single-instance lock is held, so a second launch of
    /// a running app never restarts it. Never throws.
    /// </summary>
    public bool TryApplyPendingAtStartup(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        try
        {
            if (!_client.IsInstalled)
            {
                return false;
            }

            var pending = _client.Pending;
            if (pending is null)
            {
                return false;
            }

            if (string.Equals(ReadAttempt(), pending.Version, StringComparison.Ordinal))
            {
                _logger.LogWarning("A atualizacao {Version} ja foi tentada e nao foi aplicada; seguindo com a versao {Current}", pending.Version, _client.CurrentVersion);
                return false;
            }

            WriteAttempt(pending.Version);
            _logger.LogInformation("Aplicando a atualizacao {Version} baixada antes (versao atual {Current})", pending.Version, _client.CurrentVersion);
            _client.ApplyAndRestart(pending, arguments);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nao foi possivel aplicar a atualizacao baixada; seguindo com a versao atual");
            return false;
        }
    }

    /// <summary>Starts the background checks. Returns at once; never throws.</summary>
    public void Start()
    {
        try
        {
            _logger.LogInformation("Alerta-IBT {Version} (instalado: {Installed})", _client.CurrentVersion, _client.IsInstalled);
            if (!_client.IsInstalled)
            {
                return; // development build: there is nothing to update
            }

            _loop ??= Task.Run(() => RunAsync(_stop.Token));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nao foi possivel iniciar a verificacao de atualizacoes");
        }
    }

    /// <summary>One look at the feed, and the download when something newer is there. Never throws (except to honor <paramref name="cancellationToken"/>).</summary>
    public async Task<UpdateCheckResult> CheckOnceAsync(CancellationToken cancellationToken)
    {
        if (!_client.IsInstalled)
        {
            return UpdateCheckResult.NotInstalled;
        }

        try
        {
            using var checkLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            checkLimit.CancelAfter(_checkTimeout);
            UpdateOffer? offer;
            try
            {
                offer = await _client.CheckAsync(checkLimit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("A busca por atualizacoes demorou demais.");
            }

            if (offer is null)
            {
                return UpdateCheckResult.UpToDate;
            }

            if (string.Equals(_client.Pending?.Version, offer.Version, StringComparison.Ordinal))
            {
                return UpdateCheckResult.Downloaded; // got it on an earlier run
            }

            using var downloadLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            downloadLimit.CancelAfter(_downloadTimeout);
            try
            {
                await _client.DownloadAsync(offer, downloadLimit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("O download da atualizacao demorou demais.");
            }

            _logger.LogInformation("Atualizacao {Version} baixada; sera instalada na proxima vez que o app abrir", offer.Version);
            return UpdateCheckResult.Downloaded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Offline, GitHub down, a half download: all normal. One line, no stack, tried again later.
            _logger.LogInformation("Sem como verificar atualizacoes agora ({Reason})", ex.Message);
            return UpdateCheckResult.Failed;
        }
    }

    /// <summary>Stops the background checks.</summary>
    public void Dispose()
    {
        _stop.Cancel();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _delay(_firstCheckDelay, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await CheckOnceAsync(cancellationToken).ConfigureAwait(false);
                if (result is UpdateCheckResult.Downloaded or UpdateCheckResult.NotInstalled)
                {
                    return; // nothing more to look for until the next start
                }

                await _delay(_interval, cancellationToken).ConfigureAwait(false);
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

    private string? ReadAttempt()
    {
        try
        {
            return File.Exists(_attemptFile) ? File.ReadAllText(_attemptFile).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void WriteAttempt(string version)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_attemptFile)!);
            File.WriteAllText(_attemptFile, version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the marker a failing apply could repeat once per start; harmless.
        }
    }
}
