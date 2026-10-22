using AlertaIBT.Core.Settings;

namespace AlertaIBT.Core.Tests.Settings;

// The three fields of "Limpar historico": all optional, so a settings.json from before them loads as it did.
public sealed class SettingsHistoryClearTests
{
    [Fact]
    public void The_defaults_clear_every_monday_and_have_no_last_clear_and_no_pending_site()
    {
        var system = new AppSettings().System;

        Assert.True(system.ClearHistoryWeekly);
        Assert.Null(system.LastHistoryClearAt);
        Assert.False(system.SitePendingClear);
    }

    [Fact]
    public void An_old_settings_file_without_the_fields_loads_with_the_defaults_and_is_not_corrupt()
    {
        var (settings, corrupt) = SettingsJson.Load("""{"version":1,"system":{"hotkey":"Ctrl+Alt+F1","lastLedDevicePath":"X"}}""");

        Assert.False(corrupt);
        Assert.Equal("Ctrl+Alt+F1", settings.System.Hotkey);
        Assert.True(settings.System.ClearHistoryWeekly);
        Assert.Null(settings.System.LastHistoryClearAt);
        Assert.False(settings.System.SitePendingClear);
    }

    [Fact]
    public void The_fields_survive_a_save_and_a_load()
    {
        var moment = new DateTimeOffset(2026, 10, 19, 3, 0, 5, 123, TimeSpan.Zero);
        var original = new AppSettings
        {
            System = new SystemSettings { ClearHistoryWeekly = false, LastHistoryClearAt = moment, SitePendingClear = true },
        };

        var (loaded, corrupt) = SettingsJson.Load(SettingsJson.Save(original));

        Assert.False(corrupt);
        Assert.False(loaded.System.ClearHistoryWeekly);
        Assert.Equal(moment, loaded.System.LastHistoryClearAt);
        Assert.True(loaded.System.SitePendingClear);
    }

    [Fact]
    public void A_moment_with_an_offset_is_kept_as_the_same_instant()
    {
        var (settings, _) = SettingsJson.Load("""{"system":{"lastHistoryClearAt":"2026-10-19T00:00:00-03:00"}}""");

        Assert.Equal(new DateTimeOffset(2026, 10, 19, 3, 0, 0, TimeSpan.Zero), settings.System.LastHistoryClearAt);
    }

    [Theory]
    [InlineData("\"lastHistoryClearAt\":\"not a date\"")]
    [InlineData("\"lastHistoryClearAt\":12345")]
    [InlineData("\"lastHistoryClearAt\":null")]
    [InlineData("\"lastHistoryClearAt\":\"\"")]
    public void A_damaged_moment_is_no_moment_so_the_count_starts_again(string field)
    {
        var (settings, corrupt) = SettingsJson.Load("{\"system\":{" + field + "}}");

        Assert.False(corrupt);
        Assert.Null(settings.System.LastHistoryClearAt);
    }

    [Fact]
    public void Normalizing_keeps_the_fields_and_is_idempotent()
    {
        var moment = new DateTimeOffset(2026, 10, 19, 3, 0, 0, TimeSpan.FromHours(-3));
        var once = SettingsJson.Normalize(new AppSettings { System = new SystemSettings { LastHistoryClearAt = moment, SitePendingClear = true } });

        Assert.Equal(moment, once.System.LastHistoryClearAt);
        Assert.True(once.System.SitePendingClear);
        Assert.Equal(once, SettingsJson.Normalize(once));
    }
}
