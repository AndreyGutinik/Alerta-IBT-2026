using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Queue;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Queue;

// "Limpar historico" empties the history and nothing else.
public sealed class AlertQueueClearHistoryTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero));
    private readonly AlertQueue _queue;

    public AlertQueueClearHistoryTests() => _queue = new AlertQueue(_clock);

    private QueueItem Item(string id, string? plate = null) =>
        new(id, "TEXT " + id, plate, AlertSource.Telegram, false, _clock.GetUtcNow(), "42", "original " + id);

    private void Show(string id, string? plate)
    {
        _queue.Enqueue(Item(id, plate), DuplicatePolicy.Warn);
        _queue.StartNext();
        _queue.FinishCurrent();
    }

    [Fact]
    public void It_removes_every_history_line_and_says_how_many()
    {
        Show("a", null);
        Show("b", null);
        _queue.AddRejected(Item("c"), HistoryReasons.TooLong);

        var removed = _queue.ClearHistory();

        Assert.Equal(3, removed);
        Assert.Empty(_queue.History);
    }

    [Fact]
    public void It_keeps_the_alert_on_screen_the_waiting_ones_and_the_pause_state()
    {
        Show("old", null);
        _queue.Enqueue(Item("on-screen"), DuplicatePolicy.Warn);
        _queue.Enqueue(Item("waiting-1"), DuplicatePolicy.Warn);
        _queue.Enqueue(Item("waiting-2"), DuplicatePolicy.Warn);
        _queue.StartNext();

        _queue.ClearHistory();

        Assert.Equal("on-screen", _queue.Current?.Id);
        Assert.Equal(["waiting-1", "waiting-2"], _queue.Pending.Select(p => p.Id));
        Assert.False(_queue.IsPaused);
    }

    [Fact]
    public void A_plate_shown_a_moment_ago_is_still_a_duplicate_afterwards()
    {
        Show("a", "ABC-1D23");

        _queue.ClearHistory();

        var again = _queue.Enqueue(Item("b", "ABC-1D23"), DuplicatePolicy.Block);
        Assert.Equal(EnqueueKind.DuplicateBlocked, again.Kind);
        Assert.True(_queue.CheckDuplicate("ABC-1D23").IsDuplicate);

        // ... and the memory still ends after the 10 minutes.
        _clock.Advance(TimeSpan.FromMinutes(11));
        Assert.False(_queue.CheckDuplicate("ABC-1D23").IsDuplicate);
    }

    [Fact]
    public void The_duplicate_memory_also_survives_a_snapshot_taken_after_the_clear()
    {
        Show("a", "ABC-1D23");
        _queue.ClearHistory();

        var restored = AlertQueue.FromSnapshot(_queue.ToSnapshot(), _clock);

        Assert.Empty(restored.History);
        Assert.True(restored.CheckDuplicate("ABC-1D23").IsDuplicate);
    }

    [Fact]
    public void Changed_is_raised_once_when_something_was_removed_and_not_at_all_when_empty()
    {
        var raised = 0;
        _queue.Changed += (_, _) => raised++;

        Assert.Equal(0, _queue.ClearHistory());
        Assert.Equal(0, raised);

        Show("a", null);
        var before = raised;
        _queue.ClearHistory();

        Assert.Equal(before + 1, raised);
    }
}
