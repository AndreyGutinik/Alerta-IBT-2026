using System.Diagnostics;
using AlertaIBT.Core.Audio;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Tests.Player;
using AlertaIBT.Core.Tests.Services;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Audio;

// A stalled Windows audio service (a program that does not answer, a device that is slow to open) must never freeze the screen: changing a setting
// is a call on the UI thread, and the handlers of the audio rules and of the player may not wait for the audio service on that thread.
public sealed class SettingsChangeNeverBlocksTests : IDisposable
{
    private const double LimitMs = 100;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-stall-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Cleaned later.
        }
    }

    // Runs the setting change on a thread of its own so that a handler that blocks cannot hang the test; returns how long it took (or the wait limit).
    private static double TimedUpdate(SettingsService settings, Func<AppSettings, AppSettings> change, TimeSpan give)
    {
        var watch = Stopwatch.StartNew();
        var thread = new Thread(() => settings.Update(change)) { IsBackground = true, Name = "pretend UI thread" };
        thread.Start();
        return thread.Join(give) ? watch.Elapsed.TotalMilliseconds : give.TotalMilliseconds;
    }

    [Fact]
    public void A_setting_change_does_not_wait_for_a_look_of_the_sound_rules_that_is_stuck_in_Windows()
    {
        Directory.CreateDirectory(_dir);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 14, 36, 0, TimeSpan.Zero));
        using var settings = new SettingsService(new SettingsStore(Path.Combine(_dir, "settings.json"), clock), clock);
        var audio = new FakeAudioSystem();
        using var controller = new AudioController(audio, settings, clock);
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        controller.Start();
        audio.OnRead = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        };

        // The look (after the start delay and the debounce) is stuck inside Windows.
        var look = new Thread(() => clock.Advance(AudioController.StartDelay + AudioController.Debounce)) { IsBackground = true, Name = "pretend timer thread" };
        look.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "the look did not start");

        try
        {
            var ms = TimedUpdate(settings, s => s with { Player = s.Player with { WriteLoudnessTags = !s.Player.WriteLoudnessTags } }, TimeSpan.FromSeconds(3));
            Assert.True(ms < LimitMs, $"the setting change kept the UI thread {ms:0} ms while the audio look was stuck");

            // Reading the status for the screen must not wait either.
            var reader = new Thread(() => _ = controller.Status) { IsBackground = true, Name = "pretend UI thread (status)" };
            reader.Start();
            Assert.True(reader.Join(TimeSpan.FromMilliseconds(500)), "Status waited for the look");
        }
        finally
        {
            release.Set();
            Assert.True(look.Join(TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public void A_setting_change_that_is_not_about_the_output_does_not_ask_Windows_for_the_devices_on_the_UI_thread()
    {
        using var rig = new PlayerRig();
        var calls = 0;
        rig.Audio.OnRead = () => Interlocked.Increment(ref calls);
        var before = calls;

        rig.Change(p => p with { WriteLoudnessTags = !p.WriteLoudnessTags });
        rig.Change(p => p with { NormalizeEnabled = !p.NormalizeEnabled });
        rig.Change(p => p with { Volume = 55 });

        Assert.Equal(before, calls);
    }

    [Fact]
    public void A_stalled_audio_service_does_not_hold_up_a_setting_change_in_the_player()
    {
        using var rig = new PlayerRig();
        var release = new ManualResetEventSlim();
        rig.Audio.OnRead = () => release.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        try
        {
            var ms = TimedUpdate(rig.Settings, s => s with { Player = s.Player with { WriteLoudnessTags = !s.Player.WriteLoudnessTags } }, TimeSpan.FromSeconds(3));
            Assert.True(ms < LimitMs, $"the setting change kept the UI thread {ms:0} ms while Windows audio was stuck");
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void Choosing_another_output_still_looks_at_the_devices()
    {
        using var rig = new PlayerRig();
        var calls = 0;
        rig.Audio.OnRead = () => Interlocked.Increment(ref calls);

        rig.PlugWing34();
        rig.Change(p => p with { OutputDeviceId = PlayerRig.Wing34, OutputDeviceName = "WING-USB (Out 3-4)" });

        Assert.True(calls > 0);
    }
}
