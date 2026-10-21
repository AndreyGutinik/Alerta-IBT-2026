using System.Text;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Settings;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-svc-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 9, 30, 15, TimeSpan.Zero));
    private readonly List<Exception> _errors = [];

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    private SettingsService Service(string? path = null) =>
        new(new SettingsStore(path ?? SettingsPath, _clock), _clock, _errors.Add);

    private AppSettings OnDisk() => new SettingsStore(SettingsPath, _clock).Load().Settings;

    [Fact]
    public void The_save_delay_is_600_ms()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(600), SettingsService.SaveDelay);
    }

    [Fact]
    public void No_file_starts_with_the_defaults_and_writes_nothing_until_a_change()
    {
        using var service = Service();

        Assert.Equal(new AppSettings(), service.Current);
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void A_change_is_visible_and_announced_at_once_but_saved_only_after_the_delay()
    {
        using var service = Service();
        AppSettings? announced = null;
        service.Changed += s => announced = s;

        service.Update(s => s with { Bar = s.Bar with { Height = 80 } });

        Assert.Equal(80, service.Current.Bar.Height);
        Assert.Equal(80, announced?.Bar.Height);
        Assert.False(File.Exists(SettingsPath));

        _clock.Advance(TimeSpan.FromMilliseconds(599));
        Assert.False(File.Exists(SettingsPath));

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(80, OnDisk().Bar.Height);
        Assert.Empty(_errors);
    }

    [Fact]
    public void Changes_in_a_burst_are_written_once_with_the_last_value()
    {
        using var service = Service();
        for (var speed = 100; speed <= 140; speed += 10)
        {
            service.Update(s => s with { Bar = s.Bar with { Speed = speed } });
            _clock.Advance(TimeSpan.FromMilliseconds(300));
            Assert.False(File.Exists(SettingsPath));
        }

        _clock.Advance(TimeSpan.FromMilliseconds(600));

        Assert.Equal(140, OnDisk().Bar.Speed);
        Assert.Equal(140, service.Current.Bar.Speed);
    }

    [Fact]
    public void A_value_out_of_range_is_held_in_range()
    {
        using var service = Service();

        service.Update(s => s with { Bar = s.Bar with { Height = 500, Speed = 5 } });

        Assert.Equal(BarSettings.MaxHeight, service.Current.Bar.Height);
        Assert.Equal(BarSettings.MinSpeed, service.Current.Bar.Speed);
    }

    [Fact]
    public void SaveIfMissing_writes_the_defaults_on_a_first_run_and_leaves_an_existing_file_alone()
    {
        using (var first = Service())
        {
            first.SaveIfMissing();
            Assert.True(File.Exists(SettingsPath));
            Assert.Equal(new AppSettings(), OnDisk());
        }

        using (var changed = Service())
        {
            changed.Update(s => s with { Bar = s.Bar with { Height = 70 } });
        }

        var before = File.ReadAllText(SettingsPath);
        using var again = Service();
        again.SaveIfMissing();

        Assert.Equal(before, File.ReadAllText(SettingsPath));
        Assert.Equal(70, again.Current.Bar.Height);
    }

    [Fact]
    public void Flush_saves_at_once()
    {
        using var service = Service();
        service.Update(s => s with { System = s.System with { Hotkey = "Ctrl+Alt+F9" } });

        service.Flush();

        Assert.Equal("Ctrl+Alt+F9", OnDisk().System.Hotkey);
    }

    [Fact]
    public void Dispose_saves_a_change_still_waiting_for_the_delay()
    {
        var service = Service();
        service.Update(s => s with { Bar = s.Bar with { Position = "bottom" } });

        service.Dispose();

        Assert.Equal("bottom", OnDisk().Bar.Position);
    }

    [Fact]
    public void An_update_after_dispose_changes_the_memory_but_is_not_scheduled()
    {
        var service = Service();
        service.Dispose();

        service.Update(s => s with { Bar = s.Bar with { Height = 70 } });
        _clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(70, service.Current.Bar.Height);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void A_second_service_reads_what_the_first_saved()
    {
        using (var first = Service())
        {
            first.Update(s => s with { Telegram = s.Telegram with { ChatId = -100123 } });
        }

        using var second = Service();

        Assert.Equal(-100123, second.Current.Telegram.ChatId);
    }

    [Fact]
    public void A_corrupt_file_gives_the_defaults_keeps_a_bad_copy_and_the_next_save_writes_a_good_file()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ this is not json", new UTF8Encoding(false));

        using var service = Service();

        Assert.True(service.LoadResult.WasCorrupt);
        Assert.NotNull(service.LoadResult.BackupPath);
        Assert.True(File.Exists(service.LoadResult.BackupPath));
        Assert.False(service.FileUnreadable);
        Assert.Equal(new AppSettings(), service.Current);

        service.Update(s => s with { Bar = s.Bar with { Height = 90 } });
        service.Flush();

        Assert.Equal(90, OnDisk().Bar.Height);
    }

    [Fact]
    public void A_throwing_handler_does_not_break_the_update_nor_the_other_handlers()
    {
        using var service = Service();
        var second = 0;
        service.Changed += _ => throw new InvalidOperationException("boom");
        service.Changed += _ => second++;

        service.Update(s => s with { Bar = s.Bar with { Height = 60 } });

        Assert.Equal(1, second);
        Assert.Equal(60, service.Current.Bar.Height);
        Assert.Single(_errors);
        Assert.IsType<InvalidOperationException>(_errors[0]);
    }

    [Fact]
    public void A_failing_save_is_reported_not_thrown_and_is_retried_by_the_next_flush()
    {
        // The settings "folder" is a file, so the folder cannot be created.
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x");
        using var service = Service(Path.Combine(blocker, "settings.json"));

        service.Update(s => s with { Bar = s.Bar with { Height = 64 } });
        _clock.Advance(SettingsService.SaveDelay);

        Assert.Single(_errors);
        Assert.Equal(64, service.Current.Bar.Height);

        File.Delete(blocker);
        service.Flush();

        Assert.Single(_errors);
        Assert.Equal(64, new SettingsStore(Path.Combine(blocker, "settings.json"), _clock).Load().Settings.Bar.Height);
    }

    [Fact]
    public void HasUnsavedChanges_is_true_from_the_change_until_the_file_is_written()
    {
        using var service = Service();
        Assert.False(service.HasUnsavedChanges);

        service.Update(s => s with { Bar = s.Bar with { Height = 70 } });
        Assert.True(service.HasUnsavedChanges);

        service.Flush();

        Assert.False(service.HasUnsavedChanges);
        Assert.Equal(70, OnDisk().Bar.Height);
    }

    [Fact]
    public void HasUnsavedChanges_stays_true_when_the_save_failed()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x");
        using var service = Service(Path.Combine(blocker, "settings.json"));

        service.Update(s => s with { Bar = s.Bar with { Height = 64 } });
        service.Flush();

        Assert.True(service.HasUnsavedChanges);
        Assert.NotEmpty(_errors);
    }

    [Fact]
    public void An_unreadable_file_is_not_overwritten_until_it_can_be_read()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{\"bar\":{\"height\":100}}");

        // Hold the file with an exclusive lock so reading fails: loading reports it corrupt without being able to move it aside.
        var holder = new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        SettingsService service;
        try
        {
            service = Service();
            Assert.True(service.FileUnreadable);

            service.Update(s => s with { Bar = s.Bar with { Height = 40 } });
            service.Flush();

            Assert.NotEmpty(_errors);
            Assert.Equal(40, service.Current.Bar.Height);
        }
        finally
        {
            holder.Dispose();
        }

        using (service)
        {
            var errorsBefore = _errors.Count;
            service.Flush();

            Assert.False(service.FileUnreadable);
            Assert.Equal(errorsBefore, _errors.Count);
            Assert.Equal(100, service.Current.Bar.Height);
            Assert.Equal(100, OnDisk().Bar.Height);
        }
    }
}
