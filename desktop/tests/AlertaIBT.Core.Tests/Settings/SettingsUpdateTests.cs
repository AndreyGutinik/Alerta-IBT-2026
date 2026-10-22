using AlertaIBT.Core.Settings;

namespace AlertaIBT.Core.Tests.Settings;

// The automatic-update fields of Configuracoes > Sistema: all optional, so a settings.json from before them loads as it did,
// and the rule that keeps a failing update from restarting the app in a loop.
public sealed class SettingsUpdateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_defaults_update_by_themselves_and_remember_no_attempt()
    {
        var system = new AppSettings().System;

        Assert.True(system.AutoUpdate);
        Assert.Null(system.UpdateAttemptVersion);
        Assert.Null(system.UpdateAttemptSince);
        Assert.Equal(0, system.UpdateAttempts);
    }

    [Fact]
    public void An_old_settings_file_without_the_fields_loads_with_the_defaults_and_is_not_corrupt()
    {
        var (settings, corrupt) = SettingsJson.Load("""{"version":1,"system":{"hotkey":"Ctrl+Alt+F1","clearHistoryWeekly":false}}""");

        Assert.False(corrupt);
        Assert.Equal("Ctrl+Alt+F1", settings.System.Hotkey);
        Assert.False(settings.System.ClearHistoryWeekly);
        Assert.True(settings.System.AutoUpdate);
        Assert.Null(settings.System.UpdateAttemptVersion);
        Assert.Null(settings.System.UpdateAttemptSince);
        Assert.Equal(0, settings.System.UpdateAttempts);
    }

    [Fact]
    public void The_fields_survive_a_save_and_a_load()
    {
        var original = new AppSettings
        {
            System = new SystemSettings { AutoUpdate = false, UpdateAttemptVersion = "8.0.1-pre.2", UpdateAttemptSince = T0, UpdateAttempts = 2 },
        };

        var (loaded, corrupt) = SettingsJson.Load(SettingsJson.Save(original));

        Assert.False(corrupt);
        Assert.False(loaded.System.AutoUpdate);
        Assert.Equal("8.0.1-pre.2", loaded.System.UpdateAttemptVersion);
        Assert.Equal(T0, loaded.System.UpdateAttemptSince);
        Assert.Equal(2, loaded.System.UpdateAttempts);
    }

    [Theory]
    [InlineData("\"updateAttemptSince\":\"not a date\"")]
    [InlineData("\"updateAttemptSince\":7")]
    [InlineData("\"updateAttempts\":\"many\"")]
    [InlineData("\"updateAttempts\":-5")]
    [InlineData("\"autoUpdate\":\"yes\"")]
    public void Damaged_values_fall_back_to_the_defaults_without_corruption(string field)
    {
        var (settings, corrupt) = SettingsJson.Load("{\"system\":{" + field + "}}");

        Assert.False(corrupt);
        Assert.True(settings.System.AutoUpdate);
        Assert.Null(settings.System.UpdateAttemptSince);
        Assert.Equal(0, settings.System.UpdateAttempts);
    }

    [Fact]
    public void Normalizing_keeps_the_fields_and_is_idempotent()
    {
        var once = SettingsJson.Normalize(new AppSettings
        {
            System = new SystemSettings { AutoUpdate = false, UpdateAttemptVersion = "  8.0.2 ", UpdateAttemptSince = T0.ToOffset(TimeSpan.FromHours(-3)), UpdateAttempts = -1 },
        });

        Assert.False(once.System.AutoUpdate);
        Assert.Equal("8.0.2", once.System.UpdateAttemptVersion);
        Assert.Equal(T0, once.System.UpdateAttemptSince);
        Assert.Equal(0, once.System.UpdateAttempts);
        Assert.Equal(once, SettingsJson.Normalize(once));
    }

    [Fact]
    public void The_first_attempt_for_a_version_is_allowed_and_remembered()
    {
        var allowed = UpdateAttemptPolicy.TryRecord(new SystemSettings(), "8.0.1", T0, out var next);

        Assert.True(allowed);
        Assert.Equal("8.0.1", next.UpdateAttemptVersion);
        Assert.Equal(T0, next.UpdateAttemptSince);
        Assert.Equal(1, next.UpdateAttempts);
    }

    [Fact]
    public void The_same_version_may_be_tried_twice_in_24_hours_and_the_third_time_is_refused()
    {
        var system = new SystemSettings();
        Assert.True(UpdateAttemptPolicy.TryRecord(system, "8.0.1", T0, out system));
        Assert.True(UpdateAttemptPolicy.TryRecord(system, "8.0.1", T0.AddHours(1), out system));
        Assert.Equal(2, system.UpdateAttempts);

        var allowed = UpdateAttemptPolicy.TryRecord(system, "8.0.1", T0.AddHours(2), out var after);

        Assert.False(allowed);
        Assert.Equal(system, after); // a refusal records nothing
    }

    [Fact]
    public void The_refusal_ends_24_hours_after_the_first_attempt_and_the_count_starts_again()
    {
        var system = new SystemSettings { UpdateAttemptVersion = "8.0.1", UpdateAttemptSince = T0, UpdateAttempts = 2 };

        Assert.False(UpdateAttemptPolicy.TryRecord(system, "8.0.1", T0.AddHours(23.9), out _));

        Assert.True(UpdateAttemptPolicy.TryRecord(system, "8.0.1", T0.AddHours(24), out var next));
        Assert.Equal(1, next.UpdateAttempts);
        Assert.Equal(T0.AddHours(24), next.UpdateAttemptSince);
    }

    [Fact]
    public void A_different_version_starts_its_own_count()
    {
        var system = new SystemSettings { UpdateAttemptVersion = "8.0.1", UpdateAttemptSince = T0, UpdateAttempts = 2 };

        Assert.True(UpdateAttemptPolicy.TryRecord(system, "8.0.2", T0.AddMinutes(5), out var next));

        Assert.Equal("8.0.2", next.UpdateAttemptVersion);
        Assert.Equal(1, next.UpdateAttempts);
    }

    [Fact]
    public void A_clock_that_went_back_does_not_lock_the_version_out_for_ever()
    {
        var system = new SystemSettings { UpdateAttemptVersion = "8.0.1", UpdateAttemptSince = T0, UpdateAttempts = 2 };

        Assert.True(UpdateAttemptPolicy.TryRecord(system, "8.0.1", T0.AddDays(-3), out var next));

        Assert.Equal(1, next.UpdateAttempts);
    }
}
