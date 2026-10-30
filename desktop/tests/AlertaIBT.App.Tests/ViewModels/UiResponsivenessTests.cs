using System.Diagnostics;
using AlertaIBT.App.Platform;
using AlertaIBT.Core.Loudness;
using AlertaIBT.Core.Player;

namespace AlertaIBT.App.Tests.ViewModels;

/// <summary>
/// The owner's freeze: turning on "Gravar etiquetas nos arquivos" froze the screen. These tests flip the switches (tags, "Igualar volume", all
/// four combinations) on a real player that is playing real FLACs while the real scanner measures and writes tags into TEMP copies, and measure the
/// UI thread: no call may keep it more than 100 ms and the heartbeat must not wait long. The local diagnostic at the end reads the owner's real
/// folder (enumerating and measuring only, never a write) and skips when the folder is not there.
/// </summary>
public sealed class UiResponsivenessTests
{
    private const double LimitMs = 100;
    private static readonly string[] Pieces = ["plain16", "cover24", "tags24"];

    private static string FfmpegPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "ffmpeg.exe");

    private static bool HasFfmpeg() => File.Exists(FfmpegPath());

    private static void Ffmpeg(params string[] args)
    {
        var info = new ProcessStartInfo(FfmpegPath()) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-v", "error", "-y" }.Concat(args))
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("ffmpeg: " + error);
        }
    }

    // A folder of FLACs: noise-colored tones (so the files are not tiny), a third of them 24 bit with a cover.
    private static string MakeMusic(int count, int seconds)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ibt-ui-music-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var cover = Path.Combine(dir, "cover.png");
        Ffmpeg("-f", "lavfi", "-i", "color=c=green:s=300x300", "-frames:v", "1", cover);
        var made = new List<string>();
        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
        {
            var file = Path.Combine(dir, $"{i + 1:00} {Pieces[i % 3]}.flac");
            var tags = new[] { "-metadata", $"title=Louvor {i + 1}", "-metadata", "artist=Teste", "-metadata", "album=Culto" };
            var source = $"anoisesrc=d={seconds}:c=pink:r={(i % 3 == 1 ? 96000 : 44100)}:a={(0.1 + (i % 5) * 0.12).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}";
            if (i % 3 == 1)
            {
                Ffmpeg(["-f", "lavfi", "-i", source, "-i", cover, "-map", "0:a", "-map", "1:v", "-ac", "2", "-sample_fmt", "s32", "-bits_per_raw_sample", "24", "-c:v", "copy", "-disposition:v", "attached_pic", .. tags, file]);
            }
            else
            {
                Ffmpeg(["-f", "lavfi", "-i", source, "-ac", "2", "-sample_fmt", "s16", .. tags, file]);
            }

            lock (made)
            {
                made.Add(file);
            }
        });
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // Cleaned later.
        }
    }

    private static int TagsWritten(string dir) =>
        Directory.EnumerateFiles(dir, "*.flac").Count(f => FlacLoudnessTagWriter.ReadMarked(f) is not null);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Turning_the_switches_on_and_off_never_holds_the_UI_thread_while_a_track_plays_and_the_scanner_runs(bool normalize, bool tags)
    {
        Assert.SkipUnless(HasFfmpeg(), "ffmpeg nao encontrado");
        var dir = MakeMusic(14, 6);
        try
        {
            using var rig = new UiResponsivenessRig(dir);
            rig.StartHeartbeat();
            rig.Change(p => p with { NormalizeEnabled = normalize });
            rig.StartScanner();
            rig.OnUi(() => rig.View.PlayCommand.Execute(null));
            Thread.Sleep(800);
            rig.ResetHeartbeat();

            var calls = new List<(string What, double Ms)>();
            for (var round = 0; round < 4; round++)
            {
                var on = round % 2 == 0;
                calls.Add(("tags " + on, rig.OnUi(() => rig.View.WriteLoudnessTags = on).TotalMilliseconds));
                Thread.Sleep(200);
                calls.Add(("normalize " + !on, rig.OnUi(() => rig.View.NormalizeEnabled = !on).TotalMilliseconds));
                Thread.Sleep(200);
            }

            var slowest = calls.MaxBy(c => c.Ms);
            var report = $"normalize={normalize} tags={tags}: slowest call {slowest.What} {slowest.Ms:0.0} ms; heartbeat max {rig.MaxHeartbeatGap.TotalMilliseconds:0.0} ms, over 100 ms: {rig.HeartbeatsOver100Ms}";
            Report(report);
            Assert.True(slowest.Ms < LimitMs, report);
            Assert.True(rig.MaxHeartbeatGap.TotalMilliseconds < LimitMs * 2, report);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void The_tag_writer_only_runs_after_the_switch_and_never_stalls_the_UI_thread_or_the_sound()
    {
        Assert.SkipUnless(HasFfmpeg(), "ffmpeg nao encontrado");
        var dir = MakeMusic(10, 8);
        try
        {
            var options = new ScannerOptions { TagInterval = TimeSpan.FromMilliseconds(200), IdleAfterActivity = TimeSpan.FromMilliseconds(200), MaxCpuShare = 1.0 };
            using var rig = new UiResponsivenessRig(dir, scanner: options);
            rig.StartHeartbeat();
            rig.Change(p => p with { NormalizeEnabled = false });
            rig.StartScanner();
            rig.OnUi(() => rig.View.PlayCommand.Execute(null));

            // Measured first, with the switch off: nothing is written.
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(60) && rig.Loudness.Scanner.Progress.Measured < 10)
            {
                Thread.Sleep(100);
            }

            Assert.Equal(0, TagsWritten(dir));
            rig.ResetHeartbeat();
            var call = rig.OnUi(() => rig.View.WriteLoudnessTags = true).TotalMilliseconds;

            wait.Restart();
            while (wait.Elapsed < TimeSpan.FromSeconds(60) && rig.Loudness.Scanner.Progress.TagsPending > 0)
            {
                Thread.Sleep(100);
            }

            var report = $"switch call {call:0.0} ms; heartbeat max {rig.MaxHeartbeatGap.TotalMilliseconds:0.0} ms, over 100 ms {rig.HeartbeatsOver100Ms}; tags written {TagsWritten(dir)}/10";
            Report(report);
            Assert.True(call < LimitMs, report);
            Assert.True(rig.MaxHeartbeatGap.TotalMilliseconds < LimitMs * 2, report);
            Assert.True(TagsWritten(dir) > 0, report);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // The owner's real folder, READ ONLY: the library enumerates it and the scanner measures a few files; never a tag write (the switch stays off).
    [Fact]
    public void Local_diagnostic_on_the_owners_real_folder_reads_only()
    {
        const string real = @"C:\Users\gutni\Music\Mornye\Igreja";
        Assert.SkipUnless(Directory.Exists(real) && Environment.GetEnvironmentVariable("IBT_REAL_MUSIC_DIAG") == "1", "diagnostico local (defina IBT_REAL_MUSIC_DIAG=1)");
        using var rig = new UiResponsivenessRig(real);
        rig.StartHeartbeat();
        var started = Stopwatch.StartNew();
        rig.StartScanner();
        var enumerate = started.Elapsed;
        rig.OnUi(() => rig.View.PlayCommand.Execute(null));
        Thread.Sleep(3000);
        rig.ResetHeartbeat();
        var calls = new List<double>();
        for (var round = 0; round < 4; round++)
        {
            calls.Add(rig.OnUi(() => rig.View.NormalizeEnabled = round % 2 == 1).TotalMilliseconds);
            Thread.Sleep(500);
        }

        // The tag switch is flipped only in the temp settings and the writer is NOT given a chance: the scan has nothing measured yet in the first seconds,
        // and the files are in use or unmeasured; to be sure the test leaves it off.
        var report = $"real folder: {rig.Library.Tracks("Igreja").Count} files enumerated in {enumerate.TotalMilliseconds:0} ms; normalize calls max {calls.Max():0.0} ms; heartbeat max {rig.MaxHeartbeatGap.TotalMilliseconds:0.0} ms; measured {rig.Loudness.Scanner.Progress.Measured}";
        Report(report);
        Assert.True(calls.Max() < LimitMs, report);
    }


    // Read-only look at the real Windows audio (the same calls the player and the sound rules make on every settings change).
    [Fact]
    public void Local_diagnostic_real_windows_audio_calls_are_read_only()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("IBT_REAL_MUSIC_DIAG") == "1", "diagnostico local (defina IBT_REAL_MUSIC_DIAG=1)");
        using var audio = new WindowsAudioSystem();
        var lines = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var watch = Stopwatch.StartNew();
            var devices = audio.ActiveRenderDevices();
            var devicesMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            var defaults = audio.GetDefaultDevices();
            var defaultsMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            var sessions = devices.Sum(d => audio.GetSessions(d.Id).Count);
            lines.Add($"devices {devices.Count} in {devicesMs:0.0} ms; defaults {defaultsMs:0.0} ms; sessions {sessions} in {watch.Elapsed.TotalMilliseconds:0.0} ms");
        }

        Report("real audio: " + string.Join(" | ", lines));
    }

    private static void Report(string text)
    {
        TestContext.Current.SendDiagnosticMessage(text);
        var file = Environment.GetEnvironmentVariable("IBT_UI_REPORT");
        if (!string.IsNullOrEmpty(file))
        {
            File.AppendAllText(file, text + Environment.NewLine);
        }
    }
}
