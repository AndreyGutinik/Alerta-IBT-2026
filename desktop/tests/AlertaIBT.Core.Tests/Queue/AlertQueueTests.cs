using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Queue;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Queue;

public class AlertQueueTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero));
    private readonly AlertQueue _queue;
    private int _changed;

    public AlertQueueTests()
    {
        _queue = new AlertQueue(_clock);
        _queue.Changed += (_, _) => Interlocked.Increment(ref _changed);
    }

    private QueueItem Item(string id, string? plate = null, bool urgent = false, DateTimeOffset? createdAt = null) =>
        new(id, "TEXT " + id, plate, AlertSource.Pc, urgent, createdAt ?? _clock.GetUtcNow(), "sender", "original " + id);

    private static string[] Ids(IEnumerable<QueueItem> items) => items.Select(i => i.Id).ToArray();

    private EnqueueResult Add(string id, string? plate = null, bool urgent = false, DuplicatePolicy policy = DuplicatePolicy.Block) =>
        _queue.Enqueue(Item(id, plate, urgent), policy);

    private void Show(string id, string? plate = null)
    {
        Add(id, plate, policy: DuplicatePolicy.Warn);
        _queue.StartNext();
        _queue.FinishCurrent();
    }

    // ---- ordering ----

    [Fact]
    public void Enqueue_NormalItemsKeepArrivalOrderAndReportPosition()
    {
        var first = Add("a");
        var second = Add("b");
        var third = Add("c");

        Assert.Equal(["a", "b", "c"], Ids(_queue.Pending));
        Assert.Equal(new EnqueueResult(EnqueueKind.Queued, 1, null, false), first);
        Assert.Equal(2, second.Position);
        Assert.Equal(3, third.Position);
    }

    [Fact]
    public void Enqueue_UrgentGoesBeforeNormalsAndAfterEarlierUrgents()
    {
        Add("n1");
        Add("n2");
        var u1 = Add("u1", urgent: true);
        var u2 = Add("u2", urgent: true);
        var n3 = Add("n3");

        Assert.Equal(["u1", "u2", "n1", "n2", "n3"], Ids(_queue.Pending));
        Assert.Equal(1, u1.Position);
        Assert.Equal(2, u2.Position);
        Assert.Equal(5, n3.Position);
    }

    [Fact]
    public void Enqueue_UrgentOnEmptyQueueIsFirst()
    {
        var result = Add("u", urgent: true);

        Assert.Equal(1, result.Position);
        Assert.Equal(["u"], Ids(_queue.Pending));
    }

    // ---- pause ----

    [Fact]
    public void Pause_CancelsCurrentThenPendingInOrderAndReturnsThem()
    {
        Add("a");
        Add("b");
        Add("c");
        _queue.StartNext();

        var cancelled = _queue.Pause();

        Assert.Equal(["a", "b", "c"], Ids(cancelled));
        Assert.Null(_queue.Current);
        Assert.Empty(_queue.Pending);
        Assert.True(_queue.IsPaused);
        Assert.Equal(["a", "b", "c"], _queue.History.Select(h => h.Item.Id).ToArray());
        Assert.All(_queue.History, h =>
        {
            Assert.Equal(HistoryState.Cancelled, h.State);
            Assert.Equal(HistoryReasons.Sermon, h.Reason);
            Assert.Equal(_clock.GetUtcNow(), h.At);
        });
    }

    [Fact]
    public void Pause_WithoutCurrentCancelsOnlyPending()
    {
        Add("a");

        var cancelled = _queue.Pause();

        Assert.Equal(["a"], Ids(cancelled));
    }

    [Fact]
    public void Pause_WhenAlreadyPausedReturnsEmptyAndChangesNothing()
    {
        Add("a");
        _queue.Pause();
        var historyBefore = _queue.History;
        var changedBefore = _changed;

        var again = _queue.Pause();

        Assert.Empty(again);
        Assert.Equal(historyBefore, _queue.History);
        Assert.Equal(changedBefore, _changed);
    }

    [Fact]
    public void Pause_OnEmptyQueueStillPauses()
    {
        var cancelled = _queue.Pause();

        Assert.Empty(cancelled);
        Assert.True(_queue.IsPaused);
        Assert.Equal(1, _changed);
    }

    [Fact]
    public void Enqueue_WhilePausedIsRefusedNotQueuedAndRecordedAsRejected()
    {
        _queue.Pause();
        var item = Item("a", "ABC-1234");

        var result = _queue.Enqueue(item, DuplicatePolicy.Warn);

        Assert.Equal(new EnqueueResult(EnqueueKind.PausedRefused, 0, null, false), result);
        Assert.Empty(_queue.Pending);
        var entry = Assert.Single(_queue.History);
        Assert.Equal(item, entry.Item);
        Assert.Equal(HistoryState.Rejected, entry.State);
        Assert.Equal(HistoryReasons.Sermon, entry.Reason);
    }

    [Fact]
    public void Resume_AcceptsAlertsAgain()
    {
        _queue.Pause();
        _queue.Resume();

        var result = Add("a");

        Assert.False(_queue.IsPaused);
        Assert.Equal(EnqueueKind.Queued, result.Kind);
    }

    [Fact]
    public void Resume_WhenNotPausedDoesNotFireChanged()
    {
        _queue.Resume();

        Assert.False(_queue.IsPaused);
        Assert.Equal(0, _changed);
    }

    [Fact]
    public void HistoryReasons_HaveTheUserVisibleTexts()
    {
        Assert.Equal("prega\U000000E7\U000000E3o", HistoryReasons.Sermon);
        Assert.Equal("repetido", HistoryReasons.Duplicate);
        Assert.Equal("removido no PC", HistoryReasons.RemovedOnPc);
        Assert.Equal("cancelado no site", HistoryReasons.CancelledOnSite);
        Assert.Equal("fila limpa", HistoryReasons.QueueCleared);
    }

    // ---- duplicates ----

    [Fact]
    public void Duplicate_PendingPlateWithBlockIsRefusedAndRecordedAsRejected()
    {
        Add("a", "ABC-1234");
        var second = Item("b", "ABC-1234");

        var result = _queue.Enqueue(second, DuplicatePolicy.Block);

        Assert.Equal(new EnqueueResult(EnqueueKind.DuplicateBlocked, 0, null, true), result);
        Assert.Equal(["a"], Ids(_queue.Pending));
        var entry = Assert.Single(_queue.History);
        Assert.Equal(second, entry.Item);
        Assert.Equal(HistoryState.Rejected, entry.State);
        Assert.Equal(HistoryReasons.Duplicate, entry.Reason);
    }

    [Fact]
    public void Duplicate_PendingPlateWithWarnIsQueuedAndFlagged()
    {
        Add("a", "ABC-1234");

        var result = Add("b", "ABC-1234", policy: DuplicatePolicy.Warn);

        Assert.Equal(new EnqueueResult(EnqueueKind.Queued, 2, null, true), result);
        Assert.Equal(["a", "b"], Ids(_queue.Pending));
        Assert.Empty(_queue.History);
    }

    [Fact]
    public void Duplicate_PlateOnScreenCounts()
    {
        Add("a", "ABC-1234");
        _queue.StartNext();

        var result = Add("b", "ABC-1234");

        Assert.Equal(new EnqueueResult(EnqueueKind.DuplicateBlocked, 0, null, true), result);
    }

    [Fact]
    public void Duplicate_ShownNineMinutesAgoBlocksWithMinutesAgo()
    {
        Show("a", "ABC-1234");
        _clock.Advance(TimeSpan.FromMinutes(9));

        var result = Add("b", "ABC-1234");

        Assert.Equal(new EnqueueResult(EnqueueKind.DuplicateBlocked, 0, 9, true), result);
    }

    [Fact]
    public void Duplicate_MinutesAgoIsFloored()
    {
        Show("a", "ABC-1234");
        _clock.Advance(TimeSpan.FromSeconds(9 * 60 + 59));

        var result = Add("b", "ABC-1234");

        Assert.Equal(9, result.DuplicateMinutesAgo);
    }

    [Fact]
    public void Duplicate_ShownExactlyTenMinutesAgoStillCounts()
    {
        Show("a", "ABC-1234");
        _clock.Advance(TimeSpan.FromMinutes(10));

        var result = Add("b", "ABC-1234");

        Assert.Equal(new EnqueueResult(EnqueueKind.DuplicateBlocked, 0, 10, true), result);
    }

    [Fact]
    public void Duplicate_ShownElevenMinutesAgoDoesNotCount()
    {
        Show("a", "ABC-1234");
        _clock.Advance(TimeSpan.FromMinutes(11));

        var result = Add("b", "ABC-1234");

        Assert.Equal(new EnqueueResult(EnqueueKind.Queued, 1, null, false), result);
    }

    [Fact]
    public void Duplicate_ShownDuplicateWithWarnReportsMinutesAgo()
    {
        Show("a", "ABC-1234");
        _clock.Advance(TimeSpan.FromMinutes(4));

        var result = Add("b", "ABC-1234", policy: DuplicatePolicy.Warn);

        Assert.Equal(new EnqueueResult(EnqueueKind.Queued, 1, 4, true), result);
    }

    [Fact]
    public void Duplicate_PendingWinsOverShown()
    {
        Show("a", "ABC-1234");
        Add("b", "ABC-1234", policy: DuplicatePolicy.Warn);
        _clock.Advance(TimeSpan.FromMinutes(2));

        var result = Add("c", "ABC-1234");

        Assert.Equal(new EnqueueResult(EnqueueKind.DuplicateBlocked, 0, null, true), result);
    }

    [Fact]
    public void Duplicate_UsesTheMostRecentShownEntry()
    {
        Show("a", "ABC-1234");
        _clock.Advance(TimeSpan.FromMinutes(8));
        Show("b", "ABC-1234");
        _clock.Advance(TimeSpan.FromMinutes(5));

        var result = Add("c", "ABC-1234");

        Assert.Equal(5, result.DuplicateMinutesAgo);
        Assert.Equal(EnqueueKind.DuplicateBlocked, result.Kind);
    }

    [Fact]
    public void Duplicate_RejectedCancelledAndExpiredHistoryNeverCount()
    {
        _queue.AddRejected(Item("r", "AAA-1111"), "no");
        Add("c", "BBB-2222");
        _queue.Remove("c");
        Add("e", "CCC-3333");
        _clock.Advance(TimeSpan.FromMinutes(31));
        _queue.ExpireOld();

        Assert.Equal(EnqueueKind.Queued, Add("r2", "AAA-1111").Kind);
        Assert.Equal(EnqueueKind.Queued, Add("c2", "BBB-2222").Kind);
        Assert.Equal(EnqueueKind.Queued, Add("e2", "CCC-3333").Kind);
    }

    [Fact]
    public void Duplicate_ItemsWithoutPlateNeverDuplicate()
    {
        Add("a");
        var result = Add("b");

        Assert.Equal(new EnqueueResult(EnqueueKind.Queued, 2, null, false), result);
    }

    [Fact]
    public void Duplicate_PlatesCompareExactly()
    {
        Add("a", "ABC-1234");

        var result = Add("b", "ABC-1235");

        Assert.Equal(EnqueueKind.Queued, result.Kind);
    }

    // ---- start / finish / return ----

    [Fact]
    public void StartNext_TakesTheFirstPendingAsCurrent()
    {
        Add("a");
        Add("b");

        var started = _queue.StartNext();

        Assert.Equal("a", started?.Id);
        Assert.Equal("a", _queue.Current?.Id);
        Assert.Equal(["b"], Ids(_queue.Pending));
    }

    [Fact]
    public void StartNext_ReturnsNullWithoutChangeWhenEmpty()
    {
        var changedBefore = _changed;

        Assert.Null(_queue.StartNext());
        Assert.Equal(changedBefore, _changed);
    }

    [Fact]
    public void StartNext_ReturnsNullWithoutChangeWhileOneIsCurrent()
    {
        Add("a");
        Add("b");
        _queue.StartNext();
        var changedBefore = _changed;

        Assert.Null(_queue.StartNext());
        Assert.Equal("a", _queue.Current?.Id);
        Assert.Equal(["b"], Ids(_queue.Pending));
        Assert.Equal(changedBefore, _changed);
    }

    [Fact]
    public void StartNext_ReturnsNullWhilePaused()
    {
        Add("a");
        _queue.Pause();
        var changedBefore = _changed;

        Assert.Null(_queue.StartNext());
        Assert.Equal(changedBefore, _changed);
    }

    [Fact]
    public void FinishCurrent_RecordsShownAtNowAndClearsCurrent()
    {
        var item = Item("a", "ABC-1234");
        _queue.Enqueue(item, DuplicatePolicy.Block);
        _queue.StartNext();
        _clock.Advance(TimeSpan.FromSeconds(30));

        _queue.FinishCurrent();

        Assert.Null(_queue.Current);
        var entry = Assert.Single(_queue.History);
        Assert.Equal(item, entry.Item);
        Assert.Equal(HistoryState.Shown, entry.State);
        Assert.Equal(_clock.GetUtcNow(), entry.At);
        Assert.Null(entry.Reason);
    }

    [Fact]
    public void FinishCurrent_WithoutCurrentIsANoOp()
    {
        _queue.FinishCurrent();

        Assert.Empty(_queue.History);
        Assert.Equal(0, _changed);
    }

    [Fact]
    public void ReturnCurrentToFront_PutsItFirstEvenAheadOfUrgentsAndKeepsCreatedAt()
    {
        var created = _clock.GetUtcNow();
        Add("a");
        _queue.StartNext();
        _clock.Advance(TimeSpan.FromMinutes(1));
        Add("u", urgent: true);
        Add("n");

        _queue.ReturnCurrentToFront();

        Assert.Null(_queue.Current);
        Assert.Equal(["a", "u", "n"], Ids(_queue.Pending));
        Assert.Equal(created, _queue.Pending[0].CreatedAt);
        Assert.Empty(_queue.History);
    }

    [Fact]
    public void ReturnCurrentToFront_WithoutCurrentIsANoOp()
    {
        Add("a");
        var changedBefore = _changed;

        _queue.ReturnCurrentToFront();

        Assert.Equal(["a"], Ids(_queue.Pending));
        Assert.Equal(changedBefore, _changed);
    }

    // ---- expiry ----

    [Fact]
    public void ExpireOld_DropsPendingOlderThanThirtyMinutesAsExpired()
    {
        var old = Item("old", createdAt: _clock.GetUtcNow());
        _queue.Enqueue(old, DuplicatePolicy.Block);
        _clock.Advance(TimeSpan.FromMinutes(31));

        var expired = _queue.ExpireOld();

        Assert.Equal([old], expired);
        Assert.Empty(_queue.Pending);
        var entry = Assert.Single(_queue.History);
        Assert.Equal(HistoryState.Expired, entry.State);
        Assert.Null(entry.Reason);
        Assert.Equal(_clock.GetUtcNow(), entry.At);
    }

    [Fact]
    public void ExpireOld_KeepsTwentyNineMinutesAndExactlyThirty()
    {
        Add("a");
        _clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Empty(_queue.ExpireOld());

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(_queue.ExpireOld());

        Assert.Equal(["a"], Ids(_queue.Pending));
        Assert.Empty(_queue.History);
    }

    [Fact]
    public void ExpireOld_OnlyDropsTheOldOnesInQueueOrder()
    {
        Add("a");
        Add("b");
        _clock.Advance(TimeSpan.FromMinutes(20));
        Add("c");
        _clock.Advance(TimeSpan.FromMinutes(11));
        Add("d");

        var expired = _queue.ExpireOld();

        Assert.Equal(["a", "b"], Ids(expired));
        Assert.Equal(["c", "d"], Ids(_queue.Pending));
        Assert.Equal(["a", "b"], _queue.History.Select(h => h.Item.Id).ToArray());
    }

    [Fact]
    public void ExpireOld_NeverExpiresTheCurrent()
    {
        Add("a");
        _queue.StartNext();
        _clock.Advance(TimeSpan.FromHours(2));

        Assert.Empty(_queue.ExpireOld());
        Assert.Equal("a", _queue.Current?.Id);
    }

    [Fact]
    public void ExpireOld_CountsFromCreatedAtNotFromEnqueue()
    {
        var created = _clock.GetUtcNow() - TimeSpan.FromMinutes(40);
        _queue.Enqueue(Item("a", createdAt: created), DuplicatePolicy.Block);

        Assert.Equal(["a"], Ids(_queue.ExpireOld()));
    }

    [Fact]
    public void ExpireOld_WithNothingToExpireDoesNotFireChanged()
    {
        Add("a");
        var changedBefore = _changed;

        _queue.ExpireOld();

        Assert.Equal(changedBefore, _changed);
    }

    // ---- operator and sender actions ----

    [Fact]
    public void Move_UpAndDownByDelta()
    {
        Add("a");
        Add("b");
        Add("c");

        Assert.True(_queue.Move("c", -2));
        Assert.Equal(["c", "a", "b"], Ids(_queue.Pending));

        Assert.True(_queue.Move("c", 1));
        Assert.Equal(["a", "c", "b"], Ids(_queue.Pending));
    }

    [Fact]
    public void Move_ClampsToTheEnds()
    {
        Add("a");
        Add("b");
        Add("c");

        Assert.True(_queue.Move("b", -10));
        Assert.Equal(["b", "a", "c"], Ids(_queue.Pending));

        Assert.True(_queue.Move("b", 10));
        Assert.Equal(["a", "c", "b"], Ids(_queue.Pending));
    }

    [Fact]
    public void Move_AnItemMayPassUrgents()
    {
        Add("u", urgent: true);
        Add("n");

        Assert.True(_queue.Move("n", -1));

        Assert.Equal(["n", "u"], Ids(_queue.Pending));
    }

    [Fact]
    public void Move_UnknownOrCurrentIdIsFalseAndSilent()
    {
        Add("a");
        _queue.StartNext();
        var changedBefore = _changed;

        Assert.False(_queue.Move("a", 1));
        Assert.False(_queue.Move("zzz", 1));
        Assert.Equal(changedBefore, _changed);
    }

    [Fact]
    public void Move_ThatChangesNothingReturnsTrueWithoutFiringChanged()
    {
        Add("a");
        Add("b");
        var changedBefore = _changed;

        Assert.True(_queue.Move("a", -1));
        Assert.True(_queue.Move("b", 0));
        Assert.True(_queue.Move("b", int.MaxValue));
        Assert.True(_queue.Move("a", int.MinValue));

        Assert.Equal(["a", "b"], Ids(_queue.Pending));
        Assert.Equal(changedBefore, _changed);
    }

    [Fact]
    public void Remove_CancelsPendingWithRemovedOnPcReason()
    {
        Add("a");
        Add("b");

        Assert.True(_queue.Remove("a"));

        Assert.Equal(["b"], Ids(_queue.Pending));
        var entry = Assert.Single(_queue.History);
        Assert.Equal("a", entry.Item.Id);
        Assert.Equal(HistoryState.Cancelled, entry.State);
        Assert.Equal(HistoryReasons.RemovedOnPc, entry.Reason);
    }

    [Fact]
    public void Cancel_CancelsPendingWithCancelledOnSiteReason()
    {
        Add("a");

        Assert.True(_queue.Cancel("a"));

        Assert.Empty(_queue.Pending);
        var entry = Assert.Single(_queue.History);
        Assert.Equal(HistoryState.Cancelled, entry.State);
        Assert.Equal(HistoryReasons.CancelledOnSite, entry.Reason);
    }

    [Fact]
    public void RemoveAndCancel_AreFalseForUnknownOrCurrentIds()
    {
        Add("a");
        _queue.StartNext();
        var changedBefore = _changed;

        Assert.False(_queue.Remove("a"));
        Assert.False(_queue.Cancel("a"));
        Assert.False(_queue.Remove("zzz"));
        Assert.False(_queue.Cancel("zzz"));

        Assert.Equal("a", _queue.Current?.Id);
        Assert.Empty(_queue.History);
        Assert.Equal(changedBefore, _changed);
    }

    [Fact]
    public void Clear_CancelsEveryPendingAndLeavesTheCurrent()
    {
        Add("a");
        Add("b");
        Add("c");
        _queue.StartNext();

        _queue.Clear();

        Assert.Empty(_queue.Pending);
        Assert.Equal("a", _queue.Current?.Id);
        Assert.Equal(["b", "c"], _queue.History.Select(h => h.Item.Id).ToArray());
        Assert.All(_queue.History, h =>
        {
            Assert.Equal(HistoryState.Cancelled, h.State);
            Assert.Equal(HistoryReasons.QueueCleared, h.Reason);
        });
    }

    [Fact]
    public void Clear_OnEmptyQueueDoesNotFireChanged()
    {
        _queue.Clear();

        Assert.Equal(0, _changed);
    }

    [Fact]
    public void AddRejected_RecordsRejectedWithTheReason()
    {
        var item = Item("a");

        _queue.AddRejected(item, "faltou placa");

        var entry = Assert.Single(_queue.History);
        Assert.Equal(item, entry.Item);
        Assert.Equal(HistoryState.Rejected, entry.State);
        Assert.Equal("faltou placa", entry.Reason);
        Assert.Equal(_clock.GetUtcNow(), entry.At);
        Assert.Empty(_queue.Pending);
    }

    // ---- history ----

    [Fact]
    public void History_IsNewestFirst()
    {
        _queue.AddRejected(Item("old"), "x");
        _clock.Advance(TimeSpan.FromSeconds(1));
        _queue.AddRejected(Item("new"), "x");

        Assert.Equal(["new", "old"], _queue.History.Select(h => h.Item.Id).ToArray());
    }

    [Fact]
    public void History_KeepsOnlyTheLastFifty()
    {
        for (var i = 1; i <= 55; i++)
        {
            _queue.AddRejected(Item("i" + i), "x");
        }

        var history = _queue.History;
        Assert.Equal(50, history.Count);
        Assert.Equal("i55", history[0].Item.Id);
        Assert.Equal("i6", history[49].Item.Id);
    }

    [Fact]
    public void History_AMultiEntryOperationNearTheLimitKeepsItsOrderAndDropsTheOldest()
    {
        for (var i = 1; i <= 49; i++)
        {
            _queue.AddRejected(Item("i" + i), "x");
        }

        Add("a");
        Add("b");
        Add("c");
        _queue.Clear();

        var history = _queue.History;
        Assert.Equal(50, history.Count);
        Assert.Equal(["a", "b", "c"], history.Take(3).Select(h => h.Item.Id).ToArray());
        Assert.Equal("i3", history[49].Item.Id);
    }

    [Fact]
    public void PendingAndHistory_ReturnCopies()
    {
        Add("a");
        var pending = _queue.Pending;
        var history = _queue.History;

        Add("b");
        _queue.Remove("a");

        Assert.Equal(["a"], Ids(pending));
        Assert.Empty(history);
    }

    // ---- snapshot ----

    [Fact]
    public void Snapshot_PutsCurrentFirstThenPendingAndKeepsHistory()
    {
        Add("done", "AAA-1111");
        _queue.StartNext();
        _queue.FinishCurrent();
        _queue.AddRejected(Item("rej"), "no");
        Add("n1");
        Add("n2");
        Add("u", urgent: true);
        _queue.StartNext();

        var snapshot = _queue.ToSnapshot();

        Assert.Equal("u", _queue.Current?.Id);
        Assert.Equal(["u", "n1", "n2"], Ids(snapshot.Pending));
        Assert.Equal(["rej", "done"], snapshot.History.Select(h => h.Item.Id).ToArray());
    }

    [Fact]
    public void FromSnapshot_RestoresPendingAndHistoryWithoutCurrentOrPause()
    {
        Add("done", "AAA-1111");
        _queue.StartNext();
        _queue.FinishCurrent();
        Add("cur");
        Add("p1");
        _queue.StartNext();
        var snapshot = _queue.ToSnapshot();

        var restored = AlertQueue.FromSnapshot(snapshot, _clock);

        Assert.Null(restored.Current);
        Assert.False(restored.IsPaused);
        Assert.Equal(["cur", "p1"], Ids(restored.Pending));
        Assert.Equal(snapshot.History, restored.History);
        Assert.Equal(EnqueueKind.Queued, restored.Enqueue(Item("new"), DuplicatePolicy.Block).Kind);
    }

    [Fact]
    public void FromSnapshot_DoesNotRestoreAPause()
    {
        _queue.Pause();

        var restored = AlertQueue.FromSnapshot(_queue.ToSnapshot(), _clock);

        Assert.False(restored.IsPaused);
    }

    [Fact]
    public void FromSnapshot_KeepsOnlyTheFirstFiftyHistoryEntries()
    {
        var entries = Enumerable.Range(1, 60)
            .Select(i => new HistoryEntry(Item("h" + i), HistoryState.Rejected, _clock.GetUtcNow(), "x"))
            .ToList();

        var restored = AlertQueue.FromSnapshot(new QueueSnapshot([], entries), _clock);

        Assert.Equal(50, restored.History.Count);
        Assert.Equal("h1", restored.History[0].Item.Id);
        Assert.Equal("h50", restored.History[49].Item.Id);
    }

    [Fact]
    public void FromSnapshot_RestoredPendingStillExpiresAndBlocksDuplicates()
    {
        var created = _clock.GetUtcNow();
        var snapshot = new QueueSnapshot([Item("a", "ABC-1234", createdAt: created)], []);
        var restored = AlertQueue.FromSnapshot(snapshot, _clock);

        Assert.Equal(EnqueueKind.DuplicateBlocked, restored.Enqueue(Item("b", "ABC-1234"), DuplicatePolicy.Block).Kind);

        _clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(["a"], Ids(restored.ExpireOld()));
    }

    [Fact]
    public void FromSnapshot_CopiesTheInputLists()
    {
        var pending = new List<QueueItem> { Item("a") };
        var restored = AlertQueue.FromSnapshot(new QueueSnapshot(pending, []), _clock);

        pending.Add(Item("b"));

        Assert.Equal(["a"], Ids(restored.Pending));
    }

    // ---- Changed ----

    [Fact]
    public void Changed_FiresOncePerCallThatChangesState()
    {
        Add("a");
        Assert.Equal(1, _changed);

        Add("b");
        Assert.Equal(2, _changed);

        _queue.StartNext();
        Assert.Equal(3, _changed);

        _queue.ReturnCurrentToFront();
        Assert.Equal(4, _changed);

        _queue.StartNext();
        _queue.FinishCurrent();
        Assert.Equal(6, _changed);

        _queue.Move("b", 0);
        Assert.Equal(6, _changed);

        _queue.Remove("b");
        Assert.Equal(7, _changed);

        _queue.AddRejected(Item("r"), "x");
        Assert.Equal(8, _changed);
    }

    [Fact]
    public void Changed_FiresOnRefusedEnqueueBecauseTheHistoryChanged()
    {
        Add("a", "ABC-1234");
        var before = _changed;

        Add("b", "ABC-1234");
        Assert.Equal(before + 1, _changed);

        _queue.Pause();
        before = _changed;
        Add("c");
        Assert.Equal(before + 1, _changed);
    }

    [Fact]
    public void Changed_FiresOnceForPauseResumeClearAndExpire()
    {
        Add("a");
        Add("b");
        var before = _changed;

        _queue.Clear();
        Assert.Equal(before + 1, _changed);

        Add("c");
        _clock.Advance(TimeSpan.FromMinutes(31));
        before = _changed;
        _queue.ExpireOld();
        Assert.Equal(before + 1, _changed);

        Add("d");
        before = _changed;
        _queue.Pause();
        Assert.Equal(before + 1, _changed);

        _queue.Resume();
        Assert.Equal(before + 2, _changed);
    }

    [Fact]
    public void Changed_RunsAfterTheLockIsReleased()
    {
        var reentered = false;
        var queue = new AlertQueue(_clock);
        queue.Changed += (_, _) =>
        {
            // A handler on another thread would deadlock here if the lock were still held.
            var task = Task.Run(() =>
            {
                _ = queue.Pending;
                reentered = true;
            });
            Assert.True(task.Wait(TimeSpan.FromSeconds(5)));
        };

        queue.Enqueue(Item("a"), DuplicatePolicy.Block);

        Assert.True(reentered);
    }

    // ---- threads ----

    [Fact]
    public void Enqueue_FromManyThreadsNeverLosesOrDuplicatesAnItem()
    {
        const int threads = 8;
        const int perThread = 250;

        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            for (var i = 0; i < perThread; i++)
            {
                _queue.Enqueue(Item($"t{t}-{i}", urgent: i % 5 == 0), DuplicatePolicy.Block);
            }
        });

        var pending = _queue.Pending;
        Assert.Equal(threads * perThread, pending.Count);
        Assert.Equal(threads * perThread, pending.Select(p => p.Id).Distinct().Count());
        Assert.Equal(threads * perThread, _changed);

        // Urgents still sit before every normal item.
        var firstNormal = pending.ToList().FindIndex(p => !p.Urgent);
        Assert.All(pending.Skip(firstNormal), p => Assert.False(p.Urgent));
    }

    [Fact]
    public void MixedOperationsFromManyThreadsKeepEveryItemInExactlyOnePlace()
    {
        const int total = 400;
        var removedIds = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, total, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            var id = "m" + i;
            _queue.Enqueue(Item(id), DuplicatePolicy.Block);
            _queue.Move(id, -1);
            if (i % 3 == 0 && _queue.Remove(id))
            {
                removedIds.Add(id);
            }
        });

        var pending = Ids(_queue.Pending);
        Assert.Equal(total, pending.Length + removedIds.Count);
        Assert.Equal(pending.Length, pending.Distinct().Count());
        Assert.Empty(pending.Intersect(removedIds));
        Assert.Equal(50, _queue.History.Count);
        Assert.All(_queue.History, h => Assert.Contains(h.Item.Id, removedIds));
    }
}
