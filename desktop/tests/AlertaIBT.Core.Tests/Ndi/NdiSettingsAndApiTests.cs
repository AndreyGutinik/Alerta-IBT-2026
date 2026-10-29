using AlertaIBT.Core.Http;
using AlertaIBT.Core.Ndi;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Ndi;

public sealed class NdiSettingsAndApiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alertaibt-ndi-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new();

    public NdiSettingsAndApiTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Defaults_are_six_centered_empty_cameras_shortcuts_on_monitor_locked_visible_to_capture()
    {
        var ndi = new AppSettings().Ndi;

        Assert.True(ndi.ShortcutsEnabled);
        Assert.True(ndi.MonitorLocked);
        Assert.False(ndi.HideFromCapture);
        Assert.Empty(ndi.FixedIps);
        Assert.Equal(6, ndi.Cameras.Count);
        Assert.All(ndi.Cameras, c =>
        {
            Assert.Equal(50, c.VerticalPosition);
            Assert.Equal(string.Empty, c.Source);
        });
        Assert.Equal("C\U000000E2mera 1", ndi.Cameras[0].Name);
    }

    [Fact]
    public void An_old_file_without_ndi_loads_the_defaults()
    {
        var (settings, corrupt) = SettingsJson.Load("{\"bar\":{\"height\":60}}");

        Assert.False(corrupt);
        Assert.Equal(new NdiSettings(), settings.Ndi);
    }

    [Fact]
    public void Ndi_settings_round_trip()
    {
        var cameras = NdiTestSettings.With(2).Cameras.ToList();
        var original = new AppSettings
        {
            Ndi = new NdiSettings
            {
                ShortcutsEnabled = false,
                FixedIps = ["10.0.0.5", "10.0.0.6"],
                Cameras = cameras,
                MonitorLocked = false,
                MonitorDeviceName = @"\\.\DISPLAY3",
                HideFromCapture = true,
            },
        };

        var (loaded, _) = SettingsJson.Load(SettingsJson.Save(original));

        Assert.Equal(original.Ndi, loaded.Ndi);
    }

    [Fact]
    public void Cameras_are_always_six_and_positions_are_held_in_range()
    {
        var settings = new AppSettings
        {
            Ndi = new NdiSettings
            {
                Cameras = [new NdiCameraSlot("A", "x", 500), new NdiCameraSlot(" ", "  ", -9)],
                FixedIps = ["1.1.1.1", " ", "1.1.1.1"],
            },
        };

        var n = SettingsJson.Normalize(settings).Ndi;

        Assert.Equal(6, n.Cameras.Count);
        Assert.Equal(100, n.Cameras[0].VerticalPosition);
        Assert.Equal("C\U000000E2mera 2", n.Cameras[1].Name);
        Assert.Equal(string.Empty, n.Cameras[1].Source);
        Assert.Equal(0, n.Cameras[1].VerticalPosition);
        Assert.Equal("C\U000000E2mera 6", n.Cameras[5].Name);
        Assert.Equal(["1.1.1.1"], n.FixedIps);
    }

    [Fact]
    public void A_file_with_wrong_types_in_ndi_gives_defaults_per_value()
    {
        var (settings, corrupt) = SettingsJson.Load("{\"ndi\":{\"shortcutsEnabled\":\"yes\",\"cameras\":[1,{\"name\":\"X\",\"verticalPosition\":\"high\"}]}}");

        Assert.False(corrupt);
        Assert.True(settings.Ndi.ShortcutsEnabled);
        Assert.Equal("X", settings.Ndi.Cameras[1].Name);
        Assert.Equal(50, settings.Ndi.Cameras[1].VerticalPosition);
    }

    private NdiService NewService(out SettingsService settings, out List<FakeCameraReceiver> receivers, FakeNdiRuntime? runtime = null)
    {
        settings = new SettingsService(new SettingsStore(Path.Combine(_dir, "settings.json"), _clock), _clock);
        settings.Update(s => s with { Ndi = NdiTestSettings.With() });
        var list = new List<FakeCameraReceiver>();
        receivers = list;
        return new NdiService(
            runtime ?? new FakeNdiRuntime(),
            settings,
            _clock,
            receiverFactory: _ =>
            {
                var r = new FakeCameraReceiver(_clock);
                list.Add(r);
                return r;
            });
    }

    [Fact]
    public void Service_commands_drive_the_switcher_and_report_one_based_slots_in_the_api()
    {
        using var service = NewService(out _, out var receivers);

        Assert.True(service.SelectPreview(1).Success);
        receivers[1].Push();
        Assert.True(service.Take().Success);
        Assert.True(service.SetLayer(true).Success);

        var body = NdiIntent.Execute(NdiIntentKind.Status, service);

        Assert.Equal(true, body["layerOn"]);
        Assert.Equal(true, body["windowVisible"]);
        var program = Assert.IsType<Dictionary<string, object?>>(body["program"]);
        Assert.Equal(2, program["slot"]);
        Assert.Equal("Cam2", program["name"]);
    }

    [Fact]
    public void Service_saves_the_vertical_position_and_moves_the_picture()
    {
        using var service = NewService(out var settings, out var receivers);
        service.SelectPreview(0);
        receivers[1].Push();
        service.Take();
        double? moved = null;
        service.ProgramPositionChanged += position => moved = position;

        service.SetVerticalPosition(0, 85);

        Assert.Equal(85, settings.Current.Ndi.Cameras[0].VerticalPosition);
        Assert.Equal(85, moved);
    }

    [Fact]
    public void Service_set_camera_edits_name_and_source()
    {
        using var service = NewService(out var settings, out var receivers);

        service.SetCamera(4, " Fundo ", " 192.168.0.20 ");

        Assert.Equal("Fundo", settings.Current.Ndi.Cameras[4].Name);
        Assert.Equal("192.168.0.20", settings.Current.Ndi.Cameras[4].Source);
        Assert.False(service.SetCamera(9, "x", "y").Success);
        Assert.Empty(receivers.SelectMany(r => r.Connects));
    }

    [Fact]
    public void Layer_is_off_when_the_service_starts()
    {
        using var service = NewService(out _, out _);

        Assert.False(service.LayerOn);
        Assert.False(service.GetStatus().Switcher.WindowVisible);
    }

    [Fact]
    public void A_missing_runtime_is_reported_with_the_download_link()
    {
        using var service = NewService(out _, out _, new FakeNdiRuntime { IsAvailable = false });

        Assert.False(service.RuntimeAvailable);
        Assert.Contains("https://ndi.video/tools/", service.RuntimeText);
        Assert.False(service.GetStatus().RuntimeOk);
    }

    [Theory]
    [InlineData("GET", "/ndi/on", NdiIntentKind.On)]
    [InlineData("POST", "/NDI/Off/", NdiIntentKind.Off)]
    [InlineData("GET", "/ndi/take?x=1", NdiIntentKind.Take)]
    [InlineData("GET", "/ndi/status", NdiIntentKind.Status)]
    [InlineData("GET", "/ndi/select", NdiIntentKind.NotFound)]
    [InlineData("GET", "/ndi", NdiIntentKind.NotFound)]
    [InlineData("DELETE", "/ndi/on", NdiIntentKind.NotFound)]
    public void Routes_are_parsed(string method, string url, NdiIntentKind expected)
    {
        Assert.Equal(expected, NdiIntent.Parse(method, url));
    }

    [Fact]
    public void The_ndi_prefix_belongs_to_ndi()
    {
        Assert.True(NdiIntent.IsNdiPath("/ndi/typo"));
        Assert.True(NdiIntent.IsNdiPath("/ndi"));
        Assert.False(NdiIntent.IsNdiPath("/player/status"));
        Assert.False(NdiIntent.IsNdiPath("/ndix"));
    }

    [Fact]
    public void On_off_take_routes_call_the_control_and_take_never_selects()
    {
        using var service = NewService(out _, out var receivers);

        var take = NdiIntent.Execute(NdiIntentKind.Take, service);

        Assert.Equal(false, take["success"]); // nothing in preview: the cut is refused, no camera is chosen for the caller
        Assert.All(receivers, r => Assert.Empty(r.Connects));
        Assert.Equal(true, NdiIntent.Execute(NdiIntentKind.On, service)["success"]);
        Assert.True(service.LayerOn);
        Assert.Equal(true, NdiIntent.Execute(NdiIntentKind.Off, service)["success"]);
        Assert.False(service.LayerOn);
    }

    [Fact]
    public void Status_json_has_the_documented_fields()
    {
        using var service = NewService(out _, out _);

        var body = NdiIntent.Describe(service.GetStatus());

        foreach (var key in new[] { "runtimeOk", "layerOn", "windowVisible", "preview", "program", "fps", "lastFrameAgeMs", "takePending", "testPattern" })
        {
            Assert.True(body.ContainsKey(key), key);
        }

        Assert.Null(body["lastFrameAgeMs"]);
    }
}
