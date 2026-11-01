using AlertaIBT.Core.Ndi;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Ndi;

// Batch 39: the telao layer and the color bars come back as the owner left them (they used to start OFF at every open).
public sealed class NdiSavedStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alertaibt-ndi-saved-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new();
    private readonly List<IDisposable> _open = [];

    public NdiSavedStateTests() => Directory.CreateDirectory(_dir);

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

    // One "run of the app": settings read from the same file, a fresh service on top of them.
    private NdiService Boot(out SettingsService settings, out List<FakeCameraReceiver> receivers, FakeNdiRuntime? runtime = null)
    {
        settings = new SettingsService(new SettingsStore(Path.Combine(_dir, "settings.json"), _clock), _clock);
        _open.Add(settings);
        var list = new List<FakeCameraReceiver>();
        receivers = list;
        var service = new NdiService(
            runtime ?? new FakeNdiRuntime(),
            settings,
            _clock,
            receiverFactory: _ =>
            {
                var r = new FakeCameraReceiver(_clock);
                list.Add(r);
                return r;
            });
        _open.Add(service);
        return service;
    }

    [Fact]
    public void A_first_install_starts_with_the_layer_and_the_bars_off_and_restores_nothing()
    {
        using var service = Boot(out _, out _);
        var visible = new List<bool>();
        service.WindowVisibleChanged += visible.Add;

        service.RestoreSavedState();

        Assert.False(service.LayerOn);
        Assert.False(service.Switcher.TestPattern);
        Assert.Empty(visible);
    }

    [Fact]
    public void Turning_the_layer_on_is_saved_and_the_next_run_puts_it_back_and_the_window_shows_when_a_frame_arrives()
    {
        var first = Boot(out var settings1, out _);
        settings1.Update(s => s with { Ndi = NdiTestSettings.With() });
        first.SetLayer(true);
        settings1.Flush();
        first.Dispose();
        settings1.Dispose();

        // The next run: the service exists, the window host listens, then the shell restores.
        using var second = Boot(out var settings2, out var receivers);
        Assert.False(second.LayerOn); // nothing is applied before the shell asks (startup order: services first, switches after)
        var visible = new List<bool>();
        second.WindowVisibleChanged += visible.Add;
        second.RestoreSavedState();

        Assert.True(second.LayerOn);
        Assert.True(settings2.Current.Ndi.LayerOn);
        Assert.Empty(visible); // no camera on air yet: nothing shows

        Assert.True(second.SelectPreview(0).Success);
        receivers[1].Push();
        Assert.True(second.Take().Success);

        Assert.Equal([true], visible);
        Assert.True(second.Switcher.WindowVisible);
    }

    [Fact]
    public void The_layer_on_with_no_source_and_no_ndi_runtime_shows_nothing_and_raises_no_error()
    {
        var first = Boot(out var settings1, out _);
        first.SetLayer(true);
        settings1.Flush();
        first.Dispose();
        settings1.Dispose();

        using var second = Boot(out _, out _, new FakeNdiRuntime { IsAvailable = false });
        var errors = new List<string>();
        var visible = new List<bool>();
        second.Error += errors.Add;
        second.WindowVisibleChanged += visible.Add;

        second.RestoreSavedState();

        Assert.True(second.LayerOn);
        Assert.False(second.Switcher.WindowVisible);
        Assert.Empty(visible);
        Assert.Empty(errors);
        Assert.False(second.GetStatus().RuntimeOk);
    }

    [Fact]
    public void The_color_bars_are_saved_and_come_back_showing_the_window_even_with_the_layer_off()
    {
        var first = Boot(out var settings1, out _);
        first.SetTestPattern(true);
        settings1.Flush();
        first.Dispose();
        settings1.Dispose();

        using var second = Boot(out _, out _);
        var visible = new List<bool>();
        second.WindowVisibleChanged += visible.Add;
        second.RestoreSavedState();

        Assert.True(second.Switcher.TestPattern);
        Assert.False(second.LayerOn);
        Assert.Equal([true], visible);
    }

    [Fact]
    public void Turning_both_off_is_saved_too_so_the_next_run_starts_off()
    {
        var first = Boot(out var settings1, out _);
        first.SetLayer(true);
        first.SetTestPattern(true);
        first.ToggleLayer();
        first.SetTestPattern(false);
        settings1.Flush();
        first.Dispose();
        settings1.Dispose();

        using var second = Boot(out var settings2, out _);
        second.RestoreSavedState();

        Assert.False(second.LayerOn);
        Assert.False(second.Switcher.TestPattern);
        Assert.False(settings2.Current.Ndi.LayerOn);
        Assert.False(settings2.Current.Ndi.TestPattern);
    }

    [Fact]
    public void Every_way_to_flip_the_layer_saves_it_the_api_the_toggle_and_the_console()
    {
        using var service = Boot(out var settings, out _);

        service.SetLayer(true); // the switch, the API and the MA2 rows all end in this call
        Assert.True(settings.Current.Ndi.LayerOn);
        service.ToggleLayer(); // the shortcut
        Assert.False(settings.Current.Ndi.LayerOn);
    }

    [Fact]
    public void Restoring_does_not_touch_the_cameras_or_the_other_ndi_settings()
    {
        var first = Boot(out var settings1, out _);
        settings1.Update(s => s with { Ndi = NdiTestSettings.With() with { HideFromCapture = true, ShortcutsEnabled = false } });
        first.SetLayer(true);
        settings1.Flush();
        first.Dispose();
        settings1.Dispose();

        using var second = Boot(out var settings2, out _);
        second.RestoreSavedState();

        Assert.True(settings2.Current.Ndi.HideFromCapture);
        Assert.False(settings2.Current.Ndi.ShortcutsEnabled);
        Assert.Equal(NdiTestSettings.With().Cameras, settings2.Current.Ndi.Cameras);
    }
}
