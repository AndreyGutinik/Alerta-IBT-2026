using AlertaIBT.Core.Telegram;
using AlertaIBT.Core.Tests.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Telegram;

// "Limpar historico" on Telegram: only the journal, only the young, never the pinned one, never a throw.
public sealed class TelegramHistoryCleanerTests : IDisposable
{
    private const long Chat = -1001234567890;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-tgclean-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero));
    private readonly CapturingLogger _log = new();
    private readonly FakeMessages _api = new();
    private readonly TelegramJournal _journal;
    private readonly TelegramHistoryCleaner _cleaner;

    public TelegramHistoryCleanerTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new TelegramJournal(Path.Combine(_dir, "telegram-journal.json"), _clock);
        _cleaner = new TelegramHistoryCleaner(_journal, _api, _clock, _log);
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

    private sealed class FakeMessages : ITelegramMessages
    {
        public bool IsConfigured { get; set; } = true;

        public List<(long Chat, long Id)> Deleted { get; } = [];

        public List<long> PinnedAsked { get; } = [];

        public long? Pinned { get; set; }

        public bool PinnedFails { get; set; }

        public Func<long, TelegramException?> Refuse { get; set; } = _ => null;

        public Task DeleteMessageAsync(long chatId, long messageId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Refuse(messageId) is { } refusal)
            {
                throw refusal;
            }

            Deleted.Add((chatId, messageId));
            return Task.CompletedTask;
        }

        public Task<long?> GetPinnedMessageIdAsync(long chatId, CancellationToken ct)
        {
            PinnedAsked.Add(chatId);
            return PinnedFails ? throw new TelegramException("HTTP 400 ao falar com o Telegram: Bad Request", 400) : Task.FromResult(Pinned);
        }
    }

    private void Note(long id, double hoursAgo = 1, long chat = Chat) =>
        _journal.Add(chat, id, _clock.GetUtcNow() - TimeSpan.FromHours(hoursAgo));

    private string Summary() => _log.Entries.Single(e => e.Message.StartsWith("Telegram: ", StringComparison.Ordinal) && e.Message.Contains("apagadas", StringComparison.Ordinal)).Message;

    [Fact]
    public async Task Every_journaled_message_is_deleted_and_the_journal_is_emptied()
    {
        Note(1);
        Note(2);
        Note(3);

        var result = await _cleaner.ClearAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new TelegramClearResult(3, 0), result);
        Assert.Equal([1L, 2L, 3L], _api.Deleted.Select(d => d.Id));
        Assert.Empty(_journal.Snapshot());
        Assert.Equal("Telegram: 3 apagadas, 0 n\U000000E3o puderam ser apagadas", Summary());
    }

    [Fact]
    public async Task Nothing_outside_the_journal_is_ever_deleted()
    {
        Note(10);

        await _cleaner.ClearAsync(TestContext.Current.CancellationToken);

        Assert.Equal([10L], _api.Deleted.Select(d => d.Id));
    }

    [Fact]
    public async Task The_pinned_message_is_kept_even_when_it_is_in_the_journal()
    {
        Note(1);
        Note(2);
        Note(3);
        _api.Pinned = 2;

        var result = await _cleaner.ClearAsync(TestContext.Current.CancellationToken);

        Assert.Equal([1L, 3L], _api.Deleted.Select(d => d.Id));
        Assert.Equal(new TelegramClearResult(2, 0), result);
    }

    [Fact]
    public async Task The_pinned_message_is_asked_once_per_chat()
    {
        Note(1);
        Note(2);
        Note(3, chat: -200);

        await _cleaner.ClearAsync(TestContext.Current.CancellationToken);

        Assert.Equal([Chat, -200L], _api.PinnedAsked);
    }

    [Fact]
    public async Task A_failure_to_find_the_pinned_message_is_ignored()
    {
        Note(1);
        _api.PinnedFails = true;

        var result = await _cleaner.ClearAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new TelegramClearResult(1, 0), result);
    }

    [Fact]
    public async Task A_message_the_bot_may_not_delete_is_counted_and_the_others_still_go()
    {
        Note(1);
        Note(2);
        Note(3);
        _api.Refuse = id => id == 2 ? new TelegramException("HTTP 403 ao falar com o Telegram: Forbidden", 403) : null;

        var result = await _cleaner.ClearAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new TelegramClearResult(2, 1), result);
        Assert.Equal([1L, 3L], _api.Deleted.Select(d => d.Id));
        Assert.Empty(_journal.Snapshot());
        Assert.Equal("Telegram: 2 apagadas, 1 n\U000000E3o puderam ser apagadas", Summary());
    }

    [Fact]
    public async Task A_message_somebody_already_deleted_is_not_a_failure()
    {
        Note(1);
        Note(2);
        _api.Refuse = id => id == 1 ? new TelegramException("HTTP 400 ao falar com o Telegram: Bad Request: message to delete not found", 400) : null;

        var result = await _cleaner.ClearAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new TelegramClearResult(1, 0), result);
    }

    [Fact]
    public async Task A_message_between_47_and_48_hours_old_is_not_even_tried_and_counts_as_left()
    {
        Note(1, hoursAgo: 46.5);
        Note(2, hoursAgo: 47.5);

        var result = await _cleaner.ClearAsync(TestContext.Current.CancellationToken);

        Assert.Equal([1L], _api.Deleted.Select(d => d.Id));
        Assert.Equal(new TelegramClearResult(1, 1), result);
        Assert.Empty(_journal.Snapshot());
    }

    [Fact]
    public async Task Without_internet_it_gives_up_after_three_failures_in_a_row_and_does_not_throw()
    {
        for (var i = 1; i <= 10; i++)
        {
            Note(i);
        }

        var attempts = 0;
        _api.Refuse = _ =>
        {
            attempts++;
            return new TelegramException("Sem conex\U000000E3o com o Telegram");
        };

        var result = await _cleaner.ClearAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, attempts);
        Assert.Equal(new TelegramClearResult(0, 10), result);
    }

    [Fact]
    public async Task Too_many_requests_stops_the_run()
    {
        for (var i = 1; i <= 6; i++)
        {
            Note(i);
        }

        _api.Refuse = id => id == 2 ? new TelegramException("HTTP 429 ao falar com o Telegram", 429) : null;

        var result = await _cleaner.ClearAsync(TestContext.Current.CancellationToken);

        Assert.Equal([1L], _api.Deleted.Select(d => d.Id));
        Assert.Equal(new TelegramClearResult(1, 5), result);
    }

    [Fact]
    public async Task Without_a_token_nothing_is_called_and_the_journal_is_kept_for_later()
    {
        Note(1);
        _api.IsConfigured = false;

        var result = await _cleaner.ClearAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new TelegramClearResult(0, 0), result);
        Assert.Empty(_api.Deleted);
        Assert.Single(_journal.Snapshot());
    }

    [Fact]
    public async Task An_empty_journal_calls_nothing()
    {
        var result = await _cleaner.ClearAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new TelegramClearResult(0, 0), result);
        Assert.Empty(_api.PinnedAsked);
    }

    [Fact]
    public async Task A_cancelled_run_does_not_throw_and_keeps_the_journal()
    {
        Note(1);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await _cleaner.ClearAsync(cts.Token);

        Assert.Equal(0, result.Deleted);
        Assert.Single(_journal.Snapshot());
    }

    [Fact]
    public async Task An_unexpected_exception_is_logged_and_not_thrown()
    {
        Note(1);
        _api.Refuse = _ => throw new InvalidOperationException("boom");

        var result = await _cleaner.ClearAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Deleted);
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Error);
    }
}
