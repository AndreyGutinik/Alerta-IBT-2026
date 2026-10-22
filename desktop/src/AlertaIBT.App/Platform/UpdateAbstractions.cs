using System.Globalization;
using System.IO;

namespace AlertaIBT.App.Platform;

/// <summary>Where the automatic update stands.</summary>
public enum UpdatePhase
{
    /// <summary>Nothing under way (the last result says how the last look ended).</summary>
    Idle,

    /// <summary>The "Verificar agora" button is asking the feed.</summary>
    Checking,

    /// <summary>A newer version is being downloaded.</summary>
    Downloading,

    /// <summary>A newer version is downloaded and waits for the app to be idle.</summary>
    WaitingForIdle,

    /// <summary>The updater was started and the app is leaving to be replaced and started again.</summary>
    Installing,

    /// <summary>The same version failed to install twice in 24 hours: no more tries until the next day.</summary>
    GaveUp,
}

/// <summary>What the windows and the tray show about the update.</summary>
/// <param name="Phase">Where it stands.</param>
/// <param name="Notice">The one line shown in the main window and the tray tooltip while updating, or <c>null</c>.</param>
/// <param name="Result">The line of Configurações: how the last look ended (<c>Atualizado (8.0.1) às 14:32</c>).</param>
public sealed record UpdateState(UpdatePhase Phase, string? Notice, string Result);

/// <summary>What the windows see of the update service: the running version, the state and the "Verificar agora" button.</summary>
public interface IUpdateStatus
{
    /// <summary>The running version.</summary>
    string CurrentVersion { get; }

    /// <summary>Where the update stands now.</summary>
    UpdateState State { get; }

    /// <summary>Raised when <see cref="State"/> changes, on any thread.</summary>
    event Action<UpdateState>? StateChanged;

    /// <summary>"Verificar agora": looks at the feed even with the automatic update off, downloads and installs when idle. Never throws.</summary>
    Task CheckNowAsync(CancellationToken cancellationToken);
}

/// <summary>What the update service needs from the running app: whether it is idle, and how to leave cleanly.</summary>
public interface IUpdateHost
{
    /// <summary>
    /// Whether the app may be restarted now: nothing queued, nothing on the bar, no sermon pause (it is not saved, a restart would
    /// end it) and no send under way in a panel.
    /// </summary>
    bool IsIdle { get; }

    /// <summary>Starts telling <paramref name="maybeIdle"/> (on any thread) whenever something that could make the app idle changed.</summary>
    void Attach(Action maybeIdle);

    /// <summary>Saves what the restart must not lose: the queue and the settings.</summary>
    void PrepareForRestart();

    /// <summary>Leaves through the app's normal shutdown (the connectors stop, the files are saved); the updater is waiting for that.</summary>
    void Exit();
}

/// <summary>
/// A small file written just before the updater is started. The relaunched app finds it and waits a few seconds for the
/// single-instance lock instead of taking a held lock as "another copy is running" and exiting.
/// </summary>
public static class UpdateRestartMarker
{
    /// <summary>How long the new process waits for the lock after an update restart.</summary>
    public static readonly TimeSpan WaitForLock = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(2);

    /// <summary>Writes the marker. Never throws.</summary>
    public static void Write(string file, DateTimeOffset now)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            File.WriteAllText(file, now.UtcTicks.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the marker the new start only waits for the lock if the old process is already gone: harmless.
        }
    }

    /// <summary>
    /// Reads and deletes the marker: <see cref="WaitForLock"/> when it is fresh (the app was just restarted by an update),
    /// otherwise zero. Never throws.
    /// </summary>
    public static TimeSpan ConsumeWait(string file, DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(file))
            {
                return TimeSpan.Zero;
            }

            var text = File.ReadAllText(file).Trim();
            File.Delete(file);
            return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && ticks > 0
                && ticks <= DateTimeOffset.MaxValue.UtcTicks
                && now - new DateTimeOffset(ticks, TimeSpan.Zero) is { } age
                && age >= TimeSpan.Zero
                && age < Fresh
                    ? WaitForLock
                    : TimeSpan.Zero;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TimeSpan.Zero;
        }
    }
}
