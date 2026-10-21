using System.Text;
using System.Text.RegularExpressions;

namespace AlertaIBT.Core.Diagnostics;

/// <summary>How serious a log entry is.</summary>
public enum LogSeverity
{
    /// <summary>Anything that is not one of the others (debug, a line with no level).</summary>
    Other,

    /// <summary>Information.</summary>
    Info,

    /// <summary>A warning.</summary>
    Warning,

    /// <summary>An error or a fatal error.</summary>
    Error,
}

/// <summary>One entry of the log: its text (an exception's lines included) and how serious it is.</summary>
/// <param name="Text">The entry as written, one or more lines.</param>
/// <param name="Severity">The level read from the <c>[INF]</c>-style tag.</param>
public sealed record LogEntry(string Text, LogSeverity Severity);

/// <summary>
/// The daily log files (<c>alertaibt-yyyyMMdd.log</c>, written by Serilog) as the Diagnóstico area uses them: the newest entries to
/// read, the whole text to copy or export, and a way to clear them. The files stay open for writing by the running app, so they
/// are read with sharing and cleared by deleting what can be deleted and emptying the rest. Nothing here throws for a missing
/// folder or a file that is busy.
/// </summary>
public sealed partial class LogFiles
{
    // Only the end of a file matters for the view; a bigger file is read from its last part.
    private const int TailBytes = 768 * 1024;

    private readonly string _folder;

    /// <param name="folder">The logs folder (<c>%AppData%\AlertaIBT\logs</c>).</param>
    public LogFiles(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        _folder = folder;
    }

    /// <summary>The newest entries, newest first, at most <paramref name="maxEntries"/>.</summary>
    public IReadOnlyList<LogEntry> Newest(int maxEntries = 300)
    {
        List<LogEntry> result = [];
        foreach (var file in Files().AsEnumerable().Reverse())
        {
            var entries = Parse(ReadTail(file));
            for (var i = entries.Count - 1; i >= 0 && result.Count < maxEntries; i--)
            {
                result.Add(entries[i]);
            }

            if (result.Count >= maxEntries)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>All the kept log text, oldest first, to copy or export. Empty when there is none.</summary>
    public string AllText()
    {
        StringBuilder text = new();
        foreach (var file in Files())
        {
            var part = ReadAll(file);
            if (part.Length == 0)
            {
                continue;
            }

            text.Append(part);
            if (!part.EndsWith('\n'))
            {
                text.AppendLine();
            }
        }

        return text.ToString();
    }

    /// <summary>Writes <see cref="AllText"/> to <paramref name="path"/> (UTF-8). Throws the I/O error when the file cannot be written.</summary>
    public void ExportTo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        File.WriteAllText(path, AllText(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    /// <summary>Removes the log files; one the running app holds open is emptied instead. Returns how many files were touched.</summary>
    public int Clear()
    {
        var touched = 0;
        foreach (var file in Files())
        {
            try
            {
                File.Delete(file);
                touched++;
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Held open by the logger: empty it instead.
            }

            try
            {
                using var stream = new FileStream(file, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                touched++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Cannot be emptied either; the next try may.
            }
        }

        return touched;
    }

    /// <summary>The severity named by the <c>[INF]</c>, <c>[WRN]</c>, <c>[ERR]</c> or <c>[FTL]</c> tag of an entry's first line.</summary>
    public static LogSeverity SeverityOf(string entryText)
    {
        ArgumentNullException.ThrowIfNull(entryText);

        var match = LevelTag().Match(entryText);
        return match.Success
            ? match.Groups[1].Value switch
            {
                "INF" => LogSeverity.Info,
                "WRN" => LogSeverity.Warning,
                "ERR" or "FTL" => LogSeverity.Error,
                _ => LogSeverity.Other,
            }
            : LogSeverity.Other;
    }

    /// <summary>Splits log text into entries: a line that starts with a date begins one, other lines (an exception) continue it.</summary>
    public static IReadOnlyList<LogEntry> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<LogEntry> entries = [];
        StringBuilder? current = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (EntryStart().IsMatch(line))
            {
                Flush(entries, current);
                current = new StringBuilder(line);
            }
            else if (current is not null && line.Length > 0)
            {
                current.Append('\n').Append(line);
            }
            else if (current is null && line.Length > 0)
            {
                current = new StringBuilder(line); // the cut start of a tail
            }
        }

        Flush(entries, current);
        return entries;
    }

    private static void Flush(List<LogEntry> entries, StringBuilder? current)
    {
        if (current is null)
        {
            return;
        }

        var text = current.ToString();
        entries.Add(new LogEntry(text, SeverityOf(text)));
    }

    private List<string> Files()
    {
        try
        {
            return Directory.Exists(_folder)
                ? [.. Directory.EnumerateFiles(_folder, "alertaibt-*.log").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string ReadAll(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string ReadTail(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var cut = stream.Length > TailBytes;
            if (cut)
            {
                stream.Seek(-TailBytes, SeekOrigin.End);
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: !cut);
            var text = reader.ReadToEnd();
            if (!cut)
            {
                return text;
            }

            // Start at the first whole line (the first one is cut in the middle, maybe in the middle of a character).
            var newline = text.IndexOf('\n');
            return newline < 0 ? string.Empty : text[(newline + 1)..];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} ")]
    private static partial Regex EntryStart();

    [GeneratedRegex(@"^\S+ \S+ \[([A-Z]{3})\]")]
    private static partial Regex LevelTag();
}
