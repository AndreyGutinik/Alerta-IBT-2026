using System.Diagnostics;
using System.Security.Cryptography;
using AlertaIBT.Core.Loudness;

namespace AlertaIBT.Core.Tests.Loudness;

/// <summary>FLAC files made with ffmpeg (once per run), copied for each test; the tests skip when ffmpeg is not installed.</summary>
internal static class FlacFixtures
{
    private static readonly Lazy<string?> FfmpegPath = new(FindFfmpeg);
    private static readonly Lazy<string> Root = new(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "ibt-flac-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp folder is harmless.
            }
        };
        return dir;
    });

    private static readonly Dictionary<string, string> Made = [];
    private static readonly object Gate = new();

    public static bool Available => FfmpegPath.Value is not null;

    public static void RequireFfmpeg() => Assert.SkipWhen(!Available, "ffmpeg nao encontrado");

    private static string? FindFfmpeg()
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        if (File.Exists(local))
        {
            return local;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A bad entry in PATH.
            }
        }

        return null;
    }

    public static string RunFfmpeg(params string[] args)
    {
        var info = new ProcessStartInfo(FfmpegPath.Value!) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("-v");
        info.ArgumentList.Add("error");
        info.ArgumentList.Add("-y");
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || error.Length > 0)
        {
            throw new InvalidOperationException("ffmpeg: " + error);
        }

        return output;
    }

    /// <summary>The MD5 of the decoded audio (all samples) as ffmpeg computes it: equal before and after a tag write when the audio is untouched.</summary>
    public static string DecodedMd5(string flac)
    {
        var output = RunFfmpeg("-i", flac, "-map", "0:a", "-f", "md5", "-");
        return output.Trim();
    }

    /// <summary>Copies a fixture to <paramref name="directory"/> under <paramref name="name"/>.</summary>
    public static string Copy(string kind, string directory, string name)
    {
        var source = Make(kind);
        var target = Path.Combine(directory, name);
        File.Copy(source, target);
        return target;
    }

    private static string Make(string kind)
    {
        lock (Gate)
        {
            if (Made.TryGetValue(kind, out var path))
            {
                return path;
            }

            var dir = Root.Value;
            var file = Path.Combine(dir, kind + ".flac");
            var cover = Path.Combine(dir, "cover.png");
            if (!File.Exists(cover))
            {
                RunFfmpeg("-f", "lavfi", "-i", "color=c=red:s=96x96", "-frames:v", "1", cover);
            }

            string[] tags = ["-metadata", "title=Amazing Grace", "-metadata", "artist=Josh Snodgrass", "-metadata", "album=Culto de Domingo", "-metadata", "TRACKNUMBER=3", "-metadata", "COMMENT=ao vivo", "-metadata", "GENRE=Gospel"];
            switch (kind)
            {
                case "plain16":
                    RunFfmpeg("-f", "lavfi", "-i", "sine=frequency=440:duration=3:sample_rate=44100", "-ac", "2", "-sample_fmt", "s16", file);
                    break;
                case "tags24":
                    RunFfmpeg(["-f", "lavfi", "-i", "sine=frequency=330:duration=3:sample_rate=48000", "-ac", "2", "-sample_fmt", "s32", "-bits_per_raw_sample", "24", .. tags, file]);
                    break;
                case "foreign-gain":
                    RunFfmpeg(["-f", "lavfi", "-i", "sine=frequency=330:duration=3:sample_rate=48000", "-ac", "2", "-sample_fmt", "s16", .. tags,
                        "-metadata", "REPLAYGAIN_TRACK_GAIN=-1.00 dB", "-metadata", "REPLAYGAIN_TRACK_PEAK=0.5", "-metadata", "REPLAYGAIN_ALBUM_GAIN=-2.00 dB", file]);
                    break;
                case "cover24":
                    RunFfmpeg(["-f", "lavfi", "-i", "sine=frequency=550:duration=3:sample_rate=48000", "-i", cover, "-map", "0:a", "-map", "1:v", "-ac", "2",
                        "-sample_fmt", "s32", "-bits_per_raw_sample", "24", "-c:v", "copy", "-disposition:v", "attached_pic", .. tags, file]);
                    break;
                case "cover16":
                    RunFfmpeg("-f", "lavfi", "-i", "sine=frequency=550:duration=3:sample_rate=44100", "-i", cover, "-map", "0:a", "-map", "1:v", "-ac", "2",
                        "-sample_fmt", "s16", "-c:v", "copy", "-disposition:v", "attached_pic", file);
                    break;
                case "bare":
                    // No Vorbis comment and no padding at all: STREAMINFO and the frames only.
                    Strip(Make("plain16"), file, keepTypes: [FlacBlock.StreamInfo]);
                    break;
                case "id3":
                    // An ID3v2 tag in front of the "fLaC" marker, as some files carry.
                    PrependId3(Make("tags24"), file);
                    break;
                default:
                    throw new ArgumentException(kind);
            }

            Made[kind] = file;
            return file;
        }
    }

    public static readonly string[] Kinds = ["plain16", "tags24", "cover24", "cover16", "bare", "id3", "foreign-gain"];

    // Rewrites a FLAC keeping only some metadata blocks (the frames are copied as they are).
    private static void Strip(string source, string target, int[] keepTypes)
    {
        FlacLayout layout;
        using (var stream = File.OpenRead(source))
        {
            layout = FlacLayout.Read(stream);
        }

        var blocks = layout.Blocks.Where(b => keepTypes.Contains(b.Type)).ToList();
        using var output = File.Create(target);
        output.Write(layout.Prefix);
        output.Write("fLaC"u8);
        for (var i = 0; i < blocks.Count; i++)
        {
            output.WriteByte((byte)(blocks[i].Type | (i == blocks.Count - 1 ? 0x80 : 0)));
            output.WriteByte((byte)(blocks[i].Length >> 16));
            output.WriteByte((byte)(blocks[i].Length >> 8));
            output.WriteByte((byte)blocks[i].Length);
            output.Write(blocks[i].Data!);
        }

        using var input = File.OpenRead(source);
        input.Seek(layout.FramesOffset, SeekOrigin.Begin);
        input.CopyTo(output);
    }

    private static void PrependId3(string source, string target)
    {
        const int payload = 300;
        using var output = File.Create(target);
        output.Write("ID3"u8);
        output.Write([4, 0, 0, 0, 0, (payload >> 7) & 0x7F, payload & 0x7F]);
        output.Write(new byte[payload]);
        output.Write(File.ReadAllBytes(source));
    }

    /// <summary>What a FLAC holds, as numbers to compare before and after a write.</summary>
    public sealed record Snapshot(string FramesHash, string StreamInfoMd5, int BitsPerSample, IReadOnlyList<(int Type, string Hash)> Blocks, string PrefixHash, long Length)
    {
        public static Snapshot Of(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var layout = FlacLayout.Read(stream);
            stream.Seek(layout.FramesOffset, SeekOrigin.Begin);
            var frames = Convert.ToHexString(SHA256.HashData(stream));
            var info = layout.Blocks[0].Data!;
            var bits = ((info[12] & 1) << 4 | (info[13] >> 4)) + 1;
            return new Snapshot(
                frames,
                layout.StreamInfoMd5!,
                bits,
                layout.Blocks.Select(b => (b.Type, Convert.ToHexString(SHA256.HashData(b.Data!)))).ToList(),
                Convert.ToHexString(SHA256.HashData(layout.Prefix)),
                layout.FileLength);
        }
    }

    /// <summary>The raw Vorbis comment entries of a file (as text), vendor first.</summary>
    public static (string Vendor, List<string> Entries) Comments(string path)
    {
        using var stream = File.OpenRead(path);
        var layout = FlacLayout.Read(stream);
        var block = layout.Blocks.FirstOrDefault(b => b.Type == FlacBlock.VorbisComment);
        if (block is null)
        {
            return (string.Empty, []);
        }

        var parsed = VorbisComments.Parse(block.Data!);
        return (System.Text.Encoding.UTF8.GetString(parsed.Vendor), parsed.Entries.Select(e => System.Text.Encoding.UTF8.GetString(e)).ToList());
    }

    /// <summary>Whole-file hash plus times: equal before and after when nothing was touched.</summary>
    public static string FileFingerprint(string path)
    {
        var info = new FileInfo(path);
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks + "|" + info.CreationTimeUtc.Ticks + "|" + info.Attributes;
    }
}
