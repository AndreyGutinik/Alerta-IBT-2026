using System.Text;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 9, 30, 15, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    private SettingsStore Store() => new(SettingsPath, _clock);

    private string[] BadFiles() => Directory.Exists(_dir)
        ? Directory.GetFiles(_dir, "settings.bad-*.json").Order().ToArray()
        : [];

    [Fact]
    public void The_default_path_is_settings_json_under_AppData_AlertaIBT()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlertaIBT", "settings.json");

        Assert.Equal(expected, SettingsStore.DefaultPath);
    }

    [Fact]
    public void No_file_is_the_defaults_and_not_corrupt()
    {
        var result = Store().Load();

        Assert.Equal(new AppSettings(), result.Settings);
        Assert.False(result.WasCorrupt);
        Assert.Null(result.BackupPath);
    }

    [Fact]
    public void Save_then_load_round_trips_and_creates_the_folder()
    {
        var settings = new AppSettings { Bar = new BarSettings { Height = 80, Position = "bottom" } };

        var store = Store();
        store.Save(settings);
        var result = store.Load();

        Assert.True(File.Exists(SettingsPath));
        Assert.False(result.WasCorrupt);
        Assert.Equal(settings, result.Settings);
    }

    [Fact]
    public void Save_leaves_no_temporary_file_behind_and_replaces_the_old_content()
    {
        var store = Store();
        store.Save(new AppSettings { Bar = new BarSettings { Height = 70 } });
        store.Save(new AppSettings { Bar = new BarSettings { Height = 90 } });

        Assert.Equal(["settings.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
        Assert.Equal(90, store.Load().Settings.Bar.Height);
    }

    [Fact]
    public void A_stale_temporary_file_from_a_crash_does_not_get_in_the_way()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath + ".tmp", "half writ");

        var store = Store();
        store.Save(new AppSettings { Bar = new BarSettings { Height = 66 } });

        Assert.Equal(66, store.Load().Settings.Bar.Height);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void The_file_is_utf8_without_a_byte_order_mark()
    {
        var store = Store();
        store.Save(new AppSettings { Correction = new CorrectionSettings { FixedRules = "recep\U000000E7\U000000E3o" } });

        var bytes = File.ReadAllBytes(SettingsPath);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Contains("recep\U000000E7\U000000E3o", new UTF8Encoding(false).GetString(bytes));
    }

    [Fact]
    public void A_corrupt_file_gives_the_defaults_and_is_kept_as_a_bad_copy()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ \"bar\": { \"height\": 7");

        var result = Store().Load();

        Assert.True(result.WasCorrupt);
        Assert.Equal(new AppSettings(), result.Settings);
        var bad = Assert.Single(BadFiles());
        Assert.Equal(bad, result.BackupPath);
        Assert.Equal("settings.bad-20261020-093015-000.json", Path.GetFileName(bad));
        Assert.Equal("{ \"bar\": { \"height\": 7", File.ReadAllText(bad));
    }

    [Fact]
    public void After_a_corrupt_load_the_next_load_is_clean_and_makes_no_second_copy()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "garbage");
        var store = Store();

        store.Load();
        var second = store.Load();

        Assert.False(second.WasCorrupt);
        Assert.Single(BadFiles());
    }

    [Fact]
    public void A_file_of_zero_bytes_or_nul_bytes_is_corrupt()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(SettingsPath, new byte[128]);

        var result = Store().Load();

        Assert.True(result.WasCorrupt);
        Assert.Single(BadFiles());
    }

    [Fact]
    public void Two_corrupt_files_in_the_same_second_keep_both_copies()
    {
        Directory.CreateDirectory(_dir);
        var store = Store();

        File.WriteAllText(SettingsPath, "first");
        store.Load();
        File.WriteAllText(SettingsPath, "second");
        store.Load();

        var bad = BadFiles();
        Assert.Equal(2, bad.Length);
        Assert.Equal(["first", "second"], bad.Select(File.ReadAllText).Order());
    }

    [Fact]
    public void Only_the_five_newest_bad_copies_are_kept()
    {
        Directory.CreateDirectory(_dir);
        var store = Store();

        for (var i = 0; i < 8; i++)
        {
            File.WriteAllText(SettingsPath, "bad " + i);
            store.Load();
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        var bad = BadFiles();
        Assert.Equal(5, bad.Length);
        Assert.Equal(["bad 3", "bad 4", "bad 5", "bad 6", "bad 7"], bad.Select(File.ReadAllText));
    }

    [Fact]
    public void A_good_file_is_never_touched_by_load()
    {
        var store = Store();
        store.Save(new AppSettings { Bar = new BarSettings { Height = 77 } });
        var before = File.ReadAllBytes(SettingsPath);

        store.Load();

        Assert.Equal(before, File.ReadAllBytes(SettingsPath));
        Assert.Empty(BadFiles());
    }

    [Fact]
    public void A_file_that_cannot_be_read_gives_the_defaults_instead_of_crashing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{}");
        using var locked = new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = Store().Load();

        Assert.Equal(new AppSettings(), result.Settings);
        Assert.True(result.WasCorrupt);
    }

    [Fact]
    public async Task Concurrent_saves_never_leave_a_broken_file()
    {
        var store = Store();
        store.Save(new AppSettings());

        var writers = Enumerable.Range(0, 20)
            .Select(i => Task.Run(
                () =>
                {
                    for (var n = 0; n < 10; n++)
                    {
                        store.Save(new AppSettings { Bar = new BarSettings { Height = 28 + i } });
                    }
                },
                TestContext.Current.CancellationToken));
        var reader = Task.Run(
            () =>
            {
                for (var n = 0; n < 50; n++)
                {
                    Assert.False(store.Load().WasCorrupt);
                }
            },
            TestContext.Current.CancellationToken);

        await Task.WhenAll(writers.Append(reader));

        Assert.False(store.Load().WasCorrupt);
        Assert.Empty(BadFiles());
    }

    [Fact]
    public void Save_failing_throws_an_io_error_and_leaves_the_old_file_intact()
    {
        var store = Store();
        store.Save(new AppSettings { Bar = new BarSettings { Height = 61 } });
        // A directory squatting on the temp name makes the write fail.
        Directory.CreateDirectory(SettingsPath + ".tmp");

        var failure = Record.Exception(() => store.Save(new AppSettings { Bar = new BarSettings { Height = 99 } }));
        Assert.True(failure is IOException or UnauthorizedAccessException, $"unexpected: {failure}");
        Directory.Delete(SettingsPath + ".tmp");

        Assert.Equal(61, store.Load().Settings.Bar.Height);
    }
}
