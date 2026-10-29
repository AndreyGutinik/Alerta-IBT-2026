using System.Diagnostics;
using AlertaIBT.Core.Input;
using AlertaIBT.Core.Ndi;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Ndi;

public sealed class NdiSupportTests
{
    private static byte[] Gradient(int w, int h)
    {
        var data = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var o = (y * w + x) * 4;
                data[o] = (byte)(x * 255 / Math.Max(1, w - 1));
                data[o + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                data[o + 2] = 77;
                data[o + 3] = 255;
            }
        }

        return data;
    }

    [Fact]
    public void Mailbox_gives_the_newest_frame_and_skips_old_ones()
    {
        var box = new FrameMailbox();
        var data = new byte[16 * 4 * 4];
        data[0] = 1;
        box.Post(new NdiFrameView(16, 4, 64, data, 0), 10);
        data[0] = 2;
        box.Post(new NdiFrameView(16, 4, 64, data, 0), 20);

        Assert.True(box.TryAcquire(0, out var frame));
        Assert.Equal(2, frame.Buffer[0]);
        Assert.Equal(20, frame.VerticalPosition);
        Assert.Equal(2, frame.Sequence);
        box.Release();
        Assert.False(box.TryAcquire(frame.Sequence, out _));
    }

    [Fact]
    public void Mailbox_does_not_overwrite_a_frame_being_read()
    {
        var box = new FrameMailbox();
        var data = new byte[64];
        data[0] = 1;
        box.Post(new NdiFrameView(4, 4, 16, data, 0), 0);
        Assert.True(box.TryAcquire(0, out var reading));
        for (byte i = 2; i < 10; i++)
        {
            data[0] = i;
            box.Post(new NdiFrameView(4, 4, 16, data, 0), 0);
        }

        Assert.Equal(1, reading.Buffer[0]);
        box.Release();
    }

    [Fact]
    public void Mailbox_allocates_nothing_per_frame_in_steady_state()
    {
        var box = new FrameMailbox();
        var data = new byte[1920 * 1080 * 4];
        for (var i = 0; i < 10; i++)
        {
            box.Post(new NdiFrameView(1920, 1080, 1920 * 4, data, 0), 50);
            if (box.TryAcquire(0, out _))
            {
                box.Release();
            }
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 120; i++)
        {
            box.Post(new NdiFrameView(1920, 1080, 1920 * 4, data, 0), 50);
            if (box.TryAcquire(0, out _))
            {
                box.Release();
            }
        }

        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 4096);
    }

    [Fact]
    public void Thumbnail_is_480_wide_keeps_the_aspect_and_averages_the_colors()
    {
        var clock = new FakeTimeProvider();
        var thumb = new ThumbnailBuffer();
        var data = Gradient(1920, 1080);

        Assert.True(thumb.Update(new NdiFrameView(1920, 1080, 1920 * 4, data, 0), TimeSpan.Zero, clock));

        var snapshot = thumb.Snapshot();
        Assert.NotNull(snapshot);
        Assert.Equal(480, snapshot.Width);
        Assert.Equal(270, snapshot.Height);
        Assert.Equal(480 * 270 * 4, snapshot.Pixels.Length);
        var last = (269 * 480 + 479) * 4;
        Assert.InRange(snapshot.Pixels[last], 240, 255); // blue channel = x gradient at the right edge
        Assert.InRange(snapshot.Pixels[last + 1], 240, 255); // green = y gradient at the bottom
        Assert.Equal(77, snapshot.Pixels[last + 2]);
        Assert.Equal(255, snapshot.Pixels[last + 3]);
        Assert.InRange(snapshot.Pixels[0], 0, 4);
    }

    [Fact]
    public void Thumbnail_is_rate_limited_to_the_interval()
    {
        var clock = new FakeTimeProvider();
        var thumb = new ThumbnailBuffer();
        var data = Gradient(64, 36);
        var frame = new NdiFrameView(64, 36, 64 * 4, data, 0);

        Assert.True(thumb.Update(frame, NdiSwitcher.ThumbnailInterval, clock));
        Assert.False(thumb.Update(frame, NdiSwitcher.ThumbnailInterval, clock));
        clock.Advance(TimeSpan.FromMilliseconds(60));
        Assert.True(thumb.Update(frame, NdiSwitcher.ThumbnailInterval, clock));
        Assert.Equal(2, thumb.Version);
    }

    [Fact]
    public void Thumbnail_copy_needs_a_big_enough_buffer()
    {
        var clock = new FakeTimeProvider();
        var thumb = new ThumbnailBuffer();
        thumb.Update(new NdiFrameView(64, 36, 256, Gradient(64, 36), 0), TimeSpan.Zero, clock);

        Assert.False(thumb.TryCopy(new byte[10], out _, out _, out _));
        Assert.True(thumb.TryCopy(new byte[480 * 270 * 4], out var w, out var h, out _));
        Assert.Equal((480, 270), (w, h));
    }

    [Theory]
    [InlineData("PC (Camera 1)", "PC (Camera 1)", null)]
    [InlineData("192.168.1.50", null, "192.168.1.50")]
    [InlineData(" 192.168.1.50:5961 ", null, "192.168.1.50:5961")]
    [InlineData("camera.local", null, "camera.local")]
    public void Source_text_is_a_name_or_an_address(string text, string? name, string? url)
    {
        var source = NdiSourceParser.Parse(text);

        Assert.NotNull(source);
        Assert.Equal(name, source.Name);
        Assert.Equal(url, source.Url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_source_is_none(string? text) => Assert.Null(NdiSourceParser.Parse(text));

    [Fact]
    public void Locator_prefers_the_environment_variables_newest_first_then_the_default_folders()
    {
        var env = new Dictionary<string, string>
        {
            ["NDI_RUNTIME_DIR_V5"] = @"C:\v5",
            ["NDI_RUNTIME_DIR_V6"] = @"C:\v6",
        };

        var candidates = NdiRuntimeLocator.Candidates(name => env.GetValueOrDefault(name)).ToList();

        Assert.Equal(@"C:\v6", candidates[0]);
        Assert.Equal(@"C:\v5", candidates[1]);
        Assert.Contains(NdiRuntimeLocator.DefaultFolders[0], candidates);
    }

    [Fact]
    public void Locator_returns_the_first_existing_dll_or_null()
    {
        var found = NdiRuntimeLocator.FindDll(
            name => name == "NDI_RUNTIME_DIR_V6" ? @"C:\x" : null,
            path => path == Path.Combine(@"C:\x", NdiRuntimeLocator.DllName));

        Assert.Equal(Path.Combine(@"C:\x", NdiRuntimeLocator.DllName), found);
        Assert.Null(NdiRuntimeLocator.FindDll(_ => null, _ => false));
    }

    [Fact]
    public void Constants_carry_the_trademark_and_the_links()
    {
        Assert.Contains("registered trademark of Vizrt NDI AB", NdiConstants.TrademarkNotice);
        Assert.Equal("NDI(R) is a registered trademark of Vizrt NDI AB", NdiConstants.TrademarkNoticeAscii);
        Assert.Contains("ndi.video", NdiConstants.AboutText);
        Assert.Contains("https://ndi.video/tools/", NdiConstants.RuntimeMissingText);
    }

    [Fact]
    public void Monitor_chooser_uses_the_led_when_locked_and_the_named_monitor_when_unlocked()
    {
        var led = new Core.Screens.ScreenInfo("p1", @"\\.\DISPLAY2", 2560, 0, 2560, 768, false);
        var other = new Core.Screens.ScreenInfo("p2", @"\\.\DISPLAY3", 0, 0, 1920, 1080, true);
        var screens = new[] { led, other };

        Assert.Equal(led, NdiMonitorChooser.Choose(screens, led, new Core.Settings.NdiSettings()));
        Assert.Equal(other, NdiMonitorChooser.Choose(screens, led, new Core.Settings.NdiSettings { MonitorLocked = false, MonitorDeviceName = @"\\.\DISPLAY3" }));
        Assert.Equal(led, NdiMonitorChooser.Choose(screens, led, new Core.Settings.NdiSettings { MonitorLocked = false, MonitorDeviceName = @"\\.\DISPLAY9" }));
        Assert.Null(NdiMonitorChooser.Choose(screens, null, new Core.Settings.NdiSettings()));
    }

    [Fact]
    public void Pump_benchmark_60_fps_costs_under_4_ms_of_cpu_and_allocates_nothing_per_frame()
    {
        // The whole Core path of a 1920x1080 frame: switcher routing, thumbnail (20 fps), mailbox copy. The render thread is not in here.
        var clock = new FakeTimeProvider();
        var receivers = new List<FakeCameraReceiver>();
        var settings = NdiTestSettings.With();
        using var switcher = new NdiSwitcher(
            _ =>
            {
                var r = new FakeCameraReceiver(clock);
                receivers.Add(r);
                return r;
            },
            () => settings,
            clock);
        var box = new FrameMailbox();
        switcher.ProgramFrame += (in NdiFrameView frame, double position) => box.Post(frame, position);
        switcher.SelectPreview(0);
        var program = receivers[1];
        program.Push(1920, 1080);
        switcher.Take();

        var data = new byte[1920 * 1080 * 4];
        void Pump()
        {
            // Deliver straight to the lane handler without the fake's own per-push allocation.
            var view = new NdiFrameView(1920, 1080, 1920 * 4, data, clock.GetTimestamp());
            program.Raise(view);
        }

        for (var i = 0; i < 30; i++)
        {
            Pump();
            clock.Advance(TimeSpan.FromMilliseconds(17));
        }

        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        const int frames = 300;
        for (var i = 0; i < frames; i++)
        {
            Pump();
            clock.Advance(TimeSpan.FromMilliseconds(17));
        }

        sw.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var msPerFrame = sw.Elapsed.TotalMilliseconds / frames;

        Assert.True(msPerFrame < 4, $"{msPerFrame:0.00} ms por quadro");
        Assert.True(allocated / frames < 256, $"{allocated / frames} bytes por quadro");
    }

    [Fact]
    public void Hotkey_controller_registers_only_while_on_and_warns_about_conflicts()
    {
        var registry = new FakeRegistry { Refuse = [NdiHotkeyController.FirstSelectId + 1] };
        var control = new FakeControl();
        using var hotkeys = new NdiHotkeyController(registry, control);

        Assert.Empty(registry.Active);
        hotkeys.Apply(true);

        Assert.Equal(3, registry.Active.Count(id => id is >= NdiHotkeyController.FirstSelectId and < NdiHotkeyController.FirstSelectId + 4));
        Assert.Single(hotkeys.Warnings);
        Assert.Contains("F2", hotkeys.Warnings[0]);
        Assert.Contains(NdiHotkeyController.TakeId, registry.Active);

        hotkeys.Apply(false);
        Assert.Empty(registry.Active);
        Assert.Empty(hotkeys.Hotkeys);
    }

    [Fact]
    public void Hotkey_controller_maps_keys_to_actions_and_ignores_them_when_off()
    {
        var control = new FakeControl();
        using var hotkeys = new NdiHotkeyController(new FakeRegistry(), control);

        Assert.False(hotkeys.OnHotkey(NdiHotkeyController.TakeId));
        hotkeys.Apply(true);
        Assert.True(hotkeys.OnHotkey(NdiHotkeyController.FirstSelectId + 2));
        Assert.True(hotkeys.OnHotkey(NdiHotkeyController.TakeId));
        Assert.False(hotkeys.OnHotkey(0x1234));

        Assert.Equal(["select 2", "take"], control.Calls);
    }

    [Fact]
    public void Hotkey_controller_uses_plain_f_keys_and_ctrl_space()
    {
        var registry = new FakeRegistry();
        using var hotkeys = new NdiHotkeyController(registry, new FakeControl());

        hotkeys.Apply(true);

        Assert.Equal((HotkeyModifiers.None, 0x70), registry.Registered[NdiHotkeyController.FirstSelectId]);
        Assert.Equal((HotkeyModifiers.None, 0x73), registry.Registered[NdiHotkeyController.FirstSelectId + 3]);
        Assert.Equal((HotkeyModifiers.Control, 0x20), registry.Registered[NdiHotkeyController.TakeId]);
    }

    private sealed class FakeRegistry : INdiHotkeyRegistry
    {
        public HashSet<int> Refuse { get; init; } = [];

        public Dictionary<int, (HotkeyModifiers, int)> Registered { get; } = [];

        public IEnumerable<int> Active => Registered.Keys;

        public bool Register(int id, HotkeyModifiers modifiers, int virtualKey)
        {
            if (Refuse.Contains(id))
            {
                return false;
            }

            Registered[id] = (modifiers, virtualKey);
            return true;
        }

        public void Unregister(int id) => Registered.Remove(id);
    }

    private sealed class FakeControl : INdiControl
    {
        public List<string> Calls { get; } = [];

        public NdiResult SetLayer(bool on)
        {
            Calls.Add($"layer {on}");
            return NdiResult.Ok;
        }

        public NdiResult Take()
        {
            Calls.Add("take");
            return NdiResult.Ok;
        }

        public NdiResult SelectPreview(int slot)
        {
            Calls.Add($"select {slot}");
            return NdiResult.Ok;
        }

        public NdiStatus GetStatus() => throw new NotSupportedException();
    }
}
