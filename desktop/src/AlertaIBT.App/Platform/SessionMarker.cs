using System.Globalization;
using System.IO;

namespace AlertaIBT.App.Platform;

/// <summary>What the marker file said about the run before this one.</summary>
/// <param name="Existed">Whether a marker was found (false on the first run after an installation).</param>
/// <param name="EndedCleanly">Whether that run wrote its end line (a normal exit, an update restart, Windows ending the session).</param>
/// <param name="StartedAt">When it started, when known.</param>
/// <param name="Version">The version that ran, when known.</param>
/// <param name="EndReason">Why it ended, when it ended cleanly.</param>
public sealed record PreviousRun(bool Existed, bool EndedCleanly, DateTimeOffset? StartedAt, string? Version, string? EndReason)
{
    /// <summary>The previous run left no end line: a crash, a kill from the Task Manager, a power cut.</summary>
    public bool EndedAbnormally => Existed && !EndedCleanly;
}

/// <summary>
/// A tiny file (<c>session.txt</c> in the data folder) that tells the NEXT start how this run ended. It is written when the app starts (<c>started=</c>)
/// and a line is added when the app exits for any normal reason (<c>ended=</c> with the reason). A start that finds a marker without the end line knows
/// the previous run died abruptly and says so in the log. It never throws: a marker that cannot be written only means the next start learns nothing.
/// </summary>
public sealed class SessionMarker(string file, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Reads what the previous run left and starts the marker of this one.</summary>
    /// <param name="version">The running version, written for the next start's log line.</param>
    public PreviousRun Begin(string version)
    {
        var previous = ReadPrevious();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, $"started={_clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)}\nversion={version}\npid={Environment.ProcessId}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next start will not know how this one ended; nothing else is lost.
        }

        return previous;
    }

    /// <summary>Records that this run is ending normally, with the <paramref name="reason"/> (a short text).</summary>
    public void End(string reason)
    {
        try
        {
            File.AppendAllText(file, $"ended={_clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)}\nreason={reason}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next start will report an abnormal end that was not one; only a note in the log.
        }
    }

    private PreviousRun ReadPrevious()
    {
        try
        {
            if (!File.Exists(file))
            {
                return new PreviousRun(false, true, null, null, null);
            }

            DateTimeOffset? started = null;
            string? version = null;
            string? reason = null;
            var ended = false;
            foreach (var line in File.ReadAllLines(file))
            {
                var split = line.IndexOf('=', StringComparison.Ordinal);
                if (split <= 0)
                {
                    continue;
                }

                var key = line[..split];
                var value = line[(split + 1)..];
                switch (key)
                {
                    case "started" when DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at):
                        started = at;
                        break;
                    case "version":
                        version = value;
                        break;
                    case "ended":
                        ended = true;
                        break;
                    case "reason":
                        reason = value;
                        break;
                }
            }

            return new PreviousRun(true, ended, started, version, reason);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PreviousRun(false, true, null, null, null);
        }
    }
}

/// <summary>Counters of this process worth having in the log when the screen stops (Windows window-system quotas, memory, threads).</summary>
public static class ProcessCounters
{
    private const int GdiObjects = 0;
    private const int UserObjects = 1;

    /// <summary>For example <c>GDI 312, USER 140, handles 1204, threads 61, memoria 310 MB, GC 12/3/1</c>; never throws.</summary>
    public static string Describe()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var handle = process.Handle;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"GDI {GetGuiResources(handle, GdiObjects)}, USER {GetGuiResources(handle, UserObjects)}, handles {process.HandleCount}, threads {process.Threads.Count}, memoria {process.PrivateMemorySize64 / (1024 * 1024)} MB, GC {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
        }
        catch (Exception ex)
        {
            return "contadores indisponiveis (" + ex.GetType().Name + ")";
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, int flags);
}
