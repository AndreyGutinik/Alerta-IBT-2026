using AlertaIBT.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.App.Hosting;

/// <summary>
/// What the app does with an exception that reached the UI dispatcher unhandled. It never lets the app die from it (the answer is always "handled"), and it writes it
/// down the right way: a window-system quota error (WPF's own <c>HwndTarget</c> failure, error 1816) comes by the dozen in a second and is written ONCE per episode as
/// a warning with the session state, then one more line when the episode ends (count and duration) and the windows are put back in order; any other error is an
/// ERROR line with the process counters and what the app did last, repeated identical ones counted instead of written again.
/// </summary>
public sealed class UiErrorHandler
{
    private readonly ILogger _logger;
    private readonly UiFailureEpisodes _failures;
    private readonly SessionState _session;
    private readonly Func<string> _counters;
    private readonly BreadcrumbTrail _trail;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, (DateTimeOffset At, int Suppressed)> _logged = [];

    /// <param name="logger">Receives the lines.</param>
    /// <param name="failures">Groups the known failures into episodes.</param>
    /// <param name="session">The session state written into the lines.</param>
    /// <param name="counters">The counters of the process (GDI, USER, handles...); <c>ProcessCounters.Describe</c> in the app.</param>
    /// <param name="trail">What the app did last.</param>
    /// <param name="clock">The clock; a fake in tests.</param>
    public UiErrorHandler(ILogger logger, UiFailureEpisodes failures, SessionState session, Func<string> counters, BreadcrumbTrail? trail = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(failures);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(counters);

        _logger = logger;
        _failures = failures;
        _session = session;
        _counters = counters;
        _trail = trail ?? BreadcrumbTrail.Default;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Handles one unhandled dispatcher exception. Always returns true: the app lives on.</summary>
    public bool Handle(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var now = _clock.GetUtcNow();
        if (KnownUiFailures.Name(exception) is { } known)
        {
            _trail.Add("falha do sistema de janelas (1816)");
            if (_failures.Record(known).IsFirst)
            {
                _logger.LogWarning(
                    "Falha passageira do Windows ao atualizar uma janela: {Failure}. O app continua; vale uma linha por episodio e outra quando ele terminar [{Session}] [{Counters}]. Ultimas operacoes: {Trail}",
                    known,
                    _session.Describe(),
                    SafeCounters(),
                    _trail.Format(now));
            }

            return true;
        }

        // The same error over and over is written once, then counted.
        var key = exception.GetType().Name + ":" + exception.Message;
        var suppressed = 0;
        var write = true;
        if (_logged.TryGetValue(key, out var seen) && now - seen.At < TimeSpan.FromSeconds(10))
        {
            write = false;
            _logged[key] = (seen.At, seen.Suppressed + 1);
        }
        else
        {
            suppressed = seen.Suppressed;
            _logged[key] = (now, 0);
        }

        _trail.Add("erro na interface: " + exception.GetType().Name);
        if (write)
        {
            _logger.LogError(
                exception,
                "Erro na interface ({Repeated} iguais suprimidos desde o ultimo aviso) [{Counters}]. Ultimas operacoes: {Trail}",
                suppressed,
                SafeCounters(),
                _trail.Format(now));
        }

        return true;
    }

    /// <summary>
    /// Called every couple of seconds: when an episode of known failures has been quiet long enough, writes the line with its count and duration and runs <paramref name="reapply"/>
    /// (the windows are placed again and repainted, so the bar and the camera window are not left blank). Returns whether an episode ended.
    /// </summary>
    public bool EndQuietEpisodes(Action reapply)
    {
        ArgumentNullException.ThrowIfNull(reapply);

        if (_failures.Poll() is not { } ended)
        {
            return false;
        }

        _logger.LogWarning(
            "Falha passageira do Windows terminou: {Failure} repetida {Count} vezes, durou {Seconds:0.0} s [{Session}]",
            ended.Kind,
            ended.Count,
            ended.Duration.TotalSeconds,
            _session.Describe());
        try
        {
            reapply();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Nao foi possivel refazer as janelas depois da falha do Windows");
        }

        return true;
    }

    private string SafeCounters()
    {
        try
        {
            return _counters();
        }
        catch (Exception)
        {
            return "contadores indisponiveis";
        }
    }
}
