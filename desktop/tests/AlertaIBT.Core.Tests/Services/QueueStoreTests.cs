using System.Text;
using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Services;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Services;

public sealed class QueueStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-queue-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero));
    private readonly List<Exception> _errors = [];

    public QueueStoreTests() => Directory.CreateDirectory(_dir);

    private string Path_ => Path.Combine(_dir, "queue.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Leftover temp folder: the OS cleans it.
        }
    }

    private QueueStore Store() => new(Path_, _clock, _errors.Add);

    private QueueItem Item(string id, string? plate = "ABC-1D23", AlertSource source = AlertSource.Telegram, bool urgent = false) =>
        new(id, plate is null ? "COROLLA PRATA" : "COROLLA PRATA | PLACA: " + plate, plate, source, urgent, _clock.GetUtcNow(), "42", "corolla \"prata\" " + plate + " acentua\U000000E7\U000000E3o", Corrected: urgent);

    // ---- load ----

    [Fact]
    public void A_missing_file_is_an_empty_queue_and_no_error()
    {
        var snapshot = Store().Load();

        Assert.Empty(snapshot.Pending);
        Assert.Empty(snapshot.History);
        Assert.Empty(_errors);
    }

    [Fact]
    public void Save_and_load_round_trip_pending_and_history_exactly()
    {
        var store = Store();
        var pending = new[] { Item("a"), Item("b", "XYZ-1234", AlertSource.Pc, urgent: true), Item("c", null, AlertSource.Site) };
        var history = new[]
        {
            new HistoryEntry(Item("h1"), HistoryState.Shown, _clock.GetUtcNow(), null),
            new HistoryEntry(Item("h2"), HistoryState.Rejected, _clock.GetUtcNow().AddMinutes(-3), "faltou placa"),
            new HistoryEntry(Item("h3"), HistoryState.Cancelled, _clock.GetUtcNow(), HistoryReasons.Sermon),
            new HistoryEntry(Item("h4"), HistoryState.Expired, _clock.GetUtcNow(), null),
        };

        store.Save(new QueueSnapshot(pending, history));
        var loaded = Store().Load();

        Assert.Equal(pending, loaded.Pending);
        Assert.Equal(history, loaded.History);
        Assert.Empty(_errors);
    }

    [Fact]
    public void The_file_is_utf8_without_a_bom_and_keeps_accents_readable()
    {
        Store().Save(new QueueSnapshot([Item("a")], []));

        var bytes = File.ReadAllBytes(Path_);

        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Contains("acentua\U000000E7\U000000E3o", Encoding.UTF8.GetString(bytes));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"text\"")]
    [InlineData("{\"pending\": [{\"id\":\"a\",\"text\":\"X")]
    public void An_unusable_file_gives_an_empty_queue_and_is_kept_as_a_bad_copy(string content)
    {
        File.WriteAllText(Path_, content);

        var snapshot = Store().Load();

        Assert.Empty(snapshot.Pending);
        Assert.Empty(snapshot.History);
        Assert.False(File.Exists(Path_));
        var bad = Assert.Single(Directory.GetFiles(_dir, "queue.bad-*.json"));
        Assert.Equal(content, File.ReadAllText(bad));
    }

    [Fact]
    public void A_file_of_nul_bytes_is_unusable_too()
    {
        File.WriteAllBytes(Path_, new byte[2048]);

        var snapshot = Store().Load();

        Assert.Empty(snapshot.Pending);
        Assert.Single(Directory.GetFiles(_dir, "queue.bad-*.json"));
    }

    [Fact]
    public void Only_the_five_newest_bad_copies_are_kept()
    {
        for (var i = 0; i < 8; i++)
        {
            File.WriteAllText(Path_, "{ broken " + i);
            Store().Load();
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        var kept = Directory.GetFiles(_dir, "queue.bad-*.json");

        Assert.Equal(5, kept.Length);
        Assert.Contains(kept, f => File.ReadAllText(f) == "{ broken 7");
        Assert.DoesNotContain(kept, f => File.ReadAllText(f) == "{ broken 0");
    }

    [Fact]
    public void An_entry_that_makes_no_sense_is_skipped_and_its_neighbours_survive()
    {
        File.WriteAllText(Path_, """
            {
              "version": 1,
              "pending": [
                { "id": "ok1", "text": "GOL BRANCO", "plate": "AAA-1111", "source": "telegram", "urgent": true, "createdAt": "2026-10-25T08:50:00+00:00", "senderId": "7", "original": "gol" },
                { "id": 5, "text": "wrong type id" },
                { "text": "no id" },
                { "id": "empty", "text": "   " },
                { "id": "emoji", "text": "__CAR__" },
                "not an object",
                null,
                { "id": "ok1", "text": "DUPLICATE ID" },
                { "id": "ok2", "text": "line one\nline two __CAR__", "plate": "garbage", "source": "martian", "urgent": "yes", "createdAt": "not a date" }
              ],
              "history": [
                { "item": { "id": "h1", "text": "SHOWN" }, "state": "shown", "at": "2026-10-25T08:00:00+00:00" },
                { "item": { "id": "h2", "text": "X" }, "state": "exploded" },
                { "item": 3, "state": "shown" },
                { "state": "shown" },
                { "item": { "id": "h3", "text": "Y" }, "state": "REJECTED", "reason": "repetido" }
              ]
            }
            """.Replace("__CAR__", "\U0001F697"));

        var snapshot = Store().Load();

        Assert.Equal(["ok1", "ok2"], snapshot.Pending.Select(p => p.Id));
        var ok1 = snapshot.Pending[0];
        Assert.Equal("AAA-1111", ok1.Plate);
        Assert.True(ok1.Urgent);
        Assert.Equal(AlertSource.Telegram, ok1.Source);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 8, 50, 0, TimeSpan.Zero), ok1.CreatedAt);
        var ok2 = snapshot.Pending[1];
        Assert.Equal("LINE ONE LINE TWO", ok2.Text); // cleaned to one line, no emoji
        Assert.Null(ok2.Plate);
        Assert.Equal(AlertSource.Pc, ok2.Source);
        Assert.False(ok2.Urgent);
        Assert.Equal(_clock.GetUtcNow(), ok2.CreatedAt);
        Assert.Equal(["h1", "h3"], snapshot.History.Select(h => h.Item.Id));
        Assert.Equal(HistoryState.Rejected, snapshot.History[1].State);
        Assert.Empty(Directory.GetFiles(_dir, "queue.bad-*.json")); // the file itself was fine
    }

    [Fact]
    public void A_runaway_file_is_cut_to_a_sane_size()
    {
        var items = string.Join(",", Enumerable.Range(0, 500).Select(i => $$"""{"id":"i{{i}}","text":"T{{i}}"}"""));
        File.WriteAllText(Path_, "{\"pending\":[" + items + "]}");

        var snapshot = Store().Load();

        Assert.Equal(200, snapshot.Pending.Count);
        Assert.Equal("i0", snapshot.Pending[0].Id);
    }

    [Fact]
    public void A_file_held_open_by_another_program_is_readable_and_a_locked_one_does_not_throw()
    {
        Store().Save(new QueueSnapshot([Item("a")], []));
        using (new FileStream(Path_, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.Single(Store().Load().Pending);
        }

        using (new FileStream(Path_, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var snapshot = Store().Load();

            Assert.Empty(snapshot.Pending);
            Assert.NotEmpty(_errors);
        }

        Assert.True(File.Exists(Path_)); // a locked file is not a corrupt one: it is left alone
    }

    // ---- attach and save ----

    [Fact]
    public void Attach_writes_the_current_state_and_every_change_after_it()
    {
        var queue = new AlertQueue(_clock);
        using var store = Store();
        store.Attach(queue);
        store.Flush();
        Assert.True(File.Exists(Path_));
        Assert.Empty(Store().Load().Pending);

        queue.Enqueue(Item("a"), DuplicatePolicy.Warn);
        queue.Enqueue(Item("b", "XYZ-1234"), DuplicatePolicy.Warn);
        store.Flush();

        Assert.Equal(["a", "b"], Store().Load().Pending.Select(p => p.Id));
    }

    [Fact]
    public async Task Changes_are_saved_in_the_background_without_a_flush()
    {
        var queue = new AlertQueue(_clock);
        using var store = Store();
        store.Attach(queue);

        queue.Enqueue(Item("a"), DuplicatePolicy.Warn);

        await Wait.Until(() => File.Exists(Path_) && Store().Load().Pending.Count == 1, "the background save");
    }

    [Fact]
    public void The_alert_on_screen_is_saved_with_the_pending_ones_and_the_pause_is_never_saved()
    {
        var queue = new AlertQueue(_clock);
        using var store = Store();
        store.Attach(queue);
        queue.Enqueue(Item("a"), DuplicatePolicy.Warn);
        queue.Enqueue(Item("b", "XYZ-1234"), DuplicatePolicy.Warn);
        queue.StartNext();
        store.Flush();
        Assert.Equal(["a", "b"], Store().Load().Pending.Select(p => p.Id));

        queue.Pause();
        queue.Enqueue(Item("c", "QQQ-1111"), DuplicatePolicy.Warn); // refused: paused
        store.Flush();
        var text = File.ReadAllText(Path_);
        var restored = AlertQueue.FromSnapshot(Store().Load(), _clock);

        Assert.DoesNotContain("pause", text, StringComparison.OrdinalIgnoreCase);
        Assert.False(restored.IsPaused);
        Assert.Empty(restored.Pending);
        Assert.Equal(3, restored.History.Count); // a, b cancelled and c refused, all remembered
    }

    [Fact]
    public void A_restored_queue_continues_where_the_old_one_stopped()
    {
        var queue = new AlertQueue(_clock);
        using (var store = Store())
        {
            store.Attach(queue);
            queue.Enqueue(Item("a"), DuplicatePolicy.Warn);
            queue.Enqueue(Item("b", "XYZ-1234"), DuplicatePolicy.Warn);
            queue.StartNext();
            queue.FinishCurrent();
            queue.Enqueue(Item("c", "QQQ-1111"), DuplicatePolicy.Warn);
        } // Dispose saves the last state

        var restored = AlertQueue.FromSnapshot(Store().Load(), _clock);

        Assert.Equal(["b", "c"], restored.Pending.Select(p => p.Id));
        Assert.Equal(HistoryState.Shown, restored.History[0].State);
        Assert.Equal("a", restored.History[0].Item.Id);
        Assert.Equal(EnqueueKind.DuplicateBlocked, restored.Enqueue(Item("d"), DuplicatePolicy.Block).Kind); // "a" was shown just now
    }

    [Fact]
    public void Many_threads_changing_the_queue_end_with_the_file_equal_to_the_queue()
    {
        var queue = new AlertQueue(_clock);
        using var store = Store();
        store.Attach(queue);

        Parallel.For(0, 400, i =>
        {
            queue.Enqueue(Item("i" + i, null), DuplicatePolicy.Warn);
            if (i % 5 == 0)
            {
                queue.Remove("i" + (i / 2));
            }
        });
        store.Flush();

        var onDisk = Store().Load();
        var inMemory = queue.ToSnapshot();
        Assert.Equal(inMemory.Pending.Select(p => p.Id).Take(200), onDisk.Pending.Select(p => p.Id));
        Assert.Equal(Math.Min(200, inMemory.Pending.Count), onDisk.Pending.Count);
        Assert.Equal(inMemory.History.Select(h => h.Item.Id), onDisk.History.Select(h => h.Item.Id));
        Assert.Empty(_errors);
    }

    [Fact]
    public void Saving_leaves_no_temporary_file_and_never_a_half_written_one()
    {
        var queue = new AlertQueue(_clock);
        using var store = Store();
        store.Attach(queue);

        for (var i = 0; i < 30; i++)
        {
            queue.Enqueue(Item("i" + i, null), DuplicatePolicy.Warn);
            store.Flush();
            Assert.Equal(i + 1, Store().Load().Pending.Count); // always a complete, parseable file
        }

        Assert.Equal(["queue.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void A_failed_save_is_reported_not_thrown_and_the_next_change_tries_again()
    {
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x"); // a file where the folder should be
        var queue = new AlertQueue(_clock);
        using var store = new QueueStore(Path.Combine(blocker, "queue.json"), _clock, _errors.Add);

        store.Attach(queue);
        store.Flush();

        Assert.NotEmpty(_errors);
        File.Delete(blocker);
        queue.Enqueue(Item("a"), DuplicatePolicy.Warn);
        store.Flush();
        Assert.Single(new QueueStore(Path.Combine(blocker, "queue.json"), _clock).Load().Pending);
    }

    [Fact]
    public void An_error_callback_that_throws_cannot_break_the_store()
    {
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x");
        var queue = new AlertQueue(_clock);
        using var store = new QueueStore(Path.Combine(blocker, "queue.json"), _clock, _ => throw new InvalidOperationException("bad callback"));

        store.Attach(queue);
        store.Flush();
        queue.Enqueue(Item("a"), DuplicatePolicy.Warn);
        store.Flush();
    }

    [Fact]
    public void A_second_attach_is_refused_and_after_dispose_changes_are_ignored()
    {
        var queue = new AlertQueue(_clock);
        var store = Store();
        store.Attach(queue);
        Assert.Throws<InvalidOperationException>(() => store.Attach(new AlertQueue(_clock)));

        store.Dispose();
        queue.Enqueue(Item("late"), DuplicatePolicy.Warn);
        store.Flush();

        Assert.Empty(Store().Load().Pending);
        store.Dispose(); // twice is fine
    }
}
