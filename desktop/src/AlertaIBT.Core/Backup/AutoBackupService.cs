using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AlertaIBT.Core.Diagnostics;

namespace AlertaIBT.Core.Backup;

/// <summary>Where the automatic copy stands, for the status line of the card.</summary>
/// <param name="Enabled">Whether the automatic copy is on.</param>
/// <param name="Folder">The folder of the copies (blank when none).</param>
/// <param name="LastCopyAt">When the last copy was written, or <c>null</c> when none is known.</param>
/// <param name="Error">Why the last attempt failed (pt-BR), or <c>null</c> when it did not.</param>
public sealed record AutoBackupStatus(bool Enabled, string Folder, DateTimeOffset? LastCopyAt, string? Error);

/// <summary>
/// The automatic copy: when one of the included settings changes, 5 seconds after the last change a new
/// <c>ibt-backup-yyyyMMdd-HHmmss.ibtbackup</c> is written in the chosen folder and only the newest 5 are kept (only files of that exact pattern are
/// ever deleted). It needs the password, so it is asked once when the copy is turned on and kept protected with DPAPI in the settings; turning the
/// copy off forgets it. The write runs on a background task (never the UI thread) as a temporary file moved into place. A folder that is missing or
/// cannot be written puts the status in error, never throws, and is tried again on the next change. Nothing here logs a password or a secret.
/// </summary>
public sealed class AutoBackupService : IDisposable
{
    /// <summary>How long the included settings must stay unchanged before a copy is written.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(5);

    private readonly SettingsService _settings;
    private readonly ISecretProtector _protector;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly Func<Action, Task> _runner;
    private readonly ITimer _timer;
    private readonly object _gate = new();
    private readonly object _writeGate = new();
    private BackupPayload? _lastSeen;
    private BackupPayload? _lastWritten;
    private AutoBackupStatus _status;
    private bool _disposed;

    /// <param name="settings">The settings: it is watched for changes and holds the folder and the protected password.</param>
    /// <param name="protector">Protects the password (DPAPI in the app) and reads the secrets of the settings.</param>
    /// <param name="clock">Times the 5 seconds and the names; the system clock by default.</param>
    /// <param name="logger">Receives failures (never a password or a secret).</param>
    /// <param name="runner">Runs the work off the calling thread; <c>Task.Run</c> by default.</param>
    public AutoBackupService(SettingsService settings, ISecretProtector protector, TimeProvider? clock = null, ILogger? logger = null, Func<Action, Task>? runner = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(protector);

        _settings = settings;
        _protector = protector;
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _runner = runner ?? (work => Task.Run(work));
        _timer = _clock.CreateTimer(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var backup = settings.Current.Backup;
        _status = new AutoBackupStatus(backup.AutoEnabled, backup.Folder, null, null);
    }

    /// <summary>Raised from any thread when <see cref="Status"/> changes.</summary>
    public event Action<AutoBackupStatus>? StatusChanged;

    /// <summary>The last write, for tests to wait on.</summary>
    internal Task LastWork { get; private set; } = Task.CompletedTask;

    /// <summary>Where the automatic copy stands.</summary>
    public AutoBackupStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>Starts watching the settings, and looks (in the background) when the newest copy in the folder was written.</summary>
    public void Start()
    {
        _lastSeen = Fingerprint(_settings.Current);
        _settings.Changed += OnChanged;
        LastWork = _runner(ProbeLastCopy);
    }

    /// <summary>Turns the copy on: keeps <paramref name="folder"/> and the protected <paramref name="password"/>, and writes the first copy at once.</summary>
    /// <exception cref="BackupException"><see cref="BackupError.WeakPassword"/> when the password is shorter than 8 characters.</exception>
    public void Enable(string folder, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!BackupCrypto.IsAcceptablePassword(password))
        {
            throw new BackupException(BackupError.WeakPassword);
        }

        var protectedPassword = _protector.Protect(password);
        _settings.Update(s => s with { Backup = new BackupSettings { AutoEnabled = true, Folder = folder.Trim(), PasswordProtected = protectedPassword } });
        SetStatus(status => status with { Enabled = true, Folder = folder.Trim(), Error = null });
        lock (_gate)
        {
            _lastWritten = null;
            _lastSeen = Fingerprint(_settings.Current);
        }

        _timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Turns the copy off and forgets the password. The files already written stay where they are.</summary>
    public void Disable()
    {
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _settings.Update(s => s with { Backup = s.Backup with { AutoEnabled = false, PasswordProtected = null } });
        SetStatus(status => status with { Enabled = false, Error = null });
    }

    /// <summary>Changes the folder of the copies and writes a copy there at once.</summary>
    public void ChangeFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        _settings.Update(s => s with { Backup = s.Backup with { Folder = folder.Trim() } });
        SetStatus(status => status with { Folder = folder.Trim(), Error = null, LastCopyAt = null });
        if (_settings.Current.Backup.AutoEnabled)
        {
            lock (_gate)
            {
                _lastWritten = null;
            }

            _timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _settings.Changed -= OnChanged;
        _timer.Dispose();
    }

    // The included values with the secrets as they are stored (no unprotecting): equal when nothing included changed.
    private static BackupPayload Fingerprint(AppSettings settings) => SettingsBackup.Capture(settings, RawProtector.Instance, DateTimeOffset.UnixEpoch);

    private void OnChanged(AppSettings settings)
    {
        if (!settings.Backup.AutoEnabled)
        {
            return;
        }

        var fingerprint = Fingerprint(settings);
        lock (_gate)
        {
            if (_disposed || fingerprint == _lastSeen)
            {
                return;
            }

            _lastSeen = fingerprint;
        }

        _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        LastWork = _runner(WriteNow);
    }

    private void WriteNow()
    {
        Breadcrumbs.Add("backup: copia automatica");
        lock (_writeGate)
        {
            var snapshot = _settings.Current;
            var backup = snapshot.Backup;
            if (!backup.AutoEnabled)
            {
                return;
            }

            try
            {
                var fingerprint = Fingerprint(snapshot);
                lock (_gate)
                {
                    if (fingerprint == _lastWritten && _status.Error is null)
                    {
                        return; // the same content was already written
                    }
                }

                var password = _protector.Unprotect(backup.PasswordProtected);
                if (password is null)
                {
                    SetStatus(status => status with { Error = "Senha da c\U000000F3pia indispon\U000000EDvel. Desligue e ligue a c\U000000F3pia autom\U000000E1tica de novo." });
                    return;
                }

                if (backup.Folder.Length == 0 || !Directory.Exists(backup.Folder))
                {
                    SetStatus(status => status with { Error = "A pasta da c\U000000F3pia n\U000000E3o existe." });
                    return;
                }

                var now = _clock.GetUtcNow();
                var bytes = SettingsBackup.Export(snapshot, _protector, password, now);
                var path = FreePath(backup.Folder, _clock.GetLocalNow().DateTime);
                BackupFiles.WriteAtomic(path, bytes);
                BackupFiles.Rotate(backup.Folder);
                lock (_gate)
                {
                    _lastWritten = fingerprint;
                }

                SetStatus(status => status with { LastCopyAt = now, Error = null });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BackupException)
            {
                // The message of these exceptions names a path or a fixed text, never a password.
                _logger.LogWarning("C\U000000F3pia autom\U000000E1tica do backup falhou: {Reason}", ex.GetType().Name);
                SetStatus(status => status with { Error = ex is BackupException ? ex.Message : "N\U000000E3o foi poss\U000000EDvel gravar na pasta da c\U000000F3pia." });
            }
            catch (Exception ex)
            {
                _logger.LogError("C\U000000F3pia autom\U000000E1tica do backup falhou de forma inesperada: {Reason}", ex.GetType().Name);
                SetStatus(status => status with { Error = "A c\U000000F3pia autom\U000000E1tica falhou. Veja o registro (log)." });
            }
        }
    }

    // The name for "now", moved on by a second while a file of that name exists (two copies in the same second).
    private static string FreePath(string folder, DateTime local)
    {
        for (var i = 0; i < 120; i++)
        {
            var path = Path.Combine(folder, BackupFiles.AutoFileName(local.AddSeconds(i)));
            if (!File.Exists(path))
            {
                return path;
            }
        }

        return Path.Combine(folder, BackupFiles.AutoFileName(local.AddSeconds(120)));
    }

    private void ProbeLastCopy()
    {
        try
        {
            var folder = _settings.Current.Backup.Folder;
            if (folder.Length == 0 || !Directory.Exists(folder))
            {
                return;
            }

            var newest = BackupFiles.ListAutoCopies(folder).FirstOrDefault();
            if (newest is not null)
            {
                var at = new DateTimeOffset(File.GetLastWriteTimeUtc(newest), TimeSpan.Zero);
                SetStatus(status => status.LastCopyAt is null ? status with { LastCopyAt = at } : status);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing known about the last copy: the status line stays without a time.
        }
    }

    private void SetStatus(Func<AutoBackupStatus, AutoBackupStatus> change)
    {
        AutoBackupStatus next;
        lock (_gate)
        {
            next = change(_status);
            if (next == _status)
            {
                return;
            }

            _status = next;
        }

        try
        {
            StatusChanged?.Invoke(next);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao avisar o estado da c\U000000F3pia autom\U000000E1tica");
        }
    }

    // Hands the stored (protected) text through untouched, so equal fingerprints mean equal stored secrets.
    private sealed class RawProtector : ISecretProtector
    {
        public static readonly RawProtector Instance = new();

        public string? Protect(string? plain) => plain;

        public string? Unprotect(string? protectedValue) => protectedValue;
    }
}
