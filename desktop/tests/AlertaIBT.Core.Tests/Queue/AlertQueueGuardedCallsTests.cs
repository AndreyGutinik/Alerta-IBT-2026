using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Queue;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Queue;

// The calls added for the alert service: a duplicate check that queues nothing, and "act on the current alert only if it is still this one".
public class AlertQueueGuardedCallsTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero));
    private readonly AlertQueue _queue;
    private int _changed;

    public AlertQueueGuardedCallsTests()
    {
        _queue = new AlertQueue(_clock);
        _queue.Changed += (_, _) => Interlocked.Increment(ref _changed);
    }

    private QueueItem Item(string id, string? plate = null) =>
        new(id, "TEXT " + id, plate, AlertSource.Pc, false, _clock.GetUtcNow(), null, null);

    // ---- CheckDuplicate ----

    [Fact]
    public void CheckDuplicate_sees_pending_current_and_recently_shown_plates_and_queues_nothing()
    {
        Assert.Equal(new DuplicateCheck(false, null), _queue.CheckDuplicate("AAA-1111"));
        Assert.Equal(new DuplicateCheck(false, null), _queue.CheckDuplicate(null));

        _queue.Enqueue(Item("a", "AAA-1111"), DuplicatePolicy.Warn);
        Assert.Equal(new DuplicateCheck(true, null), _queue.CheckDuplicate("AAA-1111"));
        _queue.StartNext();
        Assert.Equal(new DuplicateCheck(true, null), _queue.CheckDuplicate("AAA-1111"));
        _queue.FinishCurrent();
        _clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(new DuplicateCheck(true, 4), _queue.CheckDuplicate("AAA-1111"));
        _clock.Advance(TimeSpan.FromMinutes(7));
        Assert.Equal(new DuplicateCheck(false, null), _queue.CheckDuplicate("AAA-1111"));

        Assert.Empty(_queue.Pending);
        Assert.Single(_queue.History);
    }

    [Fact]
    public void CheckDuplicate_does_not_raise_changed()
    {
        _queue.Enqueue(Item("a", "AAA-1111"), DuplicatePolicy.Warn);
        var before = _changed;

        _queue.CheckDuplicate("AAA-1111");

        Assert.Equal(before, _changed);
    }

    // ---- TryFinishCurrent / TryReturnCurrentToFront ----

    [Fact]
    public void TryFinishCurrent_finishes_only_the_named_alert()
    {
        _queue.Enqueue(Item("a"), DuplicatePolicy.Warn);
        _queue.StartNext();

        Assert.False(_queue.TryFinishCurrent("other"));
        Assert.Equal("a", _queue.Current?.Id);
        Assert.True(_queue.TryFinishCurrent("a"));
        Assert.Null(_queue.Current);
        Assert.Equal(HistoryState.Shown, _queue.History[0].State);
        Assert.False(_queue.TryFinishCurrent("a")); // nothing on screen any more
        Assert.Single(_queue.History);
    }

    [Fact]
    public void TryFinishCurrent_after_a_pause_does_nothing()
    {
        _queue.Enqueue(Item("a"), DuplicatePolicy.Warn);
        _queue.StartNext();
        _queue.Pause();
        var before = _changed;

        Assert.False(_queue.TryFinishCurrent("a"));

        Assert.Equal(before, _changed);
        Assert.Equal(HistoryState.Cancelled, Assert.Single(_queue.History).State);
    }

    [Fact]
    public void TryReturnCurrentToFront_returns_only_the_named_alert_ahead_of_the_rest()
    {
        _queue.Enqueue(Item("a"), DuplicatePolicy.Warn);
        _queue.Enqueue(Item("b"), DuplicatePolicy.Warn);
        _queue.StartNext();

        Assert.False(_queue.TryReturnCurrentToFront("b"));
        Assert.True(_queue.TryReturnCurrentToFront("a"));

        Assert.Null(_queue.Current);
        Assert.Equal(["a", "b"], _queue.Pending.Select(p => p.Id));
        Assert.False(_queue.TryReturnCurrentToFront("a"));
        Assert.Empty(_queue.History);
    }

    // ---- TryPause ----

    [Fact]
    public void TryPause_is_true_only_for_the_call_that_paused_even_with_nothing_to_cancel()
    {
        Assert.True(_queue.TryPause(out var first));
        Assert.Empty(first);
        Assert.True(_queue.IsPaused);

        Assert.False(_queue.TryPause(out var second));
        Assert.Empty(second);
    }

    [Fact]
    public void TryPause_returns_the_current_alert_first_then_the_pending_ones()
    {
        _queue.Enqueue(Item("a"), DuplicatePolicy.Warn);
        _queue.Enqueue(Item("b"), DuplicatePolicy.Warn);
        _queue.Enqueue(Item("c"), DuplicatePolicy.Warn);
        _queue.StartNext();

        Assert.True(_queue.TryPause(out var cancelled));

        Assert.Equal(["a", "b", "c"], cancelled.Select(i => i.Id));
        Assert.Equal(3, _queue.History.Count(h => h.State == HistoryState.Cancelled && h.Reason == HistoryReasons.Sermon));
    }

    [Fact]
    public void Concurrent_pauses_have_exactly_one_winner()
    {
        _queue.Enqueue(Item("a"), DuplicatePolicy.Warn);
        var winners = 0;

        Parallel.For(0, 64, i =>
        {
            if (_queue.TryPause(out _))
            {
                Interlocked.Increment(ref winners);
            }
        });

        Assert.Equal(1, winners);
    }

    [Fact]
    public void MissingFields_reads_like_portuguese()
    {
        Assert.Equal("faltou placa", HistoryReasons.MissingFields(["placa"]));
        Assert.Equal("faltou cor e placa", HistoryReasons.MissingFields(["cor", "placa"]));
        Assert.Equal("faltou modelo, cor e placa", HistoryReasons.MissingFields(["modelo", "cor", "placa"]));
        Assert.Equal("faltou campos", HistoryReasons.MissingFields([]));
    }
}
