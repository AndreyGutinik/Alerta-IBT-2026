using AlertaIBT.Core.Telegram;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Telegram;

// telegram-journal.json: the messages the bot may delete, kept for the 48 hours Telegram allows.
public sealed class TelegramJournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-journal-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero));
    private readonly List<Exception> _errors = [];

    public TelegramJournalTests() => Directory.CreateDirectory(_dir);

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

    private string JournalFile => Path.Combine(_dir, "telegram-journal.json");

    private TelegramJournal Journal() => new(JournalFile, _clock, _errors.Add);

    [Fact]
    public void An_added_message_is_in_the_snapshot_and_survives_a_new_instance()
    {
        Journal().Add(-100, 7, _clock.GetUtcNow());

        var entry = Assert.Single(Journal().Snapshot());

        Assert.Equal(new JournalEntry(-100, 7, _clock.GetUtcNow()), entry);
    }

    [Fact]
    public void The_same_message_is_not_added_twice_and_the_same_id_in_another_chat_is_another_message()
    {
        var journal = Journal();

        journal.Add(-100, 7, _clock.GetUtcNow());
        journal.Add(-100, 7, _clock.GetUtcNow());
        journal.Add(-200, 7, _clock.GetUtcNow());

        Assert.Equal(2, journal.Snapshot().Count);
    }

    [Fact]
    public void Entries_older_than_48_hours_are_dropped_on_every_write_and_never_shown()
    {
        var journal = Journal();
        journal.Add(-100, 1, _clock.GetUtcNow() - TimeSpan.FromHours(10));
        journal.Add(-100, 2, _clock.GetUtcNow() - TimeSpan.FromHours(47.9));

        _clock.Advance(TimeSpan.FromHours(1)); // message 2 is now 48.9 h old
        Assert.Equal([1L], journal.Snapshot().Select(e => e.MessageId)); // not shown even before the next write

        journal.Add(-100, 3, _clock.GetUtcNow());

        var onDisk = Journal().Snapshot();
        Assert.Equal([1L, 3L], onDisk.Select(e => e.MessageId));
        Assert.DoesNotContain("\"messageId\": 2,", File.ReadAllText(JournalFile), StringComparison.Ordinal);
    }

    [Fact]
    public void A_message_that_is_already_older_than_48_hours_is_not_kept()
    {
        var journal = Journal();

        journal.Add(-100, 1, _clock.GetUtcNow() - TimeSpan.FromHours(49));

        Assert.Empty(journal.Snapshot());
    }

    [Fact]
    public void Saving_leaves_no_temporary_file_and_a_valid_json_document()
    {
        var journal = Journal();
        for (var i = 0; i < 20; i++)
        {
            journal.Add(-100, i, _clock.GetUtcNow());
        }

        Assert.Equal([JournalFile], Directory.GetFiles(_dir));
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(JournalFile));
        Assert.Equal(20, document.RootElement.GetProperty("entries").GetArrayLength());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"entries\": 5}")]
    [InlineData("{\"entries\": [")]
    public void A_damaged_file_is_an_empty_journal_and_the_next_write_repairs_it(string content)
    {
        File.WriteAllText(JournalFile, content);
        var journal = Journal();

        Assert.Empty(journal.Snapshot());
        journal.Add(-100, 9, _clock.GetUtcNow());

        Assert.Equal([9L], Journal().Snapshot().Select(e => e.MessageId));
    }

    [Fact]
    public void An_entry_that_makes_no_sense_is_skipped_without_losing_its_neighbours()
    {
        var good = _clock.GetUtcNow().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(
            JournalFile,
            "{\"entries\":[{\"chatId\":-1,\"messageId\":1,\"sentAt\":\"" + good + "\"},{\"chatId\":\"x\"},5,"
            + "{\"chatId\":-1,\"messageId\":2,\"sentAt\":\"garbage\"},{\"chatId\":-1,\"messageId\":3,\"sentAt\":\"" + good + "\"}]}");

        Assert.Equal([1L, 3L], Journal().Snapshot().Select(e => e.MessageId));
    }

    [Fact]
    public void Clear_empties_the_journal_in_memory_and_on_disk()
    {
        var journal = Journal();
        journal.Add(-100, 1, _clock.GetUtcNow());

        journal.Clear();

        Assert.Empty(journal.Snapshot());
        Assert.Empty(Journal().Snapshot());
    }

    [Fact]
    public void A_file_that_cannot_be_written_never_throws_and_is_reported()
    {
        Directory.CreateDirectory(JournalFile); // a folder where the file should be
        var journal = Journal();

        journal.Add(-100, 1, _clock.GetUtcNow());
        journal.Clear();

        Assert.NotEmpty(_errors);
    }

    [Fact]
    public async Task Many_threads_adding_at_once_lose_nothing()
    {
        var journal = Journal();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(
            () =>
            {
                for (var i = 0; i < 25; i++)
                {
                    journal.Add(-100, (t * 100) + i, _clock.GetUtcNow());
                }
            },
            TestContext.Current.CancellationToken)));

        Assert.Equal(200, journal.Snapshot().Count);
        Assert.Equal(200, Journal().Snapshot().Count);
    }
}
