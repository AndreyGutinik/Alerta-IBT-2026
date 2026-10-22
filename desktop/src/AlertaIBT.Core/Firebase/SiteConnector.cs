using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Firebase;

/// <summary>
/// Keeps the connection to the site in step with the settings: it starts a <see cref="SiteEngine"/> when the site is switched on and
/// filled in, stops it when it is switched off, and replaces it (a new sign-in) when the address, the API key, the e-mail or the
/// password change. The state goes to the <see cref="StatusHub"/> for the sidebar dot. Nothing here throws.
/// </summary>
/// <remarks>
/// <see cref="Apply"/> returns at once; changes that come close together are folded, so typing in the settings never opens a
/// connection for every letter (the settings are saved only after a pause, and only a saved value gets here).
/// </remarks>
public sealed class SiteConnector : IDisposable
{
    private readonly Func<SiteConfig, ISiteEngine> _factory;
    private readonly StatusHub _hub;
    private readonly ISecretProtector _protector;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    private SiteConfig? _wanted;
    private bool _hasWanted;
    private SiteConfig? _running;
    private ISiteEngine? _engine;
    private Task _worker = Task.CompletedTask;
    private bool _working;
    private bool _disposed;

    /// <param name="factory">Makes the engine for a configuration (not started).</param>
    /// <param name="hub">Receives the site state.</param>
    /// <param name="protector">Reads the password out of the settings.</param>
    /// <param name="logger">Receives what happened, without secrets.</param>
    public SiteConnector(Func<SiteConfig, ISiteEngine> factory, StatusHub hub, ISecretProtector protector, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(logger);

        _factory = factory;
        _hub = hub;
        _protector = protector;
        _logger = logger;
    }

    /// <summary>The running connection, to clear the history of the site; <c>null</c> when the site is off or not filled in.</summary>
    public ISiteHistory? History => Volatile.Read(ref _engine) as ISiteHistory;

    /// <summary>The configuration the settings ask for, or <c>null</c> when the site is off or not filled in.</summary>
    public static SiteConfig? ConfigOf(AppSettings settings, ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(protector);

        var site = settings.Site;
        if (!site.Enabled || !site.IsConfigured || settings.GetSitePassword(protector) is not { } password)
        {
            return null;
        }

        return new SiteConfig(site.DatabaseUrl, site.ApiKey, site.DeviceEmail, password);
    }

    /// <summary>Applies the settings: starts, stops or replaces the connection as needed. Returns at once.</summary>
    public void Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        SiteConfig? wanted;
        try
        {
            wanted = ConfigOf(settings, _protector);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao ler a configuracao do site");
            wanted = null;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _wanted = wanted;
            _hasWanted = true;
            if (_working)
            {
                return;
            }

            _working = true;
            _worker = Task.Run(WorkAsync);
        }
    }

    /// <summary>Waits until every change asked so far has been carried out (for tests and for shutdown).</summary>
    public Task SettledAsync()
    {
        lock (_gate)
        {
            return _worker;
        }
    }

    /// <summary>Stops the connection and does not start another.</summary>
    public async Task StopAsync()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        await SettledAsync().ConfigureAwait(false);
        await StopEngineAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            StopAsync().Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao parar o site");
        }
    }

    private async Task WorkAsync()
    {
        while (true)
        {
            SiteConfig? wanted;
            lock (_gate)
            {
                if (!_hasWanted || _disposed)
                {
                    _working = false;
                    return;
                }

                wanted = _wanted;
                _hasWanted = false;
            }

            try
            {
                if (wanted is not null && Same(wanted, _running) && _engine is not null)
                {
                    continue;
                }

                await StopEngineAsync().ConfigureAwait(false);
                if (wanted is null)
                {
                    _hub.SetSite(SiteState.NotConfigured);
                    continue;
                }

                var engine = _factory(wanted);
                engine.StateChanged += OnState;
                _engine = engine;
                _running = wanted;
                _hub.SetSite(SiteState.Connecting);
                engine.Start();
                _logger.LogInformation("Conectando ao site");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao iniciar o site");
                _hub.ReportError("Site", ex);
                _hub.SetSite(SiteState.Offline, "N\U000000E3o foi poss\U000000EDvel iniciar a conex\U000000E3o com o site. Veja o registro (log).");
            }
        }
    }

    private static bool Same(SiteConfig? a, SiteConfig? b) => a == b;

    private async Task StopEngineAsync()
    {
        var engine = _engine;
        _engine = null;
        _running = null;
        if (engine is null)
        {
            return;
        }

        engine.StateChanged -= OnState;
        try
        {
            await engine.StopAsync().ConfigureAwait(false);
            if (engine is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao parar o site");
        }
    }

    private void OnState(SiteState state, string? detail)
    {
        try
        {
            _hub.SetSite(state, detail);
            switch (state)
            {
                case SiteState.AuthError or SiteState.Offline:
                    _logger.LogWarning("Site {State}: {Detail}", state, detail ?? "sem detalhe");
                    break;
                default:
                    _logger.LogInformation("Site {State}", state);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao registrar o estado do site");
        }
    }
}
