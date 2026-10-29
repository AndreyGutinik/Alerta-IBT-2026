using System.Globalization;
using System.Text;
using AlertaIBT.App.Platform;
using AlertaIBT.Core.Player;

namespace AlertaIBT.App.Tests.Player;

/// <summary>
/// A LOCAL diagnostic, not a regression test: it reads (never copies) pairs of the owner's real FLACs and measures what a crossfade really sounds like
/// through the real chain. It skips cleanly when the folder is absent (CI, other PCs). The report goes to ALERTAIBT_DIAG_DIR (or the temp folder).
/// </summary>
public sealed class RealMusicCrossfadeDiagnostic
{
    private const string Folder = @"C:\Users\gutni\Music\Mornye\Igreja";
    private const int Rate = RealChainRig.Rate;

    [Fact]
    public void Measure_real_pairs()
    {
        if (!Directory.Exists(Folder))
        {
            Assert.Skip("pasta de musicas do dono ausente");
        }

        var files = Directory.EnumerateFiles(Folder, "*.flac").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count < 8)
        {
            Assert.Skip("poucas musicas");
        }

        var random = new Random(26);
        var picked = files.OrderBy(_ => random.Next()).Take(8).ToList();
        var report = new StringBuilder();
        var backend = new NAudioPlayerBackend();
        for (var pair = 0; pair < picked.Count; pair += 2)
        {
            var a = Decode(backend, picked[pair]);
            var b = Decode(backend, picked[pair + 1]);
            report.AppendLine(CultureInfo.InvariantCulture, $"=== PAR {pair / 2 + 1}: A={Path.GetFileName(a.Path)} | B={Path.GetFileName(b.Path)}");
            report.AppendLine(CultureInfo.InvariantCulture, $"A header {a.Header:F3}s decoded {a.Samples.Length / 2.0 / Rate:F3}s | B header {b.Header:F3}s decoded {b.Samples.Length / 2.0 / Rate:F3}s");
            report.AppendLine(CultureInfo.InvariantCulture, $"A efetivo fim (-50 dBFS) {EffectiveEnd(a.Samples, -50):F2}s de {a.Samples.Length / 2.0 / Rate:F2}s | B inicio efetivo {EffectiveStart(b.Samples, -50):F2}s");
            report.AppendLine(CultureInfo.InvariantCulture, $"A ultimos 15s por 1s (dBFS): {string.Join(' ', Windows(a.Samples, a.Samples.Length / 2.0 / Rate - 15, 15, 1.0).Select(Db))}");
            report.AppendLine(CultureInfo.InvariantCulture, $"B primeiros 15s por 1s (dBFS): {string.Join(' ', Windows(b.Samples, 0, 15, 1.0).Select(Db))}");
            foreach (var n in new[] { 12.0, 5.0, 3.0 })
            {
                Overlap(report, picked[pair], picked[pair + 1], a, b, n);
            }
        }

        var dir = Environment.GetEnvironmentVariable("ALERTAIBT_DIAG_DIR") ?? Path.GetTempPath();
        File.WriteAllText(Path.Combine(dir, "crossfade-real-AFTER.txt"), report.ToString());
    }

    private static void Overlap(StringBuilder report, string pathA, string pathB, Decoded a, Decoded b, double n)
    {
        using var rig = new RealChainRig([pathA, pathB], "all");
        rig.Change(p => p with { CrossfadeSeconds = n });
        rig.Player.Play();
        var duration = rig.Player.GetStatus().Duration.TotalSeconds;
        var profile = rig.Backend.Analyze(rig.Paths[0], Rate, 2);
        var anchor = profile is null ? duration : profile.EffectiveEnd.TotalSeconds;
        var startB = profile is null ? 0 : rig.Backend.Analyze(rig.Paths[1], Rate, 2)!.EffectiveStart.TotalSeconds;
        var seekTo = anchor - n - 1.0;
        Assert.True(rig.Player.Seek(TimeSpan.FromSeconds(seekTo)).Success);
        rig.Output.Pull(Rate / 10); // the duck and the jump
        var t0 = rig.Player.GetStatus().Position.TotalSeconds;
        var total = n + 2.5;
        var mix = new List<float>();
        var switchedAt = -1.0;
        for (var i = 0; i < (int)(total * 100); i++)
        {
            var pulled = rig.Output.Pull(Rate / 100);
            mix.AddRange(pulled);
            if (switchedAt < 0 && rig.Player.GetStatus().Track!.Path == rig.Paths[1])
            {
                switchedAt = (i + 1) / 100.0;
            }
        }

        var m = mix.ToArray();
        // The mix starts at A time t0; the overlap should begin at (duration - n) of A.
        report.AppendLine(CultureInfo.InvariantCulture, $"-- N={n}s: A duration {duration:F3}s, seek {seekTo:F2}s, capture t0={t0:F3}s, UI switched at capture +{switchedAt:F2}s (expected overlap start ~+{anchor - n - t0:F2}s; anchor A end {anchor:F2}s, B start {startB:F2}s)");
        report.AppendLine("t(s rel overlap)  A_alone  B_alone  MIX   (dBFS RMS per 100 ms)  mixGain: cos/sin power check");
        var step = 0.1;
        var rows = (int)(n / step);
        double minMix = 0;
        double sumMix = 0;
        for (var r = 0; r < rows; r++)
        {
            var tA = anchor - n + (r * step);
            var aAlone = Rms(a.Samples, tA, step);
            var bAlone = Rms(b.Samples, startB + (r * step), step);
            var mixRms = Rms(m, (anchor - n - t0) + (r * step), step);
            var progress = (r + 0.5) / rows;
            var (co, si) = PlayerMixer.CrossfadeGains(progress);
            var expected = Math.Sqrt(Math.Pow(aAlone * co, 2) + Math.Pow(bAlone * si, 2));
            sumMix += mixRms;
            if (r % 5 == 0 || r == rows - 1)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"{r * step,6:F1}  {Db(aAlone),7}  {Db(bAlone),7}  {Db(mixRms),7}  expect~{Db(expected)} (cos {co:F2} sin {si:F2})");
            }

            minMix = r == 0 ? mixRms : Math.Min(minMix, mixRms);
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"   A avg {Db(Windows(a.Samples, anchor - n, n, n)[0])} | B avg {Db(Windows(b.Samples, startB, n, n)[0])} | MIX avg {Db(sumMix / rows)} min {Db(minMix)}");
        report.AppendLine(CultureInfo.InvariantCulture, $"   A first half {Db(Windows(a.Samples, anchor - n, n / 2, n / 2)[0])} second half {Db(Windows(a.Samples, anchor - n / 2, n / 2, n / 2)[0])} | B first half {Db(Windows(b.Samples, startB, n / 2, n / 2)[0])} second half {Db(Windows(b.Samples, startB + (n / 2), n / 2, n / 2)[0])}");
    }

    private static string Db(double v) => (20 * Math.Log10(Math.Max(v, 1e-6))).ToString("F1", CultureInfo.InvariantCulture);

    private static List<double> Windows(float[] x, double start, double length, double window)
    {
        var list = new List<double>();
        for (var t = start; t + window <= start + length + 1e-6; t += window)
        {
            list.Add(Rms(x, t, window));
        }

        return list;
    }

    private static double Rms(float[] x, double startSeconds, double lengthSeconds)
    {
        var from = Math.Max(0, (int)Math.Round(startSeconds * Rate) * 2);
        var count = (int)Math.Round(lengthSeconds * Rate) * 2;
        double sum = 0;
        var n = 0;
        for (var i = from; i < from + count && i < x.Length; i++)
        {
            sum += (double)x[i] * x[i];
            n++;
        }

        return n == 0 ? 0 : Math.Sqrt(sum / n);
    }

    private static double EffectiveEnd(float[] x, double thresholdDb)
    {
        var threshold = Math.Pow(10, thresholdDb / 20);
        for (var i = x.Length - 1; i >= 0; i--)
        {
            if (Math.Abs(x[i]) > threshold)
            {
                return i / 2.0 / Rate;
            }
        }

        return 0;
    }

    private static double EffectiveStart(float[] x, double thresholdDb)
    {
        var threshold = Math.Pow(10, thresholdDb / 20);
        for (var i = 0; i < x.Length; i++)
        {
            if (Math.Abs(x[i]) > threshold)
            {
                return i / 2.0 / Rate;
            }
        }

        return 0;
    }

    private static Decoded Decode(NAudioPlayerBackend backend, string path)
    {
        using var track = backend.OpenTrack(path, Rate, 2);
        var all = new List<float>();
        var buffer = new float[Rate * 2];
        int n;
        while ((n = track.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < n; i++)
            {
                all.Add(buffer[i]);
            }
        }

        return new Decoded(path, track.Duration.TotalSeconds, all.ToArray());
    }

    private sealed record Decoded(string Path, double Header, float[] Samples);
}
