using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Queue;

namespace AlertaIBT.Core.Services;

/// <summary>
/// <c>queue.json</c>: the alerts still waiting and the history, saved after every change of the queue so a restart (or a
/// crash) does not lose them. The pause is never saved: a stale pause would silently block the next Sunday's alerts.
/// </summary>
/// <remarks>
/// <para>
/// Saving is atomic (a temporary file replaces the real one) and runs in the background, coalescing changes that arrive
/// while a write is under way; <see cref="Flush"/> and <see cref="Dispose"/> write at once. Loading never throws: a missing
/// file is an empty queue, an unusable one is moved to <c>queue.bad-yyyyMMdd-HHmmss-fff.json</c> (the five newest are
/// kept) and the queue starts empty, and an entry that makes no sense is skipped without losing its neighbours.
/// </para>
/// <para>The text of every restored alert is cleaned again, so a hand-edited file cannot put a multi-line text on the bar.</para>
/// </remarks>
public sealed class QueueStore : IDisposable
{
    private const int FormatVersion = 1;
    private const int MaxPending = 200;
    private const int MaxHistory = 50;
    private const int MaxRecentShown = 200;
    private const int BadCopiesKept = 5;
    private const long MaxFileBytes = 5 * 1024 * 1024;

    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly Action<Exception>? _onError;
    private readonly object _saveGate = new();
    private readonly object _attachGate = new();

    private AlertQueue? _queue;
    private int _dirty;
    private int _scheduled;
    private volatile bool _disposed;

    /// <param name="path">The queue file; its folder is created on the first save.</param>
    /// <param name="clock">Dates the bad copies and stands in for a missing creation time; the system clock by default.</param>
    /// <param name="onError">Told about a load or save that failed; it must not throw.</param>
    public QueueStore(string path, TimeProvider? clock = null, Action<Exception>? onError = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = path;
        _clock = clock ?? TimeProvider.System;
        _onError = onError;
    }

    /// <summary>The queue file path.</summary>
    public string FilePath => _path;

    /// <summary>Reads the file. Never throws; see the class remarks.</summary>
    public QueueSnapshot Load()
    {
        string text;
        try
        {
            if (!File.Exists(_path))
            {
                return Empty;
            }

            if (new FileInfo(_path).Length > MaxFileBytes)
            {
                MoveAside();
                return Empty;
            }

            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report(ex);
            return Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("The root of queue.json is not an object.");
            }

            return Read(document.RootElement);
        }
        catch (JsonException ex)
        {
            Report(ex);
            MoveAside();
            return Empty;
        }
    }

    /// <summary>Writes <paramref name="snapshot"/> atomically.</summary>
    /// <exception cref="IOException">The file could not be written; the old file is intact.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder or file is not writable; the old file is intact.</exception>
    public void Save(QueueSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_saveGate)
        {
            WriteFile(Serialize(snapshot));
        }
    }

    /// <summary>Starts saving <paramref name="queue"/> after each change, and saves its current state once.</summary>
    public void Attach(AlertQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);

        lock (_attachGate)
        {
            if (_queue is not null)
            {
                throw new InvalidOperationException("The store is already attached to a queue.");
            }

            _queue = queue;
            queue.Changed += OnChanged;
        }

        Schedule();
    }

    /// <summary>Writes the queue now if a change is waiting to be saved. Never throws; a failure goes to the error callback and the change stays pending.</summary>
    public void Flush() => _ = TryFlush();

    /// <summary>Stops listening to the queue and saves its last state.</summary>
    public void Dispose()
    {
        AlertQueue? queue;
        lock (_attachGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            queue = _queue;
            _queue = null;
        }

        if (queue is null)
        {
            return;
        }

        queue.Changed -= OnChanged;
        lock (_saveGate)
        {
            // Waits for a write under way, then writes the very last state if a change is still unsaved.
            if (Interlocked.Exchange(ref _dirty, 0) == 1)
            {
                SaveSnapshotLocked(queue);
            }
        }
    }

    private static QueueSnapshot Empty => new([], []);

    private void OnChanged(object? sender, EventArgs e) => Schedule();

    private void Schedule()
    {
        Volatile.Write(ref _dirty, 1);
        if (_disposed || Interlocked.CompareExchange(ref _scheduled, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(Drain);
    }

    // Writes until no change is waiting. A change that arrives while the last write is under way starts one more round.
    private void Drain()
    {
        try
        {
            bool saved;
            do
            {
                saved = TryFlush();
                Volatile.Write(ref _scheduled, 0);
            }
            while (saved && Volatile.Read(ref _dirty) == 1 && !_disposed && Interlocked.CompareExchange(ref _scheduled, 1, 0) == 0);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _scheduled, 0);
            Report(ex);
        }
    }

    // False when the write failed (the change stays pending). The state is read inside the lock, after the flag is
    // cleared, so the newest change always wins.
    private bool TryFlush()
    {
        lock (_saveGate)
        {
            AlertQueue? queue;
            lock (_attachGate)
            {
                queue = _queue;
            }

            if (queue is null || Interlocked.Exchange(ref _dirty, 0) == 0)
            {
                return true;
            }

            return SaveSnapshotLocked(queue);
        }
    }

    private bool SaveSnapshotLocked(AlertQueue queue)
    {
        try
        {
            WriteFile(Serialize(queue.ToSnapshot()));
            return true;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _dirty, 1); // the next change (or Flush) tries again
            Report(ex);
            return false;
        }
    }

    private void WriteFile(byte[] bytes)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(directory);

        var temp = _path + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
            {
                File.Replace(temp, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, _path);
            }
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static byte[] Serialize(QueueSnapshot snapshot)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", FormatVersion);
            writer.WriteStartArray("pending");
            foreach (var item in snapshot.Pending)
            {
                WriteItem(writer, item);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("history");
            foreach (var entry in snapshot.History.Take(MaxHistory))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("item");
                WriteItem(writer, entry.Item);
                writer.WriteString("state", entry.State.ToString().ToLowerInvariant());
                writer.WriteString("at", entry.At.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteString("reason", entry.Reason);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            if (snapshot.RecentShown is { Count: > 0 } recent)
            {
                writer.WriteStartObject("recentShown");
                foreach (var (plate, at) in recent.OrderByDescending(entry => entry.Value).Take(MaxRecentShown))
                {
                    writer.WriteString(plate, at.ToString("O", CultureInfo.InvariantCulture));
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static void WriteItem(Utf8JsonWriter writer, QueueItem item)
    {
        writer.WriteStartObject();
        writer.WriteString("id", item.Id);
        writer.WriteString("text", item.Text);
        writer.WriteString("plate", item.Plate);
        writer.WriteString("source", item.Source.ToString().ToLowerInvariant());
        writer.WriteBoolean("urgent", item.Urgent);
        writer.WriteString("createdAt", item.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteString("senderId", item.SenderId);
        writer.WriteString("original", item.Original);
        writer.WriteBoolean("corrected", item.Corrected);
        writer.WriteEndObject();
    }

    private QueueSnapshot Read(JsonElement root)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        List<QueueItem> pending = [];
        if (root.TryGetProperty("pending", out var pendingElement) && pendingElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in pendingElement.EnumerateArray())
            {
                if (pending.Count >= MaxPending)
                {
                    break;
                }

                if (ReadItem(element) is { } item && ids.Add(item.Id))
                {
                    pending.Add(item);
                }
            }
        }

        List<HistoryEntry> history = [];
        if (root.TryGetProperty("history", out var historyElement) && historyElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in historyElement.EnumerateArray())
            {
                if (history.Count >= MaxHistory)
                {
                    break;
                }

                if (ReadEntry(element) is { } entry)
                {
                    history.Add(entry);
                }
            }
        }

        return new QueueSnapshot(pending, history, ReadRecentShown(root));
    }

    // Optional (a file from before it existed has none): a plate that cannot be a plate or a date that cannot be read is skipped.
    private static Dictionary<string, DateTimeOffset>? ReadRecentShown(JsonElement root)
    {
        if (!root.TryGetProperty("recentShown", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        Dictionary<string, DateTimeOffset> recent = new(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (recent.Count >= MaxRecentShown)
            {
                break;
            }

            if (property.Value.ValueKind == JsonValueKind.String
                && PlateParser.Normalize(property.Name) is { } plate
                && DateTimeOffset.TryParse(property.Value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
            {
                recent[plate] = at;
            }
        }

        return recent.Count == 0 ? null : recent;
    }

    private HistoryEntry? ReadEntry(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("item", out var itemElement)
            || ReadItem(itemElement) is not { } item
            || GetString(element, "state") is not { } stateText
            || !Enum.TryParse<HistoryState>(stateText, ignoreCase: true, out var state)
            || !Enum.IsDefined(state))
        {
            return null;
        }

        var at = GetString(element, "at") is { } atText
            && DateTimeOffset.TryParse(atText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : _clock.GetUtcNow();
        return new HistoryEntry(item, state, at, GetString(element, "reason"));
    }

    private QueueItem? ReadItem(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || GetString(element, "id") is not { Length: > 0 } id
            || GetString(element, "text") is not { } rawText)
        {
            return null;
        }

        var text = AlertFormatter.FormatFree(rawText);
        if (text.Length == 0)
        {
            return null;
        }

        var plate = GetString(element, "plate") is { } plateText ? PlateParser.Normalize(plateText) : null;
        var source = GetString(element, "source") is { } sourceText
            && Enum.TryParse<AlertSource>(sourceText, ignoreCase: true, out var parsedSource)
            && Enum.IsDefined(parsedSource)
                ? parsedSource
                : AlertSource.Pc;
        var urgent = element.TryGetProperty("urgent", out var urgentElement) && urgentElement.ValueKind == JsonValueKind.True;
        var createdAt = GetString(element, "createdAt") is { } createdText
            && DateTimeOffset.TryParse(createdText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedCreated)
                ? parsedCreated
                : _clock.GetUtcNow();
        var corrected = element.TryGetProperty("corrected", out var correctedElement) && correctedElement.ValueKind == JsonValueKind.True;
        return new QueueItem(id, text, plate, source, urgent, createdAt, GetString(element, "senderId"), GetString(element, "original"), corrected);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Moves the unusable file to a dated name, so nothing is lost and the next start is clean.
    private void MoveAside()
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            var stem = Path.GetFileNameWithoutExtension(_path);
            var stamp = _clock.GetLocalNow().ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            var target = Path.Combine(directory, $"{stem}.bad-{stamp}.json");
            for (var n = 1; File.Exists(target); n++)
            {
                target = Path.Combine(directory, $"{stem}.bad-{stamp}-{n}.json");
            }

            try
            {
                File.Move(_path, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                File.Copy(_path, target);
            }

            foreach (var old in Directory.GetFiles(directory, $"{stem}.bad-*.json")
                         .OrderByDescending(file => file, StringComparer.Ordinal)
                         .Skip(BadCopiesKept))
            {
                TryDelete(old);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report(ex);
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file is overwritten by the next save.
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
            // The error callback must not throw; if it does there is nowhere left to report it.
        }
    }
}
