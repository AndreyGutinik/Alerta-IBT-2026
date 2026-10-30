using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AlertaIBT.Core.Loudness;

/// <summary>What the tags of one track say.</summary>
/// <param name="IntegratedLufs">The integrated loudness measured by this app.</param>
/// <param name="TruePeak">The true peak, linear.</param>
public sealed record LoudnessTagValues(double IntegratedLufs, double TruePeak);

/// <summary>How a tag write ended.</summary>
public enum TagWriteOutcome
{
    /// <summary>The tags are in the file (verified).</summary>
    Written,

    /// <summary>The file is open (it is playing, or another program holds it): nothing was done; try again later.</summary>
    Busy,

    /// <summary>The file is missing, read-only or not a FLAC: nothing was done and trying again will not help.</summary>
    NotWritable,

    /// <summary>Something went wrong or the check failed: the original is untouched; do not try this file again.</summary>
    Failed,

    /// <summary>The app is closing: nothing was changed.</summary>
    Cancelled,
}

/// <summary>The result of <see cref="ILoudnessTagWriter.Write"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Reason">Why, when it was not written (for the log; Portuguese for the screen is not needed).</param>
/// <param name="NewSize">The size of the file after a write.</param>
/// <param name="NewWriteTicks">The last-write time (UTC ticks) of the file after a write.</param>
public sealed record TagWriteResult(TagWriteOutcome Outcome, string? Reason = null, long NewSize = 0, long NewWriteTicks = 0);

/// <summary>Writes the loudness tags into a music file (a fake in tests).</summary>
public interface ILoudnessTagWriter
{
    /// <summary>Whether this writer handles the file (by extension).</summary>
    bool Supports(string path);

    /// <summary>Writes the tags of <paramref name="values"/> into <paramref name="path"/>, with every safety check; never throws.</summary>
    TagWriteResult Write(string path, LoudnessTagValues values, CancellationToken cancellation);
}

/// <summary>
/// Writes the ReplayGain 2.0 tags and this app's marker into a FLAC file, and nothing else. The file is never edited in place: a copy is written in
/// the same folder, that copy is checked against the original, and only then does it take the original's place (<see cref="File.Replace(string,string,string?)"/>
/// keeps a backup until the swap worked). The check is: the audio frames are byte for byte the same (and so is STREAMINFO with its MD5), every other metadata block
/// (pictures included) is identical, every old tag is still there with the same bytes, the new tags read back through two independent readers.
/// Any doubt leaves the original untouched.
/// </summary>
public sealed class FlacLoudnessTagWriter : ILoudnessTagWriter
{
    /// <summary>ReplayGain 2.0 reference loudness.</summary>
    public const double ReplayGainReferenceLufs = -18.0;

    /// <summary>The field with the version of this app's analysis (its presence says the other loudness tags are ours).</summary>
    public const string VersionField = "IBT_LOUDNESS_VERSION";

    /// <summary>The field with the audio MD5 the analysis was made on (so tags on a file that changed are not trusted).</summary>
    public const string Md5Field = "IBT_LOUDNESS_AUDIO_MD5";

    /// <summary>The extension of the copy written next to the music file.</summary>
    public const string TempSuffix = ".ibt-tmp";

    /// <summary>The extension of the backup kept while the swap happens.</summary>
    public const string BackupSuffix = ".ibt-bak";

    private static readonly string[] OwnedFields =
    [
        "REPLAYGAIN_TRACK_GAIN", "REPLAYGAIN_TRACK_PEAK", "REPLAYGAIN_REFERENCE_LOUDNESS", VersionField, Md5Field,
    ];

    private readonly int _analysisVersion;
    private readonly WriterHooks? _hooks;

    /// <param name="analysisVersion">The version written in the marker.</param>
    /// <param name="hooks">Test hooks (failure injection); <c>null</c> in the app.</param>
    public FlacLoudnessTagWriter(int analysisVersion = LoudnessCache.AnalysisVersion, WriterHooks? hooks = null)
    {
        _analysisVersion = analysisVersion;
        _hooks = hooks;
    }

    /// <summary>Points where a test can break the write on purpose.</summary>
    public sealed class WriterHooks
    {
        /// <summary>Called with the copy's path once it is written and before it is checked (a test may corrupt it, or throw).</summary>
        public Action<string>? AfterTempWritten { get; init; }

        /// <summary>Called just before the copy takes the original's place (a test may throw).</summary>
        public Action<string>? BeforeReplace { get; init; }
    }

    /// <inheritdoc />
    public bool Supports(string path) => string.Equals(Path.GetExtension(path), ".flac", StringComparison.OrdinalIgnoreCase);

    /// <summary>The text of REPLAYGAIN_TRACK_GAIN for a loudness (reference -18 LUFS), e.g. <c>-3.21 dB</c>.</summary>
    public static string FormatGain(double lufs)
    {
        var gain = ReplayGainReferenceLufs - lufs;
        return gain.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + " dB";
    }

    /// <summary>The text of REPLAYGAIN_TRACK_PEAK: linear, 6 decimals.</summary>
    public static string FormatPeak(double peak) => peak.ToString("0.000000", CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads the loudness a file's tags carry, but only when they are this app's own (the version marker is the current one and the audio MD5 in the
    /// marker is the one in STREAMINFO). Any other ReplayGain tag is ignored. <c>null</c> when absent, foreign, stale or unreadable.
    /// </summary>
    public static LoudnessTagValues? ReadMarked(string path, int analysisVersion = LoudnessCache.AnalysisVersion)
    {
        try
        {
            if (!string.Equals(Path.GetExtension(path), ".flac", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var layout = FlacLayout.Read(stream, type => type is FlacBlock.StreamInfo or FlacBlock.VorbisComment);
            var block = layout.Blocks.FirstOrDefault(b => b.Type == FlacBlock.VorbisComment);
            if (block?.Data is null)
            {
                return null;
            }

            var comments = VorbisComments.Parse(block.Data);
            var version = comments.Get(VersionField);
            var md5 = comments.Get(Md5Field);
            var streamMd5 = layout.StreamInfoMd5;
            if (version != analysisVersion.ToString(CultureInfo.InvariantCulture)
                || streamMd5 is null || md5 is null || !string.Equals(md5, streamMd5, StringComparison.OrdinalIgnoreCase)
                || streamMd5.All(c => c == '0'))
            {
                return null;
            }

            var gainText = comments.Get("REPLAYGAIN_TRACK_GAIN");
            var peakText = comments.Get("REPLAYGAIN_TRACK_PEAK");
            if (gainText is null || peakText is null)
            {
                return null;
            }

            var gain = double.Parse(gainText.Replace("dB", string.Empty, StringComparison.OrdinalIgnoreCase).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
            var peak = double.Parse(peakText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
            if (!double.IsFinite(gain) || !double.IsFinite(peak) || peak <= 0)
            {
                return null;
            }

            return new LoudnessTagValues(ReplayGainReferenceLufs - gain, peak);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or OverflowException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public TagWriteResult Write(string path, LoudnessTagValues values, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(values);

        var temp = path + TempSuffix;
        var backup = path + BackupSuffix;
        var replaced = false;
        try
        {
            if (!Supports(path))
            {
                return new TagWriteResult(TagWriteOutcome.NotWritable, "nao e FLAC");
            }

            RecoverLeftovers(path, temp, backup);
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new TagWriteResult(TagWriteOutcome.NotWritable, "arquivo ausente");
            }

            if (info.IsReadOnly)
            {
                return new TagWriteResult(TagWriteOutcome.NotWritable, "arquivo somente leitura");
            }

            if (cancellation.IsCancellationRequested)
            {
                return new TagWriteResult(TagWriteOutcome.Cancelled);
            }

            // In use? An exclusive open fails while the player (or any program) holds it. It is opened for read only and closed at once.
            try
            {
                using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            catch (IOException)
            {
                return new TagWriteResult(TagWriteOutcome.Busy, "arquivo em uso");
            }
            catch (UnauthorizedAccessException)
            {
                return new TagWriteResult(TagWriteOutcome.NotWritable, "sem permissao");
            }

            var creation = info.CreationTimeUtc;
            var lastWrite = info.LastWriteTimeUtc;
            var attributes = info.Attributes;

            FlacLayout layout;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                layout = FlacLayout.Read(stream);
            }

            var framesHash = HashRegion(path, layout.FramesOffset, layout.FileLength - layout.FramesOffset);
            var blocks = BuildBlocks(layout, values, layout.StreamInfoMd5 ?? string.Empty);
            WriteTemp(path, temp, layout, blocks, cancellation);
            _hooks?.AfterTempWritten?.Invoke(temp);

            var problem = Verify(path, temp, layout, values, framesHash);
            if (problem is not null)
            {
                return Fail(temp, "verificacao falhou: " + problem);
            }

            cancellation.ThrowIfCancellationRequested();
            File.SetCreationTimeUtc(temp, creation);
            File.SetLastWriteTimeUtc(temp, lastWrite);
            File.SetAttributes(temp, attributes);
            _hooks?.BeforeReplace?.Invoke(temp);
            cancellation.ThrowIfCancellationRequested();

            File.Replace(temp, path, backup, ignoreMetadataErrors: true);
            replaced = true;

            // Look at the result once more; a bad swap is undone from the backup.
            var after = new FileInfo(path);
            var framesStart = LayoutOffset(path);
            if (after.Length - framesStart != layout.FileLength - layout.FramesOffset
                || HashRegion(path, framesStart, after.Length - framesStart) != framesHash)
            {
                Restore(backup, path);
                return new TagWriteResult(TagWriteOutcome.Failed, "audio final diferente do verificado; restaurado");
            }

            TryDelete(backup);
            return new TagWriteResult(TagWriteOutcome.Written, null, after.Length, after.LastWriteTimeUtc.Ticks);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temp);
            return new TagWriteResult(TagWriteOutcome.Cancelled);
        }
        catch (IOException ex) when (!replaced && IsSharing(ex))
        {
            TryDelete(temp);
            return new TagWriteResult(TagWriteOutcome.Busy, "arquivo em uso: " + ex.Message);
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            if (replaced)
            {
                Restore(backup, path);
            }

            return new TagWriteResult(TagWriteOutcome.Failed, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static bool IsSharing(IOException ex)
    {
        // 32 and 33: ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION.
        var code = ex.HResult & 0xFFFF;
        return code is 32 or 33;
    }

    private static long LayoutOffset(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return FlacLayout.Read(stream, _ => false).FramesOffset;
    }

    private static TagWriteResult Fail(string temp, string reason)
    {
        TryDelete(temp);
        return new TagWriteResult(TagWriteOutcome.Failed, reason);
    }

    // A crash between the steps of an earlier write can leave a copy or a backup: the copy is always junk; a backup only matters when the original is gone.
    private static void RecoverLeftovers(string path, string temp, string backup)
    {
        TryDelete(temp);
        if (File.Exists(backup))
        {
            if (!File.Exists(path))
            {
                File.Move(backup, path);
            }
            else
            {
                TryDelete(backup);
            }
        }
    }

    private static void Restore(string backup, string path)
    {
        try
        {
            if (File.Exists(backup))
            {
                File.Move(backup, path, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing more can be done here; the backup file stays next to the music for the owner to find.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp file is harmless and is removed on the next try.
        }
    }

    // The new list of blocks: every block as it was, except the Vorbis comment (the old entries kept as raw bytes, ours replaced) and the padding (it absorbs the change in size).
    private List<FlacBlock> BuildBlocks(FlacLayout layout, LoudnessTagValues values, string audioMd5)
    {
        var oldIndex = layout.Blocks.ToList().FindIndex(b => b.Type == FlacBlock.VorbisComment);
        if (layout.Blocks.Count(b => b.Type == FlacBlock.VorbisComment) > 1)
        {
            throw new InvalidDataException("mais de um bloco de comentarios");
        }

        VorbisComments comments;
        if (oldIndex >= 0)
        {
            var parsed = VorbisComments.Parse(layout.Blocks[oldIndex].Data!);
            comments = new VorbisComments
            {
                Vendor = parsed.Vendor,
                Entries = parsed.Entries.Where(e => !OwnedFields.Contains(VorbisComments.NameOf(e))).ToList(),
            };
        }
        else
        {
            comments = new VorbisComments { Vendor = Encoding.UTF8.GetBytes("IBT Controller"), Entries = [] };
        }

        comments.Entries.Add(Encoding.UTF8.GetBytes("REPLAYGAIN_TRACK_GAIN=" + FormatGain(values.IntegratedLufs)));
        comments.Entries.Add(Encoding.UTF8.GetBytes("REPLAYGAIN_TRACK_PEAK=" + FormatPeak(values.TruePeak)));
        comments.Entries.Add(Encoding.UTF8.GetBytes("REPLAYGAIN_REFERENCE_LOUDNESS=" + ReplayGainReferenceLufs.ToString("0.00", CultureInfo.InvariantCulture) + " LUFS"));
        comments.Entries.Add(Encoding.UTF8.GetBytes(VersionField + "=" + _analysisVersion.ToString(CultureInfo.InvariantCulture)));
        comments.Entries.Add(Encoding.UTF8.GetBytes(Md5Field + "=" + audioMd5));
        var payload = comments.Serialize();
        if (payload.Length >= 1 << 24)
        {
            throw new InvalidDataException("comentarios grandes demais");
        }

        var oldLength = oldIndex >= 0 ? layout.Blocks[oldIndex].Length : -4;
        var delta = payload.Length - oldLength; // header bytes included when the block is new (-4 stands for the missing header)

        var blocks = new List<FlacBlock>();
        var placed = false;
        var padded = false;
        foreach (var block in layout.Blocks)
        {
            if (block.Type == FlacBlock.VorbisComment)
            {
                blocks.Add(new FlacBlock(FlacBlock.VorbisComment, payload, payload.Length));
                placed = true;
            }
            else if (block.Type == FlacBlock.Padding && !padded)
            {
                // The padding takes up the difference so the audio does not move when it can be avoided.
                padded = true;
                var length = Math.Max(0, block.Length - delta);
                blocks.Add(new FlacBlock(FlacBlock.Padding, new byte[length], length));
            }
            else
            {
                blocks.Add(block);
            }
        }

        if (!placed)
        {
            // No comment block before: it goes right after STREAMINFO.
            blocks.Insert(1, new FlacBlock(FlacBlock.VorbisComment, payload, payload.Length));
        }

        return blocks;
    }

    private static void WriteTemp(string path, string temp, FlacLayout layout, List<FlacBlock> blocks, CancellationToken cancellation)
    {
        using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(layout.Prefix);
        output.Write("fLaC"u8);
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var header = new byte[4];
            header[0] = (byte)(block.Type | (i == blocks.Count - 1 ? 0x80 : 0));
            header[1] = (byte)(block.Length >> 16);
            header[2] = (byte)(block.Length >> 8);
            header[3] = (byte)block.Length;
            output.Write(header);
            output.Write(block.Data!, 0, block.Length);
        }

        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        input.Seek(layout.FramesOffset, SeekOrigin.Begin);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
        }

        output.Flush(true);
    }

    private static string HashRegion(string path, long offset, long length)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Seek(offset, SeekOrigin.Begin);
        using var sha = SHA256.Create();
        var buffer = new byte[1 << 20];
        var remaining = length;
        while (remaining > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0)
            {
                throw new InvalidDataException("arquivo cortado ao calcular o resumo");
            }

            sha.TransformBlock(buffer, 0, read, null, 0);
            remaining -= read;
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    // Compares the copy with the original; returns what is wrong, or null when everything holds.
    private string? Verify(string original, string temp, FlacLayout before, LoudnessTagValues values, string framesHash)
    {
        FlacLayout after;
        using (var stream = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            after = FlacLayout.Read(stream);
        }

        // (a) the audio frames and STREAMINFO.
        var framesLength = before.FileLength - before.FramesOffset;
        if (after.FileLength - after.FramesOffset != framesLength)
        {
            return "tamanho do audio mudou";
        }

        if (HashRegion(temp, after.FramesOffset, framesLength) != framesHash)
        {
            return "os quadros de audio nao sao identicos";
        }

        if (!before.Blocks[0].Data!.AsSpan().SequenceEqual(after.Blocks[0].Data!) || before.StreamInfoMd5 != after.StreamInfoMd5)
        {
            return "STREAMINFO mudou";
        }

        if (!before.Prefix.AsSpan().SequenceEqual(after.Prefix))
        {
            return "o ID3 inicial mudou";
        }

        // (b) every other block byte for byte, the comments entry by entry.
        var oldOthers = before.Blocks.Where(b => b.Type is not (FlacBlock.VorbisComment or FlacBlock.Padding)).ToList();
        var newOthers = after.Blocks.Where(b => b.Type is not (FlacBlock.VorbisComment or FlacBlock.Padding)).ToList();
        if (oldOthers.Count != newOthers.Count)
        {
            return "blocos de metadados a mais ou a menos";
        }

        for (var i = 0; i < oldOthers.Count; i++)
        {
            if (oldOthers[i].Type != newOthers[i].Type || !oldOthers[i].Data.AsSpan().SequenceEqual(newOthers[i].Data))
            {
                return $"bloco {oldOthers[i].Type} mudou";
            }
        }

        var oldComment = before.Blocks.FirstOrDefault(b => b.Type == FlacBlock.VorbisComment);
        var newComment = after.Blocks.First(b => b.Type == FlacBlock.VorbisComment);
        var newParsed = VorbisComments.Parse(newComment.Data!);
        if (oldComment is not null)
        {
            var oldParsed = VorbisComments.Parse(oldComment.Data!);
            if (!oldParsed.Vendor.AsSpan().SequenceEqual(newParsed.Vendor))
            {
                return "o fabricante da etiqueta mudou";
            }

            var keep = oldParsed.Entries.Where(e => !OwnedFields.Contains(VorbisComments.NameOf(e))).ToList();
            var now = newParsed.Entries.Take(keep.Count).ToList();
            if (keep.Count != now.Count || keep.Where((e, i) => !e.AsSpan().SequenceEqual(now[i])).Any())
            {
                return "uma etiqueta antiga mudou";
            }

            if (newParsed.Entries.Count != keep.Count + OwnedFields.Length)
            {
                return "numero de etiquetas inesperado";
            }
        }

        // (c) the new tags read back, by this parser and by TagLib.
        var expected = new Dictionary<string, string>
        {
            ["REPLAYGAIN_TRACK_GAIN"] = FormatGain(values.IntegratedLufs),
            ["REPLAYGAIN_TRACK_PEAK"] = FormatPeak(values.TruePeak),
            ["REPLAYGAIN_REFERENCE_LOUDNESS"] = ReplayGainReferenceLufs.ToString("0.00", CultureInfo.InvariantCulture) + " LUFS",
            [VersionField] = _analysisVersion.ToString(CultureInfo.InvariantCulture),
        };
        foreach (var (name, text) in expected)
        {
            if (newParsed.Get(name) != text)
            {
                return "etiqueta nova nao confere: " + name;
            }
        }

        return CompareWithTagLib(original, temp, expected);
    }

    // The mime type is given because the copy's extension is not .flac.
    private static TagLib.File OpenWithTagLib(string path) =>
        TagLib.File.Create(new TagLib.File.LocalFileAbstraction(path), "taglib/flac", TagLib.ReadStyle.Average);

    // The independent reader: the usual tags, the pictures and the new fields as TagLib sees them.
    private static string? CompareWithTagLib(string original, string temp, Dictionary<string, string> expected)
    {
        using var a = OpenWithTagLib(original);
        using var b = OpenWithTagLib(temp);
        var ta = a.Tag;
        var tb = b.Tag;
        if (ta.Title != tb.Title || ta.Album != tb.Album || ta.Year != tb.Year || ta.Track != tb.Track || ta.Disc != tb.Disc
            || ta.Comment != tb.Comment || ta.Lyrics != tb.Lyrics
            || !ta.Performers.SequenceEqual(tb.Performers) || !ta.AlbumArtists.SequenceEqual(tb.AlbumArtists)
            || !ta.Genres.SequenceEqual(tb.Genres) || !ta.Composers.SequenceEqual(tb.Composers))
        {
            return "TagLib leu etiquetas diferentes";
        }

        var picturesA = ta.Pictures;
        var picturesB = tb.Pictures;
        if (picturesA.Length != picturesB.Length)
        {
            return "numero de capas mudou";
        }

        for (var i = 0; i < picturesA.Length; i++)
        {
            if (picturesA[i].Type != picturesB[i].Type
                || picturesA[i].MimeType != picturesB[i].MimeType
                || !picturesA[i].Data.Data.SequenceEqual(picturesB[i].Data.Data))
            {
                return "uma capa mudou";
            }
        }

        if (b.GetTag(TagLib.TagTypes.Xiph, false) is not TagLib.Ogg.XiphComment xiph)
        {
            return "TagLib nao achou os comentarios";
        }

        foreach (var (name, text) in expected)
        {
            var fields = xiph.GetField(name);
            if (fields.Length != 1 || fields[0] != text)
            {
                return "TagLib nao leu " + name;
            }
        }

        // Every old field TagLib knows is still there, by value.
        if (a.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment oldXiph)
        {
            foreach (var field in oldXiph)
            {
                if (OwnedFields.Contains(field.ToUpperInvariant()))
                {
                    continue;
                }

                if (!oldXiph.GetField(field).SequenceEqual(xiph.GetField(field)))
                {
                    return "campo antigo mudou: " + field;
                }
            }
        }

        return null;
    }
}
