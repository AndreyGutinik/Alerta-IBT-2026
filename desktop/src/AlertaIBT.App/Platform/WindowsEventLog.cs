using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Globalization;
using AlertaIBT.Core.Diagnostics;

namespace AlertaIBT.App.Platform;

/// <summary>
/// Reads, read-only and without admin rights, the few events of the Windows logs that tell how the previous run of the app ended: in the System log the
/// unexpected shutdown (Kernel-Power 41, EventLog 6008), the shutdown or restart request (User32 1074), entering sleep (Kernel-Power 42) and resume (107); in the
/// Application log a crash of this program (Application Error 1000, .NET Runtime 1026). The read runs on its own thread and is given up after a timeout (the
/// event log service can be slow right after a boot), and it never throws.
/// </summary>
public static class WindowsEventLog
{
    private const string SystemIds = "EventID=41 or EventID=6008 or EventID=1074 or EventID=42 or EventID=107";
    private const string ApplicationIds = "EventID=1000 or EventID=1026";

    /// <summary>The events between <paramref name="from"/> and <paramref name="to"/>; <c>Available</c> is false when the log could not be read in time.</summary>
    public static (IReadOnlyList<WindowsEvent> Events, bool Available) Read(DateTimeOffset from, DateTimeOffset to, TimeSpan timeout)
    {
        var result = new List<WindowsEvent>();
        var available = false;
        EventLogReader? active = null;
        var worker = Task.Run(() =>
        {
            try
            {
                foreach (var (log, ids) in new[] { ("System", SystemIds), ("Application", ApplicationIds) })
                {
                    var query = new EventLogQuery(log, PathType.LogName, $"*[System[({ids}) and TimeCreated[@SystemTime>='{Utc(from)}' and @SystemTime<='{Utc(to)}']]]");
                    using var reader = new EventLogReader(query);
                    active = reader;
                    for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
                    {
                        using (record)
                        {
                            if (Convert(log, record) is { } found)
                            {
                                lock (result)
                                {
                                    result.Add(found);
                                }
                            }
                        }
                    }
                }

                available = true;
            }
            catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
            {
                // Not readable (no permission, the service is busy, the reader was cancelled): the caller says so.
            }
        });

        if (!worker.Wait(timeout))
        {
            try
            {
                active?.CancelReading();
            }
            catch (Exception ex) when (ex is EventLogException or InvalidOperationException or ObjectDisposedException)
            {
                // Already finished.
            }

            return (Snapshot(result), false);
        }

        return (Snapshot(result), available);
    }

    private static IReadOnlyList<WindowsEvent> Snapshot(List<WindowsEvent> events)
    {
        lock (events)
        {
            return [.. events];
        }
    }

    private static string Utc(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static WindowsEvent? Convert(string log, EventRecord record)
    {
        var at = record.TimeCreated is { } created ? new DateTimeOffset(created.ToUniversalTime(), TimeSpan.Zero) : DateTimeOffset.MinValue;
        var provider = record.ProviderName ?? string.Empty;
        var detail = string.Empty;
        try
        {
            if (log == "System" && record.Id == 1074)
            {
                // Properties: process, computer, reason, reason code, shutdown type ("restart", "power off"), comment, user. Only the program and the type are kept (no user name).
                var process = record.Properties.Count > 0 ? Path.GetFileName(record.Properties[0].Value?.ToString() ?? string.Empty) : string.Empty;
                var kind = record.Properties.Count > 4 ? record.Properties[4].Value?.ToString() ?? string.Empty : string.Empty;
                detail = string.Join(", ", new[] { kind, process.Length > 0 ? "pedido por " + process : string.Empty }.Where(t => t.Length > 0));
            }
            else if (log == "Application")
            {
                // Application Error 1000 names the program first; the .NET Runtime 1026 text names it inside the description. Only our own crashes count.
                var first = record.Properties.Count > 0 ? record.Properties[0].Value?.ToString() ?? string.Empty : string.Empty;
                var mine = first.Contains("AlertaIBT", StringComparison.OrdinalIgnoreCase)
                    || (record.Id == 1026 && (record.FormatDescription() ?? string.Empty).Contains("AlertaIBT", StringComparison.OrdinalIgnoreCase));
                if (!mine)
                {
                    return null;
                }

                var module = record.Id == 1000 && record.Properties.Count > 3 ? record.Properties[3].Value?.ToString() ?? string.Empty : string.Empty;
                detail = module.Length > 0 ? "modulo " + module : string.Empty;
            }
        }
        catch (Exception ex) when (ex is EventLogException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            // The text of an event could not be formatted: the event itself still counts.
        }

        return new WindowsEvent(at, log, provider, record.Id, detail);
    }
}
