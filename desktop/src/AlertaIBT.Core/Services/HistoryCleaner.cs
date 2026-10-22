using AlertaIBT.Core.Firebase;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Telegram;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Services;

/// <summary>What happened to the site part of a clear.</summary>
public enum SiteClearOutcome
{
    /// <summary>The site is off or not filled in: nothing to clear there.</summary>
    NotConfigured,

    /// <summary>The finished requests were deleted from the site.</summary>
    Cleared,

    /// <summary>The site could not be cleared now (offline, or some requests stayed); it is cleared as soon as it is back.</summary>
    Pending,
}

/// <summary>What "Limpar histórico" did, per place.</summary>
/// <param name="PcEntries">History lines removed at the PC.</param>
/// <param name="Site">What happened on the site.</param>
/// <param name="SiteRequests">Requests deleted from the site.</param>
/// <param name="TelegramDeleted">Messages deleted from the Telegram group.</param>
/// <param name="TelegramFailed">Messages that stay in the group (older than 47 hours, no right to delete, or no connection).</param>
public sealed record HistoryClearResult(int PcEntries, SiteClearOutcome Site, int SiteRequests, int TelegramDeleted, int TelegramFailed);

/// <summary>
/// "Limpar histórico": empties the history of the PC queue, deletes the finished requests of the site and the messages of the
/// Telegram group that the app knows about. The queue, the notice on the bar and the memory of plates shown in the last 10 minutes
/// are not touched. It never throws: a part that fails is logged and the others still run.
/// </summary>
/// <remarks>
/// <para>
/// The site part runs only while the site is online. Otherwise <see cref="SystemSettings.SitePendingClear"/> is set and
/// <see cref="RunPendingSiteClearAsync"/> does it as soon as the site is back, without clearing the PC again.
/// </para>
/// <para>Two clears asked at the same time are one: the second caller gets the result of the first.</para>
/// </remarks>
public sealed class HistoryCleaner
{
    private readonly AlertQueue _queue;
    private readonly Func<ISiteHistory?> _site;
    private readonly ITelegramHistory _telegram;
    private readonly Func<AppSettings> _settings;
    private readonly Action<Func<AppSettings, AppSettings>> _update;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _siteGate = new(1, 1);
    private Task<HistoryClearResult>? _running;

    /// <param name="queue">The queue whose history is emptied.</param>
    /// <param name="site">Gives the running site connection, or <c>null</c> when there is none.</param>
    /// <param name="telegram">Deletes the messages of the group.</param>
    /// <param name="settings">Gives the current settings.</param>
    /// <param name="update">Changes the settings (the time of the last clear, the pending site).</param>
    /// <param name="clock">The source of the current time.</param>
    /// <param name="logger">Receives what happened.</param>
    public HistoryCleaner(
        AlertQueue queue,
        Func<ISiteHistory?> site,
        ITelegramHistory telegram,
        Func<AppSettings> settings,
        Action<Func<AppSettings, AppSettings>> update,
        TimeProvider? clock,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(telegram);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(logger);

        _queue = queue;
        _site = site;
        _telegram = telegram;
        _settings = settings;
        _update = update;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>Clears the history of the PC, the site and Telegram. Never throws (except that <paramref name="ct"/> stops the wait).</summary>
    /// <param name="ct">Stops the wait for the result; the clear itself, once started, runs to its end.</param>
    /// <returns>What was cleared in each place.</returns>
    public Task<HistoryClearResult> ClearAllAsync(CancellationToken ct = default)
    {
        Task<HistoryClearResult> run;
        lock (_gate)
        {
            _running ??= Task.Run(RunAsync, CancellationToken.None);
            run = _running;
        }

        return run.WaitAsync(ct);
    }

    /// <summary>
    /// Clears the site if an earlier clear had to leave it for later (<see cref="SystemSettings.SitePendingClear"/>) and the site is
    /// online now. Never throws.
    /// </summary>
    /// <param name="ct">Stops the wait.</param>
    /// <returns><c>true</c> when the site was cleared now.</returns>
    public async Task<bool> RunPendingSiteClearAsync(CancellationToken ct = default)
    {
        try
        {
            if (!_settings().System.SitePendingClear)
            {
                return false;
            }

            var (outcome, _) = await ClearSiteAsync(ct).ConfigureAwait(false);
            return outcome == SiteClearOutcome.Cleared;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao limpar o site depois de voltar");
            return false;
        }
    }

    private async Task<HistoryClearResult> RunAsync()
    {
        try
        {
            var (site, siteRequests) = await ClearSiteAsync(CancellationToken.None).ConfigureAwait(false);

            var pc = 0;
            try
            {
                pc = _queue.ClearHistory();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao limpar o historico do PC");
            }

            var telegram = new TelegramClearResult(0, 0);
            try
            {
                telegram = await _telegram.ClearAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao limpar as mensagens do Telegram");
            }

            var now = _clock.GetUtcNow();
            _update(s => s with { System = s.System with { LastHistoryClearAt = now } });
            _logger.LogInformation(
                "Historico limpo: {Pc} no PC, {Site} no site ({Outcome}), {Telegram} no Telegram",
                pc,
                siteRequests,
                site,
                telegram.Deleted);
            return new HistoryClearResult(pc, site, siteRequests, telegram.Deleted, telegram.Failed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao limpar o historico");
            return new HistoryClearResult(0, SiteClearOutcome.Pending, 0, 0, 0);
        }
        finally
        {
            lock (_gate)
            {
                _running = null;
            }
        }
    }

    // The site part: runs when the site is online, otherwise remembers that it is owed. One at a time.
    private async Task<(SiteClearOutcome Outcome, int Deleted)> ClearSiteAsync(CancellationToken ct)
    {
        await _siteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = _settings();
            if (!settings.Site.Enabled || !settings.Site.IsConfigured)
            {
                return (SiteClearOutcome.NotConfigured, 0);
            }

            var deleted = 0;
            var done = false;
            try
            {
                if (_site() is { State: SiteState.Online } site)
                {
                    var result = await site.ClearHistoryAsync(ct).ConfigureAwait(false);
                    deleted = result.Deleted;
                    done = result.Failed == 0;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Nao foi possivel limpar o site agora; sera limpo quando voltar");
            }

            SetPending(!done);
            return (done ? SiteClearOutcome.Cleared : SiteClearOutcome.Pending, deleted);
        }
        finally
        {
            _siteGate.Release();
        }
    }

    private void SetPending(bool pending)
    {
        if (_settings().System.SitePendingClear != pending)
        {
            _update(s => s with { System = s.System with { SitePendingClear = pending } });
        }
    }
}
