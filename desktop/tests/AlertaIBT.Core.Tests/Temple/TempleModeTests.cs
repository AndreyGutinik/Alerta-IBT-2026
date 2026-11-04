using AlertaIBT.Core.Backup;
using AlertaIBT.Core.Control;
using AlertaIBT.Core.Http;
using AlertaIBT.Core.Ndi;
using AlertaIBT.Core.Player;
using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Temple;
using AlertaIBT.Core.Tests.Control;
using AlertaIBT.Core.Tests.Ndi;
using AlertaIBT.Core.Tests.Settings;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Temple;

// Batch 41: "Templo menor". The pure rule table, the effective view the services read, persistence, backup, the screen size and the services that really stop.
public sealed class TempleModeTests : IDisposable
{
    private static readonly TempleFeature[] Every = [.. TempleFeatures.All];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alertaibt-temple-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new();
    private readonly List<IDisposable> _open = [];

    public TempleModeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var item in _open)
        {
            item.Dispose();
        }

        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static TempleSettings Temple(bool on, int mask) => new()
    {
        Enabled = on,
        Disabled = Every.Where((_, i) => (mask & (1 << i)) != 0).Select(TempleFeatures.Key).ToArray(),
    };

    private SettingsService Settings()
    {
        var settings = new SettingsService(new SettingsStore(Path.Combine(_dir, "settings.json"), _clock), _clock);
        _open.Add(settings);
        return settings;
    }

    // ---- the rule table ----

    [Fact]
    public void Every_combination_of_the_seven_gear_items_follows_the_rule_with_the_mode_on_and_off()
    {
        for (var mask = 0; mask < 1 << Every.Length; mask++)
        {
            foreach (var on in new[] { false, true })
            {
                var temple = Temple(on, mask);
                for (var i = 0; i < Every.Length; i++)
                {
                    var listed = (mask & (1 << i)) != 0;
                    Assert.Equal(!(on && listed), temple.IsActive(Every[i]));
                }
            }
        }
    }

    [Fact]
    public void With_the_mode_off_everything_runs_whatever_the_list_says_and_the_effective_view_is_the_same_instance()
    {
        var settings = new AppSettings { Temple = Temple(false, 0b1111111) };

        Assert.All(Every, f => Assert.True(settings.IsActive(f)));
        Assert.Same(settings, settings.Effective());
        Assert.Equal(TempleTabs.Home | TempleTabs.Som | TempleTabs.Ndi | TempleTabs.Control | TempleTabs.Settings, TempleProfile.VisibleTabs(settings.Temple));
    }

    [Fact]
    public void The_first_time_the_mode_is_on_nothing_is_listed_so_nothing_is_off()
    {
        var settings = new AppSettings { Temple = new TempleSettings { Enabled = true } };

        Assert.Empty(settings.Temple.Disabled);
        Assert.All(Every, f => Assert.True(settings.IsActive(f)));
        Assert.Same(settings, settings.Effective());
    }

    [Fact]
    public void The_alerts_switch_of_the_owner_is_independent_of_the_mode()
    {
        foreach (var userSwitch in new[] { true, false })
        {
            foreach (var alertsOff in new[] { true, false })
            {
                var s = new AppSettings
                {
                    System = new SystemSettings { AlertsEnabled = userSwitch },
                    Temple = Temple(true, alertsOff ? 1 : 0),
                };

                Assert.Equal(userSwitch && !alertsOff, s.AlertsRun());
                Assert.Equal(userSwitch, s.System.AlertsEnabled); // the owner's own value is never rewritten
                Assert.Equal(userSwitch && !alertsOff, s.Effective().System.AlertsEnabled);
            }
        }
    }

    [Fact]
    public void Leaving_the_mode_gives_back_exactly_the_values_of_the_owner()
    {
        var owner = new AppSettings
        {
            System = new SystemSettings { AlertsEnabled = true },
            Player = new PlayerSettings { Enabled = true, AlarmsEnabled = true, MediaKeys = true },
            Control = new ControlSettings { Enabled = true },
            Ndi = new NdiSettings { LayerOn = true, TestPattern = true },
            Holyrics = new HolyricsSettings { PresentationMode = true },
            Temple = Temple(true, 0b1111111),
        };

        var seen = owner.Effective();
        Assert.False(seen.System.AlertsEnabled);
        Assert.False(seen.Player.Enabled);
        Assert.False(seen.Player.AlarmsEnabled);
        Assert.False(seen.Player.MediaKeys);
        Assert.False(seen.Control.Enabled);
        Assert.False(seen.Ndi.LayerOn);
        Assert.False(seen.Ndi.TestPattern);
        Assert.False(seen.Holyrics.PresentationMode);
        Assert.Equal("offline", seen.Correction.Mode);

        var left = owner with { Temple = owner.Temple with { Enabled = false } };

        Assert.True(left.Effective().System.AlertsEnabled);
        Assert.True(left.Effective().Player.Enabled);
        Assert.True(left.Effective().Control.Enabled);
        Assert.True(left.Effective().Ndi.LayerOn);
        Assert.True(left.Effective().Holyrics.PresentationMode);
        Assert.True(owner.System.AlertsEnabled && owner.Player.Enabled && owner.Control.Enabled); // the raw values never moved
    }

    [Theory]
    [InlineData(TempleFeature.Alerts)]
    [InlineData(TempleFeature.Player)]
    [InlineData(TempleFeature.StartupAudio)]
    [InlineData(TempleFeature.Ndi)]
    [InlineData(TempleFeature.Ma2)]
    [InlineData(TempleFeature.Holyrics)]
    [InlineData(TempleFeature.Gemini)]
    public void Each_function_switches_off_only_its_own_fields(TempleFeature feature)
    {
        var on = new AppSettings
        {
            Site = new SiteSettings { Enabled = true },
            Ndi = new NdiSettings { LayerOn = true, TestPattern = true },
            Holyrics = new HolyricsSettings { PresentationMode = true },
            Control = new ControlSettings { Enabled = true },
            Temple = new TempleSettings { Enabled = true, Disabled = [TempleFeatures.Key(feature)] },
        };

        var view = on.Effective();

        Assert.Equal(feature != TempleFeature.Alerts, view.System.AlertsEnabled);
        Assert.Equal(feature != TempleFeature.Alerts, view.Site.Enabled);
        Assert.Equal(feature != TempleFeature.Player, view.Player.Enabled);
        Assert.Equal(feature != TempleFeature.StartupAudio, view.Audio.Enabled);
        Assert.Equal(feature != TempleFeature.StartupAudio, view.Audio.LockOutput);
        Assert.Equal(feature != TempleFeature.StartupAudio, view.Audio.HolyricsRoute);
        Assert.Equal(feature != TempleFeature.Ndi, view.Ndi.LayerOn);
        Assert.Equal(feature != TempleFeature.Ma2, view.Control.Enabled);
        Assert.Equal(feature != TempleFeature.Holyrics, view.Holyrics.PresentationMode);
        Assert.Equal(feature != TempleFeature.Gemini, view.Correction.Mode == "gemini");
    }

    // ---- the tabs ----

    [Fact]
    public void The_tabs_follow_the_functions_and_the_first_visible_tab_is_Inicio_then_Som_then_NDI_then_Controle_then_Configuracoes()
    {
        TempleTabs Tabs(params TempleFeature[] off) => TempleProfile.VisibleTabs(new TempleSettings { Enabled = true, Disabled = off.Select(TempleFeatures.Key).ToArray() });
        TempleTabs First(params TempleFeature[] off) => TempleProfile.FirstVisible(new TempleSettings { Enabled = true, Disabled = off.Select(TempleFeatures.Key).ToArray() });

        Assert.False(Tabs(TempleFeature.Alerts).HasFlag(TempleTabs.Home));
        Assert.True(Tabs(TempleFeature.Player).HasFlag(TempleTabs.Som)); // the startup volumes keep the tab
        Assert.False(Tabs(TempleFeature.Player, TempleFeature.StartupAudio).HasFlag(TempleTabs.Som));
        Assert.False(Tabs(TempleFeature.Ndi).HasFlag(TempleTabs.Ndi));
        Assert.True(Tabs(TempleFeature.Ma2).HasFlag(TempleTabs.Control)); // the Holyrics card keeps it
        Assert.False(Tabs(TempleFeature.Ma2, TempleFeature.Holyrics).HasFlag(TempleTabs.Control));
        Assert.True(Tabs(Every).HasFlag(TempleTabs.Settings));

        Assert.Equal(TempleTabs.Som, First(TempleFeature.Alerts));
        Assert.Equal(TempleTabs.Ndi, First(TempleFeature.Alerts, TempleFeature.Player, TempleFeature.StartupAudio));
        Assert.Equal(TempleTabs.Control, First(TempleFeature.Alerts, TempleFeature.Player, TempleFeature.StartupAudio, TempleFeature.Ndi));
        Assert.Equal(TempleTabs.Settings, First(Every));
        Assert.Equal(TempleTabs.Home, First());
    }

    [Fact]
    public void The_plan_lists_what_stops_what_starts_and_the_size()
    {
        var before = new TempleSettings { Enabled = false };
        var after = new TempleSettings { Enabled = true, Disabled = [TempleFeatures.Key(TempleFeature.Alerts), TempleFeatures.Key(TempleFeature.Ndi)] };

        var plan = TempleProfile.Plan(before, after);

        Assert.Equal([TempleFeature.Alerts, TempleFeature.Ndi], plan.Stops);
        Assert.Empty(plan.Starts);
        Assert.Equal(new TargetSize(2560, 768), plan.From);
        Assert.Equal(new TargetSize(1920, 1080), plan.To);
        Assert.False(plan.IsEmpty);

        var back = TempleProfile.Plan(after, before);
        Assert.Equal([TempleFeature.Alerts, TempleFeature.Ndi], back.Starts);
        Assert.Empty(back.Stops);
        Assert.Equal(new TargetSize(2560, 768), back.To);

        // Changing the list while the mode is on: only the difference.
        var plus = after with { Disabled = [TempleFeatures.Key(TempleFeature.Alerts), TempleFeatures.Key(TempleFeature.Player)] };
        var change = TempleProfile.Plan(after, plus);
        Assert.Equal([TempleFeature.Player], change.Stops);
        Assert.Equal([TempleFeature.Ndi], change.Starts);
        Assert.True(TempleProfile.Plan(after, after).IsEmpty);
    }

    // ---- persistence ----

    [Fact]
    public void The_mode_the_list_and_the_sizes_survive_a_save_and_a_load()
    {
        var saved = new AppSettings
        {
            Temple = new TempleSettings
            {
                Enabled = true,
                Disabled = [TempleFeatures.Key(TempleFeature.Gemini), TempleFeatures.Key(TempleFeature.Alerts)],
                NormalWidth = 3840,
                NormalHeight = 1080,
                TempleWidth = 1280,
                TempleHeight = 720,
            },
        };

        var (loaded, corrupt) = SettingsJson.Load(SettingsJson.Save(saved));

        Assert.False(corrupt);
        Assert.True(loaded.Temple.Enabled);
        Assert.Equal(["alerts", "gemini"], loaded.Temple.Disabled); // known names, in the fixed order
        Assert.Equal(new TargetSize(3840, 1080), loaded.Temple.NormalSize);
        Assert.Equal(new TargetSize(1280, 720), loaded.Temple.Target);
    }

    [Fact]
    public void An_older_file_without_the_section_loads_the_defaults_and_bad_values_are_held()
    {
        var (old, _) = SettingsJson.Load("""{"version":1,"bar":{"height":60}}""");
        Assert.False(old.Temple.Enabled);
        Assert.Empty(old.Temple.Disabled);
        Assert.Equal(new TargetSize(2560, 768), old.Temple.Target);

        var (bad, _) = SettingsJson.Load("""{"temple":{"enabled":true,"disabled":["nope","Ndi","ndi",""],"normalWidth":0,"normalHeight":-5,"templeWidth":99999,"templeHeight":10}}""");
        Assert.Equal(["ndi"], bad.Temple.Disabled);
        Assert.Equal(new TargetSize(2560, 768), bad.Temple.NormalSize);
        Assert.Equal(new TargetSize(1920, 1080), bad.Temple.TempleSize);
    }

    [Fact]
    public void The_mode_comes_back_after_a_restart_and_the_next_run_sees_it_before_anything_starts()
    {
        using (var first = Settings())
        {
            var service = new TempleModeService(first);
            service.Apply(true, [TempleFeature.Alerts, TempleFeature.Player]);
            first.Flush();
        }

        var second = Settings();
        var temple = new TempleModeService(second);

        Assert.True(temple.IsOn);
        Assert.False(temple.IsFeatureActive(TempleFeature.Alerts));
        Assert.False(temple.IsFeatureActive(TempleFeature.Player));
        Assert.True(temple.IsFeatureActive(TempleFeature.Ndi));
        Assert.Equal(new TargetSize(1920, 1080), temple.Target);
        Assert.False(second.Current.AlertsRun());
    }

    [Fact]
    public void The_service_tells_when_the_mode_the_list_or_a_size_changes_and_not_for_anything_else()
    {
        var settings = Settings();
        var temple = new TempleModeService(settings);
        var changes = 0;
        temple.Changed += () => changes++;

        settings.Update(s => s with { Bar = s.Bar with { Height = 70 } });
        Assert.Equal(0, changes);

        temple.SetOn(true);
        Assert.Equal(1, changes);
        temple.SetOn(true); // nothing changed
        Assert.Equal(1, changes);
        temple.Apply(true, [TempleFeature.Ndi]);
        Assert.Equal(2, changes);
        temple.SetSizes(new TargetSize(2560, 768), new TargetSize(1600, 900));
        Assert.Equal(3, changes);
        Assert.Equal(new TargetSize(1600, 900), temple.Target);

        temple.SetOn(false);
        Assert.Equal(new TargetSize(2560, 768), temple.Target);
        Assert.Equal([TempleFeature.Ndi], temple.Disabled); // the list is kept for the next time
    }

    [Fact]
    public void Resolution_edits_are_validated_when_saved()
    {
        var settings = Settings();
        var temple = new TempleModeService(settings);

        temple.SetSizes(new TargetSize(10, 99999), new TargetSize(1920, 1080));
        settings.Flush();
        var (loaded, _) = SettingsJson.Load(File.ReadAllText(Path.Combine(_dir, "settings.json")));

        Assert.Equal(new TargetSize(2560, 768), loaded.Temple.NormalSize); // out of range: back to its default
        Assert.Equal("1920 x 1080", loaded.Temple.TempleSize.ToString());
    }

    [Fact]
    public void The_status_body_of_the_api_has_the_mode_the_list_and_the_resolution()
    {
        var settings = Settings();
        var temple = new TempleModeService(settings);
        temple.Apply(true, [TempleFeature.Alerts, TempleFeature.Player]);

        var body = temple.StatusBody();

        Assert.Equal(true, body["templeMode"]);
        Assert.Equal(new[] { "alerts", "player" }, (string[])body["disabled"]!);
        var resolution = (Dictionary<string, object?>)body["resolution"]!;
        Assert.Equal(1920, resolution["width"]);
        Assert.Equal(1080, resolution["height"]);
        Assert.Equal("1920 x 1080", resolution["text"]);
    }

    // ---- backup ----

    [Fact]
    public void The_backup_carries_the_mode_and_the_list_and_the_preview_shows_them()
    {
        var current = new AppSettings();
        var withTemple = new AppSettings { Temple = Temple(true, 0b0000101) };
        var protector = new FakeProtector();

        var payload = SettingsBackup.Capture(withTemple, protector, DateTimeOffset.UnixEpoch);
        var restored = SettingsBackup.Apply(current, payload, protector);

        Assert.True(restored.Temple.Enabled);
        Assert.Equal(["alerts", "audio"], restored.Temple.Disabled);
        Assert.Contains("Templo menor", SettingsBackup.ItemLabels);
        var preview = SettingsBackup.Preview(current, payload, protector);
        Assert.Equal(SettingsBackup.ItemLabels.Count, preview.Count);
        var line = preview.Single(c => c.Label == "Templo menor");
        Assert.True(line.Changed);
        Assert.Equal("Desligado", line.Old);
        Assert.Equal("Ligado", line.New);
        Assert.Contains("Avisos", preview.Single(c => c.Label.StartsWith("Fun", StringComparison.Ordinal)).New);
    }

    [Fact]
    public void An_older_backup_without_the_items_leaves_the_mode_and_the_list_untouched()
    {
        var current = new AppSettings { Temple = Temple(true, 0b0000010) };
        var protector = new FakeProtector();
        var payload = SettingsBackup.Capture(new AppSettings(), protector, DateTimeOffset.UnixEpoch) with { TempleMode = null, TempleDisabled = null };

        var restored = SettingsBackup.Apply(current, payload, protector);

        Assert.True(restored.Temple.Enabled);
        Assert.Equal(["player"], restored.Temple.Disabled);
        Assert.False(SettingsBackup.Preview(current, payload, protector).Any(c => c.Label.Contains("emplo", StringComparison.Ordinal) && c.Changed));
    }

    // ---- the screen ----

    private static ScreenInfo Screen(string name, int width, int height, bool primary = false) => new(@"\\?\DISPLAY#" + name, @"\\.\" + name, 0, 0, width, height, primary);

    [Fact]
    public void The_led_is_the_monitor_of_the_active_size()
    {
        var desk = Screen("DISPLAY1", 2560, 1440, primary: true);
        var wide = Screen("DISPLAY2", 2560, 768);
        var small = Screen("DISPLAY3", 1920, 1080);
        IReadOnlyList<ScreenInfo> screens = [desk, wide, small];

        Assert.Same(wide, LedSelector.Select(screens, null));
        Assert.Same(wide, LedSelector.Select(screens, null, TargetSize.DefaultNormal));
        Assert.Same(small, LedSelector.Select(screens, null, TargetSize.DefaultTemple));
        Assert.Null(LedSelector.Select([desk, wide], null, TargetSize.DefaultTemple));
        Assert.Equal(1920d / 1080, TargetSize.DefaultTemple.Aspect, 6);
        Assert.Equal("1920 x 1080", TargetSize.DefaultTemple.ToString());
    }

    [Fact]
    public void The_tracker_looks_for_the_new_size_as_soon_as_the_target_changes()
    {
        var desk = Screen("DISPLAY1", 2560, 1440, primary: true);
        var wide = Screen("DISPLAY2", 2560, 768);
        var small = Screen("DISPLAY3", 1920, 1080);
        var target = TargetSize.DefaultNormal;
        var tracker = new LedTracker(() => [desk, wide, small], () => null, testMode: false, () => target);

        Assert.True(tracker.Refresh());
        Assert.Same(wide, tracker.Current);

        target = TargetSize.DefaultTemple;
        Assert.True(tracker.Refresh());
        Assert.Same(small, tracker.Current);

        target = new TargetSize(1000, 1000);
        Assert.True(tracker.Refresh());
        Assert.Null(tracker.Current); // no monitor of that size: the "LED off" state, as always
    }

    [Fact]
    public void A_1920x1080_source_into_a_1920x1080_target_has_no_crop_and_the_vertical_position_means_nothing()
    {
        var crop = NdiCropMath.Compute(1920, 1080, 1920, 1080, 30);

        Assert.Equal(1.0, crop.Scale, 9);
        Assert.Equal(0, crop.HiddenRows);
        Assert.Equal(0, crop.HiddenColumns);
        Assert.Equal((0d, 0d, 1d, 1d), (crop.U0, crop.V0, crop.U1, crop.V1));
        Assert.False(NdiCropMath.HasVerticalCrop(1920, 1080, 1920, 1080));

        // The same camera on the LED still hides rows, and every other source size keeps working.
        Assert.True(NdiCropMath.HasVerticalCrop(1920, 1080, 2560, 768));
        Assert.True(NdiCropMath.HasVerticalCrop(1280, 960, 1920, 1080)); // 4:3 into 16:9 overflows vertically
        Assert.False(NdiCropMath.HasVerticalCrop(3840, 1080, 1920, 1080)); // wider than the target: the sides are cut, not the rows
        Assert.False(NdiCropMath.HasVerticalCrop(3840, 2160, 1920, 1080));
        var wide = NdiCropMath.Compute(3840, 1080, 1920, 1080, 50);
        Assert.Equal(0, wide.HiddenRows);
        Assert.True(wide.HiddenColumns > 0);
    }

    // ---- the api routes ----

    [Fact]
    public void The_routes_under_templo_are_read_like_the_other_ones()
    {
        Assert.True(TempleIntent.IsTemplePath("/templo/status"));
        Assert.True(TempleIntent.IsTemplePath("/TEMPLO"));
        Assert.False(TempleIntent.IsTemplePath("/templomais"));
        Assert.Equal(TempleIntentKind.On, TempleIntent.Parse("GET", "/templo/on"));
        Assert.Equal(TempleIntentKind.Off, TempleIntent.Parse("POST", "/Templo/off/?x=1"));
        Assert.Equal(TempleIntentKind.Status, TempleIntent.Parse("GET", "/templo/status"));
        Assert.Equal(TempleIntentKind.NotFound, TempleIntent.Parse("DELETE", "/templo/on"));
        Assert.Equal(TempleIntentKind.NotFound, TempleIntent.Parse("GET", "/templo/other"));
    }

    [Fact]
    public void The_on_and_off_routes_turn_the_mode_on_and_off_and_answer_the_status()
    {
        var settings = Settings();
        var temple = new TempleModeService(settings);

        var on = TempleIntent.Execute(TempleIntentKind.On, temple);
        Assert.Equal(true, on["templeMode"]);
        Assert.True(settings.Current.Temple.Enabled);

        var status = TempleIntent.Execute(TempleIntentKind.Status, temple);
        Assert.Equal(true, status["templeMode"]);

        var off = TempleIntent.Execute(TempleIntentKind.Off, temple);
        Assert.Equal(false, off["templeMode"]);
        Assert.False(settings.Current.Temple.Enabled);
    }

    // ---- the services really stop and come back ----

    [Fact]
    public void The_artnet_socket_closes_while_the_ma2_is_off_and_opens_again_without_touching_the_owners_switch()
    {
        var settings = Settings();
        var sources = new List<FakeArtNetSource>();
        using var service = new ControlService(settings, new FakeControlTargets(), () =>
        {
            var source = new FakeArtNetSource();
            sources.Add(source);
            return source;
        });
        settings.Update(s => s with { Control = s.Control with { Enabled = true } });
        service.Start();
        Assert.Single(sources);
        Assert.False(sources[0].Disposed);

        settings.Update(s => s with { Temple = new TempleSettings { Enabled = true, Disabled = [TempleFeatures.Key(TempleFeature.Ma2)] } });

        Assert.True(sources[0].Disposed); // the UDP socket is closed
        Assert.True(settings.Current.Control.Enabled); // the owner's switch is untouched

        settings.Update(s => s with { Temple = s.Temple with { Enabled = false } });

        Assert.Equal(2, sources.Count);
        Assert.False(sources[1].Disposed);
    }

    [Fact]
    public void A_start_with_the_mode_on_never_opens_the_artnet_socket()
    {
        var settings = Settings();
        settings.Update(s => s with
        {
            Control = s.Control with { Enabled = true },
            Temple = new TempleSettings { Enabled = true, Disabled = [TempleFeatures.Key(TempleFeature.Ma2)] },
        });
        var opened = 0;
        using var service = new ControlService(settings, new FakeControlTargets(), () =>
        {
            opened++;
            return new FakeArtNetSource();
        });

        service.Start();

        Assert.Equal(0, opened);
    }

    [Fact]
    public void The_alarms_and_the_player_read_as_off_in_the_effective_view()
    {
        var now = new DateTime(2026, 11, 8, 9, 30, 0); // a Sunday
        var player = new PlayerSettings
        {
            Enabled = true,
            AlarmsEnabled = true,
            Alarms = [new AlarmEntry("a", true, "09:00", "10:00", AlarmEntry.AllDays, "Louvor")],
        };
        var settings = new AppSettings { Player = player, Temple = Temple(true, 0b10) };

        Assert.Empty(AlarmRules.Due(settings.Effective().Player, now));
        Assert.Equal("Player desligado", AlarmRules.Describe(settings.Effective().Player, now));
    }

    [Fact]
    public void The_ndi_layer_the_receivers_and_the_commands_stop_and_the_saved_switches_come_back_with_the_function()
    {
        var settings = Settings();
        settings.Update(s => s with
        {
            Ndi = s.Ndi with
            {
                LayerOn = true,
                Cameras = [new NdiCameraSlot("Camera 1", "192.168.1.50"), .. s.Ndi.Cameras.Skip(1)],
            },
        });
        var receivers = new List<FakeCameraReceiver>();
        using var service = new NdiService(
            new FakeNdiRuntime(),
            settings,
            _clock,
            receiverFactory: _ =>
            {
                var receiver = new FakeCameraReceiver(_clock);
                receivers.Add(receiver);
                return receiver;
            });
        service.RestoreSavedState();
        Assert.True(service.LayerOn);

        settings.Update(s => s with { Temple = new TempleSettings { Enabled = true, Disabled = [TempleFeatures.Key(TempleFeature.Ndi)] } });

        Assert.True(service.Suspended);
        Assert.False(service.LayerOn);
        Assert.False(service.SetLayer(true).Success);
        Assert.False(service.SelectPreview(0).Success);
        Assert.False(service.Take().Success);
        Assert.True(settings.Current.Ndi.LayerOn); // the owner's last position is still saved

        settings.Update(s => s with { Temple = s.Temple with { Enabled = false } });

        Assert.False(service.Suspended);
        Assert.True(service.LayerOn); // back through the real restore path
    }

    [Fact]
    public void The_holyrics_client_sends_nothing_while_the_control_is_off()
    {
        var settings = Settings();
        var protector = new FakeProtector();
        settings.Update(s => s.WithHolyricsToken("token-123", protector));
        using var client = new AlertaIBT.Core.Holyrics.HolyricsClient(settings, protector, minInterval: TimeSpan.Zero);
        Assert.True(client.IsConfigured);

        settings.Update(s => s with { Temple = new TempleSettings { Enabled = true, Disabled = [TempleFeatures.Key(TempleFeature.Holyrics)] } });

        Assert.False(client.IsConfigured);
        Assert.False(client.ArrowsActive);
        Assert.False(client.Post(new AlertaIBT.Core.Holyrics.HolyricsCommand(AlertaIBT.Core.Holyrics.HolyricsActionKind.SlideNext)));

        settings.Update(s => s with { Temple = s.Temple with { Enabled = false } });
        Assert.True(client.IsConfigured);
    }
}
