using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AlertaIBT.Core.Telegram;

/// <summary>One message of the group the app knows about.</summary>
/// <param name="ChatId">The chat the message is in.</param>
/// <param name="MessageId">The message's id inside its chat.</param>
/// <param name="SentAt">When it was sent.</param>
public sealed record JournalEntry(long ChatId, long MessageId, DateTimeOffset SentAt);

/// <summary>
/// <c>telegram-journal.json</c>: the messages "Limpar histórico" may delete from the group, which are only the ones the app itself
/// saw: every reply the bot sent and every <c>#</c> alert message the engine took. Nothing else is ever deleted, so the pinned
/// instructions of the group are safe. Telegram refuses to delete a message older than 48 hours, so older entries are dropped on
/// every write.
/// </summary>
/// <remarks>
/// Saving is atomic (a temporary file replaces the real one). Loading never throws: a missing, damaged or unreadable file is an
/// empty journal, and an entry that makes no sense is skipped. Safe to use from several threads.
/// </remarks>
public sealed class TelegramJournal
{
    /// <summary>How long an entry is kept: the age after which Telegram no longer lets a bot delete the message.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(48);

    private const int FormatVersion = 1;
    private const int MaxEntries = 5000;
    private const long MaxFileBytes = 2 * 1024 * 1024;

    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly Action<Exception>? _onError;
    private readonly object _gate = new();
    private List<JournalEntry>? _entries;

    /// <param name="path">The journal file; its folder is created on the first save.</param>
    /// <param name="clock">Ages the entries; the system clock by default.</param>
    /// <param name="onError">Told about a save that failed; it must not throw.</param>
    public TelegramJournal(string path, TimeProvider? clock = null, Action<Exception>? onError = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = path;
        _clock = clock ?? TimeProvider.System;
        _onError = onError;
    }

    /// <summary>The journal file path.</summary>
    public string FilePath => _path;

    /// <summary>Notes a message. A message already in the journal is not added twice. Never throws.</summary>
    /// <param name="chatId">The chat.</param>
    /// <param name="messageId">The message id.</param>
    /// <param name="sentAt">When it was sent.</param>
    public void Add(long chatId, long messageId, DateTimeOffset sentAt)
    {
        lock (_gate)
        {
            var entries = Entries();
            if (!entries.Any(e => e.ChatId == chatId && e.MessageId == messageId))
            {
                entries.Add(new JournalEntry(chatId, messageId, sentAt));
            }

            Prune(entries);
            if (entries.Count > MaxEntries)
            {
                entries.RemoveRange(0, entries.Count - MaxEntries);
            }

            Save(entries);
        }
    }

    /// <summary>The entries that are still young enough to be deleted, oldest first (a copy).</summary>
    public IReadOnlyList<JournalEntry> Snapshot()
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            return Entries().Where(e => now - e.SentAt <= Retention).ToArray();
        }
    }

    /// <summary>Forgets every entry and saves the empty journal. Never throws.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            var entries = Entries();
            entries.Clear();
            Save(entries);
        }
    }

    // Caller holds the lock.
    private List<JournalEntry> Entries() => _entries ??= Load();

    // Caller holds the lock.
    private void Prune(List<JournalEntry> entries)
    {
        var now = _clock.GetUtcNow();
        entries.RemoveAll(e => now - e.SentAt > Retention);
    }

    private List<JournalEntry> Load()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > MaxFileBytes)
            {
                return [];
            }

            string text;
            using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true))
            {
                text = reader.ReadToEnd();
            }

            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("entries", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var entries = new List<JournalEntry>();
            foreach (var item in list.EnumerateArray())
            {
                if (TryRead(item, out var entry))
                {
                    entries.Add(entry);
                }
            }

            return entries;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged or unreadable journal is an empty one: the worst that happens is that old messages stay in the group.
            Report(ex);
            return [];
        }
    }

    private static bool TryRead(JsonElement item, out JournalEntry entry)
    {
        entry = null!;
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("chatId", out var chat) || chat.ValueKind != JsonValueKind.Number || !chat.TryGetInt64(out var chatId)
            || !item.TryGetProperty("messageId", out var message) || message.ValueKind != JsonValueKind.Number || !message.TryGetInt64(out var messageId)
            || !item.TryGetProperty("sentAt", out var sent) || sent.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(sent.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var sentAt))
        {
            return false;
        }

        entry = new JournalEntry(chatId, messageId, sentAt);
        return true;
    }

    // Caller holds the lock. Writes to a temporary file and moves it over the real one.
    private void Save(List<JournalEntry> entries)
    {
        var temp = _path + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var array = new JsonArray();
            foreach (var entry in entries)
            {
                array.Add(new JsonObject
                {
                    ["chatId"] = entry.ChatId,
                    ["messageId"] = entry.MessageId,
                    ["sentAt"] = entry.SentAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                });
            }

            var root = new JsonObject { ["version"] = FormatVersion, ["entries"] = array };
            File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report(ex);
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // The temporary file stays; the next save overwrites it.
            }
        }
    }

    private void Report(Exception ex)
    {
        try
        {
            _onError?.Invoke(ex);
        }
        catch (Exception)
        {
            // The error callback is the last resort.
        }
    }
}
