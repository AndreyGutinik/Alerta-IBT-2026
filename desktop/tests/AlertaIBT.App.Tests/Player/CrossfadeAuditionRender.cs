using System.Globalization;
using System.Text;
using AlertaIBT.App.Platform;
using AlertaIBT.Core.Player;

namespace AlertaIBT.App.Tests.Player;

/// <summary>
/// A LOCAL tool, not a regression test: it renders, OFFLINE through the real chain (RealChainRig: decoder, resampler, TrackAnalyzer, MusicPlayer and
/// PlayerMixer; only the sound card is replaced), the last 25 s of one of the owner's real tracks plus the first 25 s of another with an 8 s and a
/// 12 s crossfade, as 16-bit WAV files, and writes for each one a CSV with the MEASURED gain of each track (least squares of the mix on the two
/// decoded tracks), the ideal cos/sin and the levels in dB. It does nothing unless ALERTAIBT_AUDITION_DIR names the output folder, and it only
/// reads the music folder (never copies a track).
/// </summary>
public sealed class CrossfadeAuditionRender
{
    private const string Folder = @"C:\Users\gutni\Music\Mornye\Igreja";
    private const int Rate = RealChainRig.Rate;
    private const double Lead = 25;
    private const double Window = 0.1;

    [Fact]
    public void Render_audition_files()
    {
        var output = Environment.GetEnvironmentVariable("ALERTAIBT_AUDITION_DIR");
        if (string.IsNullOrWhiteSpace(output) || !Directory.Exists(Folder))
        {
            Assert.Skip("diagnostico local: defina ALERTAIBT_AUDITION_DIR");
        }

        Directory.CreateDirectory(output);
        var backend = new NAudioPlayerBackend();
        var files = Directory.EnumerateFiles(Folder, "*.flac").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(48).ToList();

        // Pick the track whose last 12 s are loudest (it ends while still playing) and a different one whose first 12 s are loudest.
        var scored = files.Select(f =>
        {
            var d = Decode(backend, f);
            var profile = TrackAnalyzer.Analyze(() => backend.OpenTrack(f, Rate, 2), Rate, 2);
            return (Path: f, Decoded: d, Profile: profile,
                Tail: Rms(d, profile.EffectiveEnd.TotalSeconds - 12, 12),
                Head: Rms(d, profile.EffectiveStart.TotalSeconds, 12));
        }).ToList();
        var a = scored.OrderByDescending(s => s.Tail).First();
        var b = scored.Where(s => s.Path != a.Path).OrderByDescending(s => s.Head).First();

        var readme = new StringBuilder();
        readme.AppendLine(CultureInfo.InvariantCulture, $"A={Path.GetFileName(a.Path)} fim efetivo {a.Profile.EffectiveEnd.TotalSeconds:F2}s nivel ultimos 12s {Db(a.Tail)} dBFS");
        readme.AppendLine(CultureInfo.InvariantCulture, $"B={Path.GetFileName(b.Path)} inicio efetivo {b.Profile.EffectiveStart.TotalSeconds:F2}s nivel primeiros 12s {Db(b.Head)} dBFS");
        foreach (var n in new[] { 8.0, 12.0 })
        {
            Render(output, a.Path, b.Path, a.Decoded, b.Decoded, a.Profile, b.Profile, n, readme);
        }

        File.WriteAllText(Path.Combine(output, "dados.txt"), readme.ToString());
    }

    private static void Render(string output, string pathA, string pathB, float[] a, float[] b, TrackProfile pa, TrackProfile pb, double n, StringBuilder readme)
    {
        using var rig = new RealChainRig([pathA, pathB], "all");
        rig.Change(p => p with { CrossfadeSeconds = n });
        rig.Player.Play();
        var anchor = pa.EffectiveEnd.TotalSeconds;
        Assert.True(rig.Player.Seek(TimeSpan.FromSeconds(anchor - Lead)).Success);
        rig.Output.Pull(Rate / 10); // the 12 ms duck and the jump
        var t0 = anchor - Lead; // the jump lands on the seek target; the reported position runs ahead of the sound (decoder read-ahead)
        var overlapAt = anchor - n - t0; // overlap start in the capture
        var total = overlapAt + n + (Lead - n) + 0.3;
        var mix = new List<float>();
        var switchedAt = -1.0;
        for (var i = 0; i < (int)(total * 100); i++)
        {
            mix.AddRange(rig.Output.Pull(Rate / 100));
            if (switchedAt < 0 && rig.Player.GetStatus().Track!.Path == rig.Paths[1])
            {
                switchedAt = (i + 1) / 100.0;
            }
        }

        var m = mix.ToArray();

        // A seek of Media Foundation on a FLAC lands a fraction of a second away from where it was asked (and the reported position is the asked
        // one), so the decoded references are aligned to the capture by correlation: A on the first seconds, B on the seconds after the overlap.
        var aShift = FindShift(a, m, 1.0, t0 + 1.0);
        var bShift = FindShift(b, m, overlapAt + n + 2.0, pb.EffectiveStart.TotalSeconds + n + 2.0);
        t0 += aShift;
        var startB = pb.EffectiveStart.TotalSeconds + bShift;
        var name = string.Create(CultureInfo.InvariantCulture, $"mescla-{n:0}s");
        WriteWav(Path.Combine(output, name + ".wav"), m);

        WriteCsv(Path.Combine(output, name + ".csv"), m, a, b, t0, startB, overlapAt, n);
        if (n >= 12)
        {
            var linear = LinearMix(m, a, b, t0, startB, overlapAt, n);
            WriteWav(Path.Combine(output, name + "-COMPARACAO-linear.wav"), linear);
            WriteCsv(Path.Combine(output, name + "-COMPARACAO-linear.csv"), linear, a, b, t0, startB, overlapAt, n);
        }

        readme.AppendLine(string.Create(CultureInfo.InvariantCulture, $"N={n}: a busca do FLAC (Media Foundation) caiu {aShift * 1000:F0} ms (A) e {bShift * 1000:F0} ms (B) do pedido; captura comeca em A {t0:F2}s; sobreposicao em +{overlapAt:F2}s ate +{overlapAt + n:F2}s (a tela trocou em +{switchedAt:F2}s); duracao {m.Length / 2.0 / Rate:F1}s"));
    }

    private static void WriteCsv(string path, float[] m, float[] a, float[] b, double t0, double startB, double overlapAt, double n)
    {
        var csv = new StringBuilder("t;gOut;gIn;cos;sin;mixDb;aDb;bDb\n");
        var rows = (int)(m.Length / 2.0 / Rate / Window);
        for (var r = 0; r < rows; r++)
        {
            var t = r * Window;
            var inOverlap = t >= overlapAt - 1e-6 && t < overlapAt + n - 1e-6;
            var after = t >= overlapAt + n - 1e-6;
            var ma = Slice(a, t0 + t);
            var mb = after || inOverlap ? Slice(b, startB + (t - overlapAt)) : null;
            var mz = Slice(m, t);
            double go;
            double gi;
            if (!inOverlap && !after)
            {
                go = Dot(mz, ma) / Math.Max(1e-12, Dot(ma, ma));
                gi = 0;
            }
            else if (after)
            {
                go = 0;
                gi = Dot(mz, mb!) / Math.Max(1e-12, Dot(mb!, mb!));
            }
            else
            {
                var saa = Dot(ma, ma);
                var sbb = Dot(mb!, mb!);
                var sab = Dot(ma, mb!);
                var sma = Dot(mz, ma);
                var smb = Dot(mz, mb!);
                var det = (saa * sbb) - (sab * sab);
                go = ((sma * sbb) - (smb * sab)) / det;
                gi = ((smb * saa) - (sma * sab)) / det;
            }

            var progress = Math.Clamp((t + (Window / 2) - overlapAt) / n, 0, 1);
            csv.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{t:F2};{go:F4};{gi:F4};{Math.Cos(progress * Math.PI / 2):F4};{Math.Sin(progress * Math.PI / 2):F4};{Db(Math.Sqrt(Dot(mz, mz) / mz.Length))};{Db(Math.Sqrt(Dot(ma, ma) / ma.Length))};{(mb is null ? "-120" : Db(Math.Sqrt(Dot(mb, mb) / mb.Length)))}"));
        }

        File.WriteAllText(path, csv.ToString());
    }

    // COMPARISON ONLY (not the app's curve): the same overlap with straight-line gains (outgoing 1-p, incoming p), mixed from the decoded tracks.
    private static float[] LinearMix(float[] m, float[] a, float[] b, double t0, double startB, double overlapAt, double n)
    {
        var alt = (float[])m.Clone();
        var first = (long)Math.Round(overlapAt * Rate);
        var frames = (long)Math.Round(n * Rate);
        for (long f = 0; f < frames; f++)
        {
            var p = (float)((f + 0.5) / frames);
            var ia = (((long)Math.Round(t0 * Rate)) + first + f) * 2;
            var ib = (((long)Math.Round(startB * Rate)) + f) * 2;
            for (var c = 0; c < 2; c++)
            {
                var va = ia + c < a.Length ? a[ia + c] : 0f;
                var vb = ib + c < b.Length ? b[ib + c] : 0f;
                alt[((first + f) * 2) + c] = (va * (1 - p)) + (vb * p);
            }
        }

        return alt;
    }

    // How many seconds the reference lags (+) or leads (-) the position that was expected for the 0.4 s of the capture that starts at captureSeconds.
    private static double FindShift(float[] reference, float[] capture, double captureSeconds, double expectedReferenceSeconds)
    {
        const int Decimate = 8;
        var length = (int)(0.4 * Rate) / Decimate;
        var from = (int)(captureSeconds * Rate);
        var probe = new double[length];
        for (var i = 0; i < length; i++)
        {
            probe[i] = capture[(from + (i * Decimate)) * 2];
        }

        var center = (int)(expectedReferenceSeconds * Rate);
        var best = double.MinValue;
        var bestOffset = 0;
        for (var offset = -(int)(1.6 * Rate); offset <= (int)(1.6 * Rate); offset += Decimate)
        {
            var score = Correlate(reference, center + offset, probe, Decimate);
            if (score > best)
            {
                best = score;
                bestOffset = offset;
            }
        }

        for (var offset = bestOffset - Decimate; offset <= bestOffset + Decimate; offset++)
        {
            var score = Correlate(reference, center + offset, probe, Decimate);
            if (score > best)
            {
                best = score;
                bestOffset = offset;
            }
        }

        return bestOffset / (double)Rate;
    }

    private static double Correlate(float[] reference, int startFrame, double[] probe, int step)
    {
        if (startFrame < 0 || (startFrame + (probe.Length * step)) * 2 >= reference.Length)
        {
            return double.MinValue;
        }

        double dot = 0;
        double energy = 1e-12;
        for (var i = 0; i < probe.Length; i++)
        {
            var v = reference[(startFrame + (i * step)) * 2];
            dot += v * probe[i];
            energy += (double)v * v;
        }

        return dot / Math.Sqrt(energy);
    }

    private static float[] Slice(float[] x, double startSeconds)
    {
        var from = Math.Min(x.Length, Math.Max(0, (int)Math.Round(startSeconds * Rate) * 2));
        var count = Math.Min((int)Math.Round(Window * Rate) * 2, Math.Max(0, x.Length - from));
        return x.AsSpan(from, count).ToArray();
    }

    private static double Dot(float[] x, float[] y)
    {
        double sum = 0;
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            sum += (double)x[i] * y[i];
        }

        return sum;
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

    private static string Db(double v) => (20 * Math.Log10(Math.Max(v, 1e-6))).ToString("F1", CultureInfo.InvariantCulture);

    private static float[] Decode(NAudioPlayerBackend backend, string path)
    {
        using var track = backend.OpenTrack(path, Rate, 2);
        var all = new List<float>();
        var buffer = new float[Rate * 2];
        int read;
        while ((read = track.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                all.Add(buffer[i]);
            }
        }

        return all.ToArray();
    }

    private static void WriteWav(string path, float[] stereo)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        var dataBytes = stereo.Length * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(Rate);
        writer.Write(Rate * 4);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        foreach (var sample in stereo)
        {
            writer.Write((short)Math.Round(Math.Clamp(sample, -1f, 1f) * 32767));
        }
    }
}
