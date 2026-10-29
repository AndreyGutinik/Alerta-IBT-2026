using System.Diagnostics;
using System.Globalization;
using AlertaIBT.App.Platform;
using NAudio.CoreAudioApi;

namespace AlertaIBT.App.Tests.Platform;

/// <summary>
/// A LOCAL diagnostic, not a regression test: times the real Core Audio calls behind a volume change. It writes the SAME value it reads (nothing audible
/// changes) and skips when there is no output device. The report goes to ALERTAIBT_DIAG_DIR (or the temp folder).
/// </summary>
public sealed class RealAudioTimingDiagnostic
{
    [Fact]
    public void Time_the_real_volume_calls()
    {
        string? deviceId = null;
        int percent = 0;
        bool muted = false;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Console, out var device) || device is null)
            {
                Assert.Skip("sem saida de audio");
            }

            deviceId = device!.ID;
            percent = (int)Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
            muted = device.AudioEndpointVolume.Mute;
            device.Dispose();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Assert.Skip("audio indisponivel");
        }

        var lines = new List<string>();
        using var system = new WindowsAudioSystem();
        var sw = Stopwatch.StartNew();
        system.GetDefaultDevices();
        lines.Add($"first GetDefaultDevices (creates the enumerator): {sw.Elapsed.TotalMilliseconds:F1} ms");

        sw.Restart();
        system.SetMasterVolume(deviceId!, percent, muted);
        lines.Add($"SetMasterVolume first call (cold: GetDevice + activate endpoint volume): {sw.Elapsed.TotalMilliseconds:F1} ms");
        var warm = new List<double>();
        for (var i = 0; i < 50; i++)
        {
            sw.Restart();
            system.SetMasterVolume(deviceId!, percent, muted);
            warm.Add(sw.Elapsed.TotalMilliseconds);
        }

        lines.Add($"SetMasterVolume cached x50: avg {warm.Average():F2} ms, max {warm.Max():F2} ms");

        sw.Restart();
        var sessions = system.GetSessions(deviceId!);
        lines.Add($"GetSessions ({sessions.Count} sessions, cold, includes Process lookups): {sw.Elapsed.TotalMilliseconds:F1} ms");
        sw.Restart();
        sessions = system.GetSessions(deviceId!);
        lines.Add($"GetSessions again: {sw.Elapsed.TotalMilliseconds:F1} ms (this is what each old per-change lookup repeated, plus a RefreshSessions)");

        var dir = Environment.GetEnvironmentVariable("ALERTAIBT_DIAG_DIR") ?? Path.GetTempPath();
        File.WriteAllText(Path.Combine(dir, "audio-timing.txt"), string.Join(Environment.NewLine, lines.Select(l => l.ToString(CultureInfo.InvariantCulture))));
    }
}
