using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Services;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Queue;

// The "same plate within 10 minutes" rule keeps its own memory: the 50-line history may push a shown plate out.
public sealed class AlertQueueRecentShownTests : IDisposable
{
    private const string Plate = "ABC-1234";

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero));
    private readonly AlertQueue _queue;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-recent-" + Guid.NewGuid().ToString("N"));

    public AlertQueueRecentShownTests()
    {
        _queue = new AlertQueue(_clock);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // The OS cleans the temp folder.
        }
    }

    private QueueItem Item(string id, string? plate) =>
        new(id, "TEXT " + id, plate, AlertSource.Telegram, false, _clock.GetUtcNow(), "42", "original " + id);

    private void ShowPlate(string id = "shown")
    {
        _queue.Enqueue(Item(id, Plate), DuplicatePolicy.Warn);
        _queue.StartNext();
        _queue.FinishCurrent();
    }

    private void RejectMany(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _queue.AddRejected(Item("r" + i, null), HistoryReasons.TooLong);
        }
    }

    [Fact]
    public void SixtyRejectedEntriesAfterAShownPlate_DoNotLetTheDuplicateThrough()
    {
        ShowPlate();
        _clock.Advance(TimeSpan.FromMinutes(1));

        RejectMany(60);

        Assert.DoesNotContain(_queue.History, entry => entry.State == HistoryState.Shown); // pushed out of the 50 lines
        var again = _queue.Enqueue(Item("again", Plate), DuplicatePolicy.Block);
        Assert.Equal(EnqueueKind.DuplicateBlocked, again.Kind);
        Assert.Equal(1, again.DuplicateMinutesAgo);
        Assert.True(_queue.CheckDuplicate(Plate).IsDuplicate);
    }

    [Fact]
    public void CancelledEntriesCannotEvictItEither()
    {
        ShowPlate();
        for (var i = 0; i < 55; i++)
        {
            _queue.Enqueue(Item("c" + i, null), DuplicatePolicy.Warn);
            _queue.Remove("c" + i);
        }

        Assert.Equal(EnqueueKind.DuplicateBlocked, _queue.Enqueue(Item("again", Plate), DuplicatePolicy.Block).Kind);
    }

    [Fact]
    public void AtExactlyTenMinutesItIsStillADuplicate_AfterElevenItIsAllowed()
    {
        ShowPlate();
        RejectMany(60);

        _clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(EnqueueKind.DuplicateBlocked, _queue.Enqueue(Item("a", Plate), DuplicatePolicy.Block).Kind);

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(_queue.CheckDuplicate(Plate).IsDuplicate);
        Assert.Equal(EnqueueKind.Queued, _queue.Enqueue(Item("b", Plate), DuplicatePolicy.Block).Kind);
    }

    [Fact]
    public void ShowingTheSamePlateAgain_RestartsTheTenMinutes()
    {
        ShowPlate("first");
        _clock.Advance(TimeSpan.FromMinutes(9));
        ShowPlate("second");
        _clock.Advance(TimeSpan.FromMinutes(9));

        Assert.True(_queue.CheckDuplicate(Plate).IsDuplicate);
        Assert.Equal(9, _queue.CheckDuplicate(Plate).MinutesAgo);
    }

    [Fact]
    public void OldPlatesArePrunedFromTheSnapshot()
    {
        ShowPlate();
        _clock.Advance(TimeSpan.FromMinutes(11));

        var snapshot = _queue.ToSnapshot();

        Assert.True(snapshot.RecentShown is null || snapshot.RecentShown.Count == 0);
    }

    [Fact]
    public void ARejectedOrCancelledPlate_IsNotRemembered()
    {
        _queue.AddRejected(Item("r", Plate), HistoryReasons.TooLong);
        _queue.Enqueue(Item("c", "XYZ-9999"), DuplicatePolicy.Warn);
        _queue.Remove("c");

        Assert.False(_queue.CheckDuplicate(Plate).IsDuplicate);
        Assert.False(_queue.CheckDuplicate("XYZ-9999").IsDuplicate);
    }

    // ---- snapshot ----

    [Fact]
    public void SnapshotRoundTrip_KeepsThePlateEvenWhenTheHistoryLostIt()
    {
        ShowPlate();
        _clock.Advance(TimeSpan.FromMinutes(2));
        RejectMany(60);

        var restored = AlertQueue.FromSnapshot(_queue.ToSnapshot(), _clock);

        Assert.Equal(EnqueueKind.DuplicateBlocked, restored.Enqueue(Item("again", Plate), DuplicatePolicy.Block).Kind);
        _clock.Advance(TimeSpan.FromMinutes(9));
        Assert.False(restored.CheckDuplicate(Plate).IsDuplicate);
    }

    [Fact]
    public void ThroughTheQueueFile_ItSurvivesARestart()
    {
        ShowPlate();
        _clock.Advance(TimeSpan.FromMinutes(2));
        RejectMany(60);
        var path = Path.Combine(_dir, "queue.json");
        new QueueStore(path, _clock).Save(_queue.ToSnapshot());

        var loaded = new QueueStore(path, _clock).Load();
        var restored = AlertQueue.FromSnapshot(loaded, _clock);

        Assert.Equal(EnqueueKind.DuplicateBlocked, restored.Enqueue(Item("again", Plate), DuplicatePolicy.Block).Kind);
        Assert.Equal(2, restored.CheckDuplicate(Plate).MinutesAgo);
    }

    [Fact]
    public void AnOldFileWithoutTheField_StillLoads_AndTheShownHistoryStillCounts()
    {
        var path = Path.Combine(_dir, "queue.json");
        var at = _clock.GetUtcNow().AddMinutes(-3).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(
            path,
            "{\"version\":1,\"pending\":[],\"history\":[{\"item\":{\"id\":\"h1\",\"text\":\"GOL | PLACA: ABC-1234\",\"plate\":\"ABC-1234\",\"source\":\"telegram\",\"urgent\":false,"
            + "\"createdAt\":\"" + at + "\",\"senderId\":\"42\",\"original\":\"gol\",\"corrected\":false},\"state\":\"shown\",\"at\":\"" + at + "\",\"reason\":null}]}");

        var loaded = new QueueStore(path, _clock).Load();
        var restored = AlertQueue.FromSnapshot(loaded, _clock);

        Assert.Null(loaded.RecentShown);
        Assert.Single(loaded.History);
        Assert.Equal(EnqueueKind.DuplicateBlocked, restored.Enqueue(Item("again", Plate), DuplicatePolicy.Block).Kind);
        Assert.Equal(3, restored.CheckDuplicate(Plate).MinutesAgo);
    }

    [Fact]
    public void AMalformedRecentShownField_IsIgnoredWithoutLosingTheRest()
    {
        var path = Path.Combine(_dir, "queue.json");
        File.WriteAllText(path, "{\"version\":1,\"pending\":[],\"history\":[],\"recentShown\":{\"ABC-1234\":\"not a date\",\"XYZ-9999\":5,\"BAD\":\"2026-10-21T08:59:00+00:00\"}}");

        var loaded = new QueueStore(path, _clock).Load();

        Assert.True(loaded.RecentShown is null || loaded.RecentShown.Count == 0);
    }
}
