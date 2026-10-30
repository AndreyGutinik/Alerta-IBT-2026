using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using AlertaIBT.Core.Loudness;

namespace AlertaIBT.Core.Tests.Loudness;

/// <summary>
/// Local diagnostics on the owner real FLAC folder. They skip cleanly when the folder (or ffmpeg) is absent. The source folder is only ever READ: the tag
/// writer runs on copies in a temp folder that is deleted afterwards, and nothing is copied into the repository.
/// </summary>
public sealed class RealFlacDiagnostics
{
    private const string Source = @"C:\Users\gutni\Music\Mornye\Igreja";

    private static List<string> RealFlacs()
    {
        Assert.SkipUnless(Directory.Exists(Source), "pasta de musicas do dono ausente");
        FlacFixtures.RequireFfmpeg();
        var files = Directory.EnumerateFiles(Source, "*.flac", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.SkipWhen(files.Count < 5, "menos de 5 FLAC");
        return files;
    }

    [Fact]
    public void Five_real_FLAC_copies_keep_frames_md5_pictures_and_tags_and_every_failure_leaves_them_untouched()
    {
        var files = RealFlacs();
        var temp = Path.Combine(Path.GetTempPath(), "ibt-real-flac-" + Guid.NewGuid().ToString("N"));
        var pristineDir = Path.Combine(temp, "pristine");
        Directory.CreateDirectory(pristineDir);
        var report = new StringBuilder();
        try
        {
            var chosen = Enumerable.Range(0, 5).Select(i => files[i * files.Count / 5]).ToList();
            var writer = new FlacLoudnessTagWriter();
            foreach (var original in chosen)
            {
                var name = Path.GetFileName(original);
                var copy = Path.Combine(temp, name);
                var pristine = Path.Combine(pristineDir, name);
                File.Copy(original, copy); // the source is only read
                File.Copy(original, pristine);
                var snapshot = FlacFixtures.Snapshot.Of(pristine);
                var pictures = snapshot.Blocks.Count(b => b.Type == FlacBlock.Picture);
                var decodedBefore = FlacFixtures.DecodedMd5(copy);
                var created = File.GetCreationTimeUtc(copy);
                var modified = File.GetLastWriteTimeUtc(copy);

                // Failure injection on this very file: nothing changes.
                var fingerprint = FlacFixtures.FileFingerprint(copy);
                var broken = new FlacLoudnessTagWriter(hooks: new FlacLoudnessTagWriter.WriterHooks { AfterTempWritten = t => File.AppendAllText(t, "x") });
                Assert.Equal(TagWriteOutcome.Failed, broken.Write(copy, new LoudnessTagValues(-17.3, 0.8), CancellationToken.None).Outcome);
                Assert.Equal(fingerprint, FlacFixtures.FileFingerprint(copy));
                var thrower = new FlacLoudnessTagWriter(hooks: new FlacLoudnessTagWriter.WriterHooks { BeforeReplace = _ => throw new IOException("injetado") });
                Assert.Equal(TagWriteOutcome.Failed, thrower.Write(copy, new LoudnessTagValues(-17.3, 0.8), CancellationToken.None).Outcome);
                Assert.Equal(fingerprint, FlacFixtures.FileFingerprint(copy));
                using (new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Assert.Equal(TagWriteOutcome.Busy, writer.Write(copy, new LoudnessTagValues(-17.3, 0.8), CancellationToken.None).Outcome);
                }

                Assert.Equal(fingerprint, FlacFixtures.FileFingerprint(copy));
                Assert.Empty(Directory.EnumerateFiles(temp, "*.ibt-*"));

                // The real write.
                var result = writer.Write(copy, new LoudnessTagValues(-17.3, 0.8), CancellationToken.None);
                Assert.True(result.Outcome == TagWriteOutcome.Written, name + ": " + result.Reason);
                var after = FlacFixtures.Snapshot.Of(copy);
                Assert.Equal(snapshot.FramesHash, after.FramesHash);
                Assert.Equal(snapshot.StreamInfoMd5, after.StreamInfoMd5);
                Assert.Equal(snapshot.Blocks.Where(b => b.Type is not (1 or 4)).ToList(), after.Blocks.Where(b => b.Type is not (1 or 4)).ToList());
                var (vendorBefore, entriesBefore) = FlacFixtures.Comments(pristine);
                var (vendorAfter, entriesAfter) = FlacFixtures.Comments(copy);
                Assert.Equal(vendorBefore, vendorAfter);
                var kept = entriesBefore.Where(e => !FlacTagWriterTests.Owned.Contains(FlacTagWriterTests.NameOf(e))).ToList();
                Assert.Equal(kept, entriesAfter.Take(kept.Count).ToList());
                Assert.Equal(decodedBefore, FlacFixtures.DecodedMd5(copy));
                Assert.Equal(created, File.GetCreationTimeUtc(copy));
                Assert.Equal(modified, File.GetLastWriteTimeUtc(copy));
                // A file whose encoder stored no audio MD5 (all zeros) cannot vouch for its tags: they are never trusted back, which is the safe side.
                var hasMd5 = snapshot.StreamInfoMd5.Any(c => c != '0');
                Assert.Equal(hasMd5, FlacLoudnessTagWriter.ReadMarked(copy) is not null);
                Assert.Empty(Directory.EnumerateFiles(temp, "*.ibt-*"));
                report.AppendLine(CultureInfo.InvariantCulture, $"{name}: {snapshot.BitsPerSample}-bit, {snapshot.Length / 1024} KiB, {pictures} cover block(s), {entriesBefore.Count} old tags, size {snapshot.Length} -> {after.Length}, frames identical, MD5 {snapshot.StreamInfoMd5}, decoded MD5 identical, STREAMINFO MD5 {(hasMd5 ? "present" : "all zeros")}");
            }

            WriteReport("tagwriter-real-copies.txt", report.ToString());
        }
        finally
        {
            Directory.Delete(temp, recursive: true); // only the temp copies
        }

        Assert.True(File.Exists(files[0])); // the owner folder is intact
    }

    [Fact]
    public void Headroom_statistics_over_the_real_folder_are_written_when_asked()
    {
        var path = Environment.GetEnvironmentVariable("ALERTAIBT_LOUDNESS_STATS");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(path), "defina ALERTAIBT_LOUDNESS_STATS para gerar a estatistica");
        var files = RealFlacs();
        var lines = new List<string> { "file;bits;rate;channels;seconds;lufs;samplePeakDb;truePeakDb" };
        var rows = new List<(double Lufs, double TruePeakDb)>();
        foreach (var file in files)
        {
            FlacLayout layout;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                layout = FlacLayout.Read(stream, t => t == FlacBlock.StreamInfo);
            }

            var info = layout.Blocks[0].Data!;
            var rate = (info[10] << 12) | (info[11] << 4) | (info[12] >> 4);
            var channels = ((info[12] >> 1) & 7) + 1;
            var bits = (((info[12] & 1) << 4) | (info[13] >> 4)) + 1;
            var start = new ProcessStartInfo(FindFfmpeg(), $"-v error -i \"{file}\" -map 0:a -f f32le -acodec pcm_f32le -ar {rate} -ac {channels} -")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(start)!;
            var meter = new LoudnessMeter(rate, channels);
            var buffer = new byte[rate * channels * 4];
            var carry = 0;
            while (true)
            {
                var read = process.StandardOutput.BaseStream.Read(buffer, carry, buffer.Length - carry);
                if (read <= 0)
                {
                    break;
                }

                var total = carry + read;
                var usable = total - (total % (4 * channels));
                meter.Add(MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, usable)));
                carry = total - usable;
                Array.Copy(buffer, usable, buffer, 0, carry);
            }

            process.WaitForExit();
            var result = meter.Finish();
            if (result.IntegratedLufs is not { } lufs)
            {
                lines.Add(string.Join(';', Path.GetFileName(file), bits, rate, channels, result.Seconds.ToString("F1", CultureInfo.InvariantCulture), "silent", "", ""));
                continue;
            }

            var truePeakDb = 20 * Math.Log10(result.TruePeak);
            rows.Add((lufs, truePeakDb));
            lines.Add(string.Join(';', Path.GetFileName(file), bits, rate, channels, result.Seconds.ToString("F1", CultureInfo.InvariantCulture), lufs.ToString("F2", CultureInfo.InvariantCulture), (20 * Math.Log10(result.SamplePeak)).ToString("F2", CultureInfo.InvariantCulture), truePeakDb.ToString("F2", CultureInfo.InvariantCulture)));
        }

        lines.Add(string.Empty);
        lines.Add($"measured {rows.Count} of {files.Count} files");
        if (rows.Count > 0)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"LUFS min {rows.Min(r => r.Lufs):F1}, median {rows.OrderBy(r => r.Lufs).ElementAt(rows.Count / 2).Lufs:F1}, max {rows.Max(r => r.Lufs):F1}"));
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"true peak max {rows.Max(r => r.TruePeakDb):F2} dBFS, above -1 dBFS: {rows.Count(r => r.TruePeakDb > -1)}"));
        }

        foreach (var target in new[] { -24.0, -23.0, -20.0, -18.0, -16.0, -14.0, -12.0 })
        {
            var gains = rows.Select(r => NormalizationGain.Compute(new TrackLoudness(r.Lufs, Math.Pow(10, r.TruePeakDb / 20)), target, true)).ToList();
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"target {target:F0} LUFS: raised {gains.Count(g => g.GainDb > 0.05)}, lowered {gains.Count(g => g.GainDb < -0.05)}, capped by the peak {gains.Count(g => g.Capped)}, max boost {gains.Max(g => g.GainDb):F1} dB, max cut {gains.Min(g => g.GainDb):F1} dB"));
        }

        File.WriteAllLines(path!, lines);
    }

    private static string FindFfmpeg()
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        return File.Exists(local) ? local : "ffmpeg";
    }

    private static void WriteReport(string name, string text)
    {
        var dir = Environment.GetEnvironmentVariable("ALERTAIBT_DIAG_OUT");
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name), text);
        }
    }
}
