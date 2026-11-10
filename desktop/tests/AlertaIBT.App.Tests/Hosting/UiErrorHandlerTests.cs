using System.ComponentModel;
using AlertaIBT.App.Hosting;
using AlertaIBT.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.App.Tests.Hosting;

// Batch 42: the 1816 episodes of the owner's log (2026-11-06 21:56 and 23:42, 17 identical ERROR lines) become one warning per episode with the session state, one line when
// the episode ends, and the windows are redone once. The app never dies from them.
public sealed class UiErrorHandlerTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 6, 23, 42, 0, TimeSpan.Zero));
    private readonly List<(LogLevel Level, string Message)> _lines = [];
    private readonly SessionState _session;
    private readonly BreadcrumbTrail _trail;
    private readonly UiErrorHandler _handler;

    private sealed class FakeStackWin32() : Win32Exception(1816)
    {
        public override string? StackTrace =>
            "   at System.Windows.Interop.HwndTarget.UpdateWindowSettings(Boolean enableRenderTarget, Nullable`1 channelSet)\n   at System.Windows.Interop.HwndTarget.UpdateWindowPos(IntPtr lParam)";
    }

    private sealed class ListLogger(List<(LogLevel Level, string Message)> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Add((logLevel, formatter(state, exception)));
    }

    public UiErrorHandlerTests()
    {
        _session = new SessionState(_clock);
        _trail = new BreadcrumbTrail(clock: _clock);
        _handler = new UiErrorHandler(new ListLogger(_lines), new UiFailureEpisodes(_clock), _session, () => "GDI 46, USER 41", _trail, _clock);
    }

    [Fact]
    public void Seventeen_identical_quota_errors_are_one_warning_with_the_session_state_and_never_an_error()
    {
        _session.SetLocked(true);
        _session.SetDisplay(DisplayPower.Off);

        for (var i = 0; i < 17; i++)
        {
            Assert.True(_handler.Handle(new FakeStackWin32()));
            _clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        var line = Assert.Single(_lines);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("1816", line.Message, StringComparison.Ordinal);
        Assert.Contains("bloqueada", line.Message, StringComparison.Ordinal);
        Assert.Contains("tela desligada", line.Message, StringComparison.Ordinal);
        Assert.Contains("GDI 46", line.Message, StringComparison.Ordinal);
        Assert.Contains("falha do sistema de janelas (1816) x17", _trail.Format(_clock.GetUtcNow()), StringComparison.Ordinal);
    }

    [Fact]
    public void When_the_episode_ends_one_more_line_tells_the_count_and_the_duration_and_the_windows_are_redone_once()
    {
        var redone = 0;
        for (var i = 0; i < 17; i++)
        {
            _handler.Handle(new FakeStackWin32());
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.False(_handler.EndQuietEpisodes(() => redone++)); // still inside the episode
        _clock.Advance(UiFailureEpisodes.QuietToEnd);
        Assert.True(_handler.EndQuietEpisodes(() => redone++));
        Assert.False(_handler.EndQuietEpisodes(() => redone++)); // told once

        Assert.Equal(1, redone);
        Assert.Equal(2, _lines.Count);
        Assert.Equal(LogLevel.Warning, _lines[1].Level);
        Assert.Contains("repetida 17 vezes", _lines[1].Message, StringComparison.Ordinal);
        Assert.Contains("durou 16.0 s", _lines[1].Message.Replace(',', '.'), StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_while_redoing_the_windows_is_only_a_warning()
    {
        _handler.Handle(new FakeStackWin32());
        _clock.Advance(UiFailureEpisodes.QuietToEnd);

        Assert.True(_handler.EndQuietEpisodes(() => throw new InvalidOperationException("still refused")));

        Assert.Equal(LogLevel.Warning, _lines[^1].Level);
    }

    [Fact]
    public void Any_other_error_is_still_handled_and_written_as_an_error_with_identical_repeats_counted()
    {
        for (var i = 0; i < 4; i++)
        {
            Assert.True(_handler.Handle(new InvalidOperationException("outro erro")));
        }

        var line = Assert.Single(_lines);
        Assert.Equal(LogLevel.Error, line.Level);
        Assert.Contains("Erro na interface", line.Message, StringComparison.Ordinal);

        _clock.Advance(TimeSpan.FromSeconds(11));
        _handler.Handle(new InvalidOperationException("outro erro"));

        Assert.Equal(2, _lines.Count);
        Assert.Contains("(3 iguais suprimidos", _lines[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_counters_function_that_throws_never_loses_the_line()
    {
        var handler = new UiErrorHandler(new ListLogger(_lines), new UiFailureEpisodes(_clock), _session, () => throw new InvalidOperationException("no counters"), _trail, _clock);

        Assert.True(handler.Handle(new InvalidOperationException("x")));

        Assert.Contains("contadores indisponiveis", Assert.Single(_lines).Message, StringComparison.Ordinal);
    }
}
