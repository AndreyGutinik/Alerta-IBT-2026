using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AlertaIBT.App.Hosting;
using AlertaIBT.App.Platform;
using AlertaIBT.App.Tests.Platform;
using AlertaIBT.App.Tests.ViewModels;
using AlertaIBT.Core.Audio;
using AlertaIBT.Core.Input;
using Microsoft.Extensions.DependencyInjection;

namespace AlertaIBT.App.Tests.Tray;

// Batch 42: the owner's log of 2026-11-06 21:04 and 11-08 12:42 shows "Saindo a pedido do usuario", "encerrando", "Mouse destravado" and then nothing: the quit froze on the UI
// thread and the process lived on with no window (single-instance lock held, Telegram and the API still running). The steps of the shell's Dispose that talk to Windows
// services are bounded now, so a Windows call that never returns cannot freeze the quit.
public sealed class QuitNeverFreezesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-quit-" + Guid.NewGuid().ToString("N"));

    public QuitNeverFreezesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Serilog.Log.CloseAndFlush();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A log file still held by the logger: the OS temp cleaner takes it.
        }
    }

    private ServiceProvider Provider(IAudioSystem audio)
    {
        var paths = new AppPaths(_dir);
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var services = new ServiceCollection();
        services.AddLogging();
        AppHost.ConfigureServices(services, paths, new StartupArguments(Tray: true, TestLed: false), port);
        services.AddSingleton<INativeCursor>(new FakeCursor());
        services.AddSingleton<IKeyboardHook>(new FakeKeyboardHook());
        services.AddSingleton<IKeyboardGate>(new FakeKeyboardGate());
        services.AddSingleton(audio); // the last registration wins
        return services.BuildServiceProvider();
    }

    [Fact]
    public void A_windows_audio_service_that_never_answers_does_not_freeze_the_shell_Dispose()
    {
        WpfTestHost.Run(() =>
        {
            using var never = new ManualResetEventSlim();
            var audio = new HungAudio(never);
            using var provider = Provider(audio);
            var shell = provider.GetRequiredService<AppShell>();

            var clock = Stopwatch.StartNew();
            shell.Dispose();
            clock.Stop();
            never.Set(); // let the abandoned helper thread end

            Assert.True(audio.DisposeStarted, "the audio step really started");
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"the quit took {clock.Elapsed.TotalSeconds:0.0} s: it waited for the audio service");
        });
    }

    [Fact]
    public void A_normal_quit_goes_through_every_step_quickly()
    {
        WpfTestHost.Run(() =>
        {
            var audio = new HungAudio(null);
            using var provider = Provider(audio);
            var shell = provider.GetRequiredService<AppShell>();

            var clock = Stopwatch.StartNew();
            shell.Dispose();
            clock.Stop();

            Assert.True(audio.DisposeStarted);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"a normal quit took {clock.Elapsed.TotalSeconds:0.0} s");
        });
    }

    // A Windows audio service that does not answer: Dispose waits on the gate (null = answers at once).
    private sealed class HungAudio(ManualResetEventSlim? hang) : IAudioSystem
    {
        public bool DisposeStarted { get; private set; }

        public event Action? DevicesChanged
        {
            add { }
            remove { }
        }

        public event Action? DefaultDeviceChanged
        {
            add { }
            remove { }
        }

        public event Action? SessionCreated
        {
            add { }
            remove { }
        }

        public IReadOnlyList<AudioDevice> ActiveRenderDevices() => [];

        public DefaultDevices GetDefaultDevices() => new(null, null, null);

        public bool SetDefaultRenderDevice(string deviceId) => true;

        public bool SetMasterVolume(string deviceId, int percent, bool muted) => true;

        public IReadOnlyList<AudioSessionInfo> GetSessions(string deviceId) => [];

        public bool SetSessionVolume(string deviceId, string sessionKey, int percent, bool muted) => true;

        public void WatchSessions(IReadOnlyList<string> deviceIds)
        {
        }

        public void Dispose()
        {
            DisposeStarted = true;
            hang?.Wait();
        }
    }
}
