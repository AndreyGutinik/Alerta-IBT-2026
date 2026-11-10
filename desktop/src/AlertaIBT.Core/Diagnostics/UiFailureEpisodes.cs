using System.ComponentModel;

namespace AlertaIBT.Core.Diagnostics;

/// <summary>
/// Failures of the Windows window system that WPF raises on its own and that are not our bug: <c>Win32Exception 1816</c> ("Nao ha cota suficiente para processar
/// este comando", ERROR_NOT_ENOUGH_QUOTA) from <c>HwndTarget.UpdateWindowSettings</c> / <c>UpdateWindowPos</c>, thrown while WPF handles <c>WM_WINDOWPOSCHANGED</c>
/// (a display change, a locked or disconnected session, a closing dispatcher). The app must never die from them: they are handled, written once per episode and
/// the windows are repainted when the episode ends.
/// </summary>
public static class KnownUiFailures
{
    /// <summary>ERROR_NOT_ENOUGH_QUOTA.</summary>
    public const int NotEnoughQuota = 1816;

    /// <summary>Whether <paramref name="exception"/> is the window-system quota error of WPF's <c>HwndTarget</c>.</summary>
    public static bool IsWindowSystemQuota(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is Win32Exception { NativeErrorCode: NotEnoughQuota })
            {
                var stack = current.StackTrace ?? string.Empty;
                if (stack.Contains("HwndTarget", StringComparison.Ordinal)
                    || stack.Contains("UpdateWindowSettings", StringComparison.Ordinal)
                    || stack.Contains("UpdateWindowPos", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>A short name of the failure for the log ("cota do sistema de janelas (erro 1816)"), or null when it is not a known one.</summary>
    public static string? Name(Exception? exception) =>
        IsWindowSystemQuota(exception) ? "cota do sistema de janelas (erro 1816 em HwndTarget)" : null;
}

/// <summary>How the episode a failure belongs to stands.</summary>
/// <param name="IsFirst">The failure opened a new episode: write the one warning now.</param>
/// <param name="Count">How many failures the episode has so far (this one included).</param>
public readonly record struct EpisodeNote(bool IsFirst, int Count);

/// <summary>An episode that ended (no failure for <see cref="UiFailureEpisodes.QuietToEnd"/>).</summary>
/// <param name="Kind">The name given to <see cref="UiFailureEpisodes.Record"/>.</param>
/// <param name="Count">How many failures it had.</param>
/// <param name="Duration">From the first failure to the last.</param>
/// <param name="StartedAt">When the first one came.</param>
public sealed record EpisodeEnd(string Kind, int Count, TimeSpan Duration, DateTimeOffset StartedAt);

/// <summary>
/// Groups a flood of the same failure into episodes: the first failure opens an episode (one warning), the next ones only count, and when nothing came for
/// <see cref="QuietToEnd"/> the episode ends with its count and duration (one more line), and the app repaints its windows. A window-system quota error comes by
/// the dozen in one second, and 17 identical ERROR lines say nothing the first one did not. Thread-safe; <see cref="Poll"/> is called by a timer.
/// </summary>
public sealed class UiFailureEpisodes
{
    /// <summary>How long without a failure ends an episode.</summary>
    public static readonly TimeSpan QuietToEnd = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private string? _kind;
    private DateTimeOffset _startedAt;
    private DateTimeOffset _lastAt;
    private int _count;

    /// <param name="clock">The clock; a fake in tests.</param>
    public UiFailureEpisodes(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>Whether an episode is open.</summary>
    public bool Active
    {
        get
        {
            lock (_gate)
            {
                return _kind is not null;
            }
        }
    }

    /// <summary>Notes one failure of kind <paramref name="kind"/>. A failure of another kind than the open episode's starts its own (the open one is closed by <see cref="Poll"/> later).</summary>
    public EpisodeNote Record(string kind)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);

        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (_kind is null || !string.Equals(_kind, kind, StringComparison.Ordinal))
            {
                _kind = kind;
                _startedAt = now;
                _lastAt = now;
                _count = 1;
                return new EpisodeNote(true, 1);
            }

            _lastAt = now;
            _count++;
            return new EpisodeNote(false, _count);
        }
    }

    /// <summary>The episode that has been quiet long enough, or null (and then there is nothing to say).</summary>
    public EpisodeEnd? Poll()
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (_kind is null || now - _lastAt < QuietToEnd)
            {
                return null;
            }

            var ended = new EpisodeEnd(_kind, _count, _lastAt - _startedAt, _startedAt);
            _kind = null;
            _count = 0;
            return ended;
        }
    }
}
