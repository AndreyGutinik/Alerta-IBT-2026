using AlertaIBT.Core.Backup;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Tests.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Backup;

public sealed class AutoBackupServiceTests : IDisposable
{
    private const string Password = "senha-da-copia-1234";
    private const string Token = "123456789:AAFakeTokenFakeTokenFakeToken_1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-auto-" + Guid.NewGuid().ToString("N"));
    private readonly string _folder;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero));
    private readonly FakeProtector _protector = new();
    private readonly CaptureLogger _log = new();
    private readonly SettingsService _settings;
    private readonly AutoBackupService _auto;

    public AutoBackupServiceTests()
    {
        _folder = Path.Combine(_dir, "copias");
        Directory.CreateDirectory(_folder);
        _settings = new SettingsService(new SettingsStore(Path.Combine(_dir, "settings.json"), _clock), _clock);
        _settings.Update(s => s.WithTelegramToken(Token, _protector));
        _auto = new AutoBackupService(_settings, _protector, _clock, _log, work =>
        {
            work();
            return Task.CompletedTask;
        });
        _auto.Start();
    }

    public void Dispose()
    {
        _auto.Dispose();
        _settings.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string[] Copies() => Directory.GetFiles(_folder, "*.ibtbackup").OrderBy(p => p, StringComparer.Ordinal).ToArray();

    private void Change(int barSpeed) => _settings.Update(s => s with { Bar = s.Bar with { Speed = barSpeed } });

    [Fact]
    public void Turning_it_on_writes_a_first_copy_that_opens_with_the_password()
    {
        _auto.Enable(_folder, Password);
        _clock.Advance(TimeSpan.FromMilliseconds(1));

        var file = Assert.Single(Copies());
        Assert.Matches(@"^ibt-backup-\d{8}-\d{6}\.ibtbackup$", Path.GetFileName(file));
        var payload = SettingsBackup.Read(File.ReadAllBytes(file), Password);
        Assert.Equal(Token, payload.TelegramToken);
        Assert.NotNull(_auto.Status.LastCopyAt);
        Assert.Null(_auto.Status.Error);
        Assert.True(_auto.Status.Enabled);
    }

    [Fact]
    public void A_change_of_an_included_setting_writes_one_copy_only_after_five_quiet_seconds()
    {
        _auto.Enable(_folder, Password);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(Copies());

        Change(100);
        _clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Single(Copies());

        Change(110); // another change inside the window restarts the wait
        _clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Single(Copies());

        _clock.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Equal(2, Copies().Length);
        Assert.Equal(110, SettingsBackup.Read(File.ReadAllBytes(Copies()[^1]), Password).BarSpeed);
    }

    [Fact]
    public void A_change_outside_the_list_writes_nothing()
    {
        _auto.Enable(_folder, Password);
        _clock.Advance(TimeSpan.FromSeconds(1));

        _settings.Update(s => s with { Audio = s.Audio with { MasterVolume = 12 }, Player = s.Player with { Volume = 7 } });
        _clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Single(Copies());
    }

    [Fact]
    public void Only_the_newest_five_of_its_own_pattern_are_kept_and_other_files_are_never_touched()
    {
        File.WriteAllText(Path.Combine(_folder, "notas.txt"), "x");
        File.WriteAllText(Path.Combine(_folder, "ibt-backup-20260101.ibtbackup"), "manual");
        File.WriteAllText(Path.Combine(_folder, "ibt-backup-lixo.ibtbackup"), "x");
        File.WriteAllText(Path.Combine(_folder, "outro-20260101-000000.ibtbackup"), "x");
        File.WriteAllText(Path.Combine(_folder, "ibt-backup-20200101-000000.ibtbackup.bak"), "x");
        File.WriteAllText(Path.Combine(_folder, "ibt-backup-20200101-000000.ibtbackup"), "antiga");

        _auto.Enable(_folder, Password);
        _clock.Advance(TimeSpan.FromSeconds(2));
        for (var i = 0; i < 7; i++)
        {
            Change(100 + i);
            _clock.Advance(TimeSpan.FromSeconds(6));
        }

        var own = Directory.GetFiles(_folder).Where(p => BackupFiles.IsAutoCopyName(Path.GetFileName(p))).ToArray();
        Assert.Equal(5, own.Length);
        Assert.DoesNotContain(own, p => p.EndsWith("20200101-000000.ibtbackup", StringComparison.Ordinal)); // the old own-pattern file rotated out
        Assert.Equal(106, SettingsBackup.Read(File.ReadAllBytes(own.OrderBy(p => p, StringComparer.Ordinal).Last()), Password).BarSpeed);
        foreach (var kept in new[] { "notas.txt", "ibt-backup-20260101.ibtbackup", "ibt-backup-lixo.ibtbackup", "outro-20260101-000000.ibtbackup", "ibt-backup-20200101-000000.ibtbackup.bak" })
        {
            Assert.True(File.Exists(Path.Combine(_folder, kept)), kept);
        }

        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    [Fact]
    public void Two_copies_in_the_same_second_get_different_names()
    {
        _auto.Enable(_folder, Password);
        Change(120);
        _clock.Advance(TimeSpan.FromSeconds(5)); // same fake second as nothing else moved the clock, apart from the 5 s

        _auto.ChangeFolder(_folder);
        _clock.Advance(TimeSpan.Zero);

        Assert.Equal(Copies().Length, Copies().Distinct().Count());
        Assert.True(Copies().Length >= 2);
    }

    [Fact]
    public void The_password_is_kept_only_protected_and_forgotten_when_turned_off()
    {
        _auto.Enable(_folder, Password);

        var stored = _settings.Current.Backup.PasswordProtected;
        Assert.Equal(_protector.Protect(Password), stored);
        Assert.NotEqual(Password, stored);
        _settings.Flush();
        var json = File.ReadAllText(Path.Combine(_dir, "settings.json"));
        Assert.DoesNotContain(Password, json, StringComparison.Ordinal);
        Assert.Contains(stored!, json, StringComparison.Ordinal);

        _auto.Disable();

        Assert.Null(_settings.Current.Backup.PasswordProtected);
        Assert.False(_settings.Current.Backup.AutoEnabled);
        Assert.Equal(_folder, _settings.Current.Backup.Folder);
        var before = Copies().Length;
        Change(130);
        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(before, Copies().Length);
    }

    [Fact]
    public void A_short_password_cannot_turn_it_on()
    {
        var ex = Assert.Throws<BackupException>(() => _auto.Enable(_folder, "curta"));

        Assert.Equal(BackupError.WeakPassword, ex.Error);
        Assert.False(_settings.Current.Backup.AutoEnabled);
        Assert.Null(_settings.Current.Backup.PasswordProtected);
    }

    [Fact]
    public void A_missing_folder_shows_the_error_state_never_throws_and_the_next_change_tries_again()
    {
        var gone = Path.Combine(_dir, "nao-existe");
        _auto.Enable(gone, Password);
        _clock.Advance(TimeSpan.FromSeconds(1));

        Assert.NotNull(_auto.Status.Error);
        Assert.Contains("pasta", _auto.Status.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(_auto.Status.LastCopyAt);

        Directory.CreateDirectory(gone);
        Change(140);
        _clock.Advance(TimeSpan.FromSeconds(6));

        Assert.Null(_auto.Status.Error);
        Assert.NotNull(_auto.Status.LastCopyAt);
        Assert.Single(Directory.GetFiles(gone, "*.ibtbackup"));
    }

    [Fact]
    public void A_folder_that_cannot_be_written_reports_the_error()
    {
        // A file where the folder should be: the folder "exists" for nobody, so the write cannot happen.
        var notAFolder = Path.Combine(_dir, "arquivo.txt");
        File.WriteAllText(notAFolder, "x");

        _auto.Enable(notAFolder, Password);
        _clock.Advance(TimeSpan.FromSeconds(1));

        Assert.NotNull(_auto.Status.Error);
    }

    [Fact]
    public void An_unreadable_stored_password_is_an_error_not_a_crash()
    {
        _auto.Enable(_folder, Password);
        _clock.Advance(TimeSpan.FromSeconds(1));
        _settings.Update(s => s with { Backup = s.Backup with { PasswordProtected = "isto-nao-e-base64-valido!!" } });

        Change(150);
        _clock.Advance(TimeSpan.FromSeconds(6));

        Assert.NotNull(_auto.Status.Error);
        Assert.Contains("Senha", _auto.Status.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Neither_the_password_nor_a_secret_ever_reaches_the_log_or_the_status()
    {
        var gone = Path.Combine(_dir, "nao-existe");
        _auto.Enable(gone, Password);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Directory.CreateDirectory(gone);
        Change(160);
        _clock.Advance(TimeSpan.FromSeconds(6));
        Directory.Delete(gone, recursive: true);
        Change(170);
        _clock.Advance(TimeSpan.FromSeconds(6));

        var everything = string.Join("\n", _log.Lines) + _auto.Status;
        Assert.DoesNotContain(Password, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(_protector.Protect(Password)!, everything, StringComparison.Ordinal);
    }

    [Fact]
    public void The_status_is_announced_when_it_changes()
    {
        var seen = new List<AutoBackupStatus>();
        _auto.StatusChanged += seen.Add;

        _auto.Enable(_folder, Password);
        _clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Contains(seen, s => s.Enabled);
        Assert.Contains(seen, s => s.LastCopyAt is not null);
    }

    [Fact]
    public void Starting_with_copies_already_in_the_folder_shows_when_the_newest_was_written()
    {
        _settings.Update(s => s with { Backup = new BackupSettings { AutoEnabled = true, Folder = _folder, PasswordProtected = _protector.Protect(Password) } });
        var path = Path.Combine(_folder, "ibt-backup-20261001-080000.ibtbackup");
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc));
        using var fresh = new AutoBackupService(_settings, _protector, _clock, _log, work =>
        {
            work();
            return Task.CompletedTask;
        });

        fresh.Start();

        Assert.Equal(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero), fresh.Status.LastCopyAt);
    }

    [Fact]
    public void The_write_runs_through_the_runner_so_the_caller_is_never_blocked()
    {
        var queued = new List<Action>();
        using var service = new AutoBackupService(_settings, _protector, _clock, _log, work =>
        {
            queued.Add(work);
            return Task.CompletedTask;
        });
        service.Start();
        queued.Clear();

        service.Enable(_folder, Password);
        _clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(Directory.GetFiles(_folder, "*.ibtbackup")); // nothing written on the calling thread
        Assert.Single(queued);
        queued[0]();
        Assert.Single(Directory.GetFiles(_folder, "*.ibtbackup"));
    }

    private sealed class CaptureLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + (exception is null ? string.Empty : "\n" + exception));
    }
}
