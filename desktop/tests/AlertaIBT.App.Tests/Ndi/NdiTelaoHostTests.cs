using System.Windows;
using AlertaIBT.App.Ndi;
using AlertaIBT.App.Overlay;
using AlertaIBT.Core.Ndi;
using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Settings;

namespace AlertaIBT.App.Tests.Ndi;

public sealed class NdiTelaoHostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alertaibt-host-" + Guid.NewGuid().ToString("N"));

    public NdiTelaoHostTests() => Directory.CreateDirectory(_dir);

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
    public void The_layer_starts_off_then_on_with_a_frame_shows_a_non_topmost_window_and_off_hides_it()
    {
        var spot = new ScreenInfo("t", "t", 150, 150, 640, 192, false);
        var receivers = new List<StubReceiver>();
        NdiService? service = null;
        NdiTelaoHost? host = null;
        SettingsService? settings = null;
        WpfTestHost.Run(() =>
        {
            settings = new SettingsService(new SettingsStore(Path.Combine(_dir, "settings.json")));
            settings.Update(s => s with
            {
                Ndi = s.Ndi with
                {
                    Cameras = s.Ndi.Cameras.Select((c, i) => i == 0 ? c with { Source = "PC (Cam)" } : c).ToList(),
                    HideFromCapture = true,
                },
            });
            service = new NdiService(
                new StubRuntime(),
                settings,
                receiverFactory: _ =>
                {
                    var r = new StubReceiver();
                    receivers.Add(r);
                    return r;
                });
            host = new NdiTelaoHost(service, settings, new StubLed(spot), new WpfUiDispatcher(Application.Current.Dispatcher), () => [spot]);
            host.Start();
        });

        try
        {
            Assert.False(service!.LayerOn); // always OFF at start
            Assert.Null(host!.Window); // and nothing GPU-related exists yet

            service.SelectPreview(0);
            receivers[1].Push();
            service.Take();
            service.SetLayer(true);

            Assert.True(WaitFor(() => host.Window is { IsShown: true }), "a janela da camera nao apareceu");
            WpfTestHost.Run(() =>
            {
                var facts = host.Window!.Facts();
                Assert.True(facts.Visible);
                Assert.False(facts.Topmost);
                Assert.True(facts.ClickThrough);
                Assert.True(host.Window.CaptureExcluded); // "Esconder da live" was on in the settings
            });

            service.SetLayer(false);
            Assert.True(WaitFor(() => host.Window is { IsShown: false }), "a janela da camera nao sumiu");
        }
        finally
        {
            WpfTestHost.Run(() =>
            {
                host?.Dispose();
                service?.Dispose();
                settings?.Dispose();
            });
        }
    }

    private static bool WaitFor(Func<bool> condition)
    {
        var end = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < end)
        {
            if (WpfTestHost.Run(condition))
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return WpfTestHost.Run(condition);
    }

    private sealed class StubLed(ScreenInfo? led) : ILedSource
    {
        public ScreenInfo? Led { get; } = led;

        public event Action<ScreenInfo?>? LedChanged
        {
            add { }
            remove { }
        }
    }

    private sealed class StubRuntime : INdiRuntime
    {
        public bool IsAvailable => true;

        public string Version => "stub";

        public string? UnavailableReason => null;

        public string? LoadedFrom => null;

        public INdiFinder CreateFinder(IReadOnlyList<string> extraIps) => throw new NotSupportedException();

        public INdiNativeReceiver CreateReceiver(NdiSourceRef source, string receiverName) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class StubReceiver : ICameraReceiver
    {
        private long _last;

        public event NdiFrameHandler? Frame;

        public event Action? StateChanged;

        public NdiConnectionState State { get; private set; }

        public NdiSourceRef? Source { get; private set; }

        public bool HasFrameSinceConnect { get; private set; }

        public long LastFrameTimestamp => _last;

        public long FramesReceived { get; private set; }

        public long FramesDropped => 0;

        public int Width { get; private set; }

        public int Height { get; private set; }

        public double Fps => 60;

        public string? LastError => null;

        public void Connect(NdiSourceRef? source)
        {
            Source = source;
            HasFrameSinceConnect = false;
            State = NdiConnectionState.Connecting;
            StateChanged?.Invoke();
        }

        public void Push()
        {
            var data = new byte[64 * 36 * 4];
            Array.Fill(data, (byte)200);
            _last = TimeProvider.System.GetTimestamp();
            FramesReceived++;
            Width = 64;
            Height = 36;
            HasFrameSinceConnect = true;
            State = NdiConnectionState.Connected;
            Frame?.Invoke(new NdiFrameView(64, 36, 256, data, _last));
        }

        public void Dispose()
        {
        }
    }
}

public sealed class NdiLicenseGuardTests
{
    [Fact]
    public void No_ndi_runtime_file_is_ever_part_of_the_build_output()
    {
        // The runtime is loaded from the NDI Tools folder at run time; nothing of NDI may be copied next to the app (public repo, license).
        Assert.Empty(Directory.GetFiles(AppContext.BaseDirectory, "Processing.NDI*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(AppContext.BaseDirectory, "*NDI*.dll", SearchOption.AllDirectories));
    }
}
