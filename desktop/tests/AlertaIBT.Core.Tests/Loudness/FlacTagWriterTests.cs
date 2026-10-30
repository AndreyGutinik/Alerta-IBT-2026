using AlertaIBT.Core.Loudness;

namespace AlertaIBT.Core.Tests.Loudness;

/// <summary>
/// The tag writer on FLAC files made with ffmpeg (16 and 24 bit, with and without cover, tags, padding, an ID3v2 prefix): the audio frames, STREAMINFO
/// with its MD5, the pictures and every old tag stay as they were, the new tags read back through three readers, and every way a write can go wrong
/// leaves the original file untouched. Only copies in a temp folder are ever written.
/// </summary>
public sealed class FlacTagWriterTests : IDisposable
{
    private static readonly LoudnessTagValues Values = new(-18.40, 0.912345);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ibt-tagwriter-" + Guid.NewGuid().ToString("N"));
    private readonly string _pristine;

    public FlacTagWriterTests()
    {
        Directory.CreateDirectory(_dir);
        _pristine = Path.Combine(_dir, "pristine");
        Directory.CreateDirectory(_pristine);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Left for the system temp cleanup.
        }
    }

    private (string Path, string Pristine) Prepare(string kind)
    {
        FlacFixtures.RequireFfmpeg();
        var path = FlacFixtures.Copy(kind, _dir, kind + ".flac");
        var pristine = FlacFixtures.Copy(kind, _pristine, kind + ".flac");
        return (path, pristine);
    }

    internal static readonly string[] Owned = ["REPLAYGAIN_TRACK_GAIN", "REPLAYGAIN_TRACK_PEAK", "REPLAYGAIN_REFERENCE_LOUDNESS", FlacLoudnessTagWriter.VersionField, FlacLoudnessTagWriter.Md5Field];

    internal static string NameOf(string entry) => entry[..Math.Max(0, entry.IndexOf('='))].ToUpperInvariant();

    private void AssertNoLeftovers() =>
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.ibt-*", SearchOption.AllDirectories));

    // Everything that must hold after a successful write, checked here without the writer's own code where possible.
    internal static void AssertPreserved(string pristine, string written)
    {
        var before = FlacFixtures.Snapshot.Of(pristine);
        var after = FlacFixtures.Snapshot.Of(written);
        Assert.Equal(before.FramesHash, after.FramesHash);
        Assert.Equal(before.StreamInfoMd5, after.StreamInfoMd5);
        Assert.Equal(before.BitsPerSample, after.BitsPerSample);
        Assert.Equal(before.PrefixHash, after.PrefixHash);
        Assert.Equal(
            before.Blocks.Where(b => b.Type is not (1 or 4)).ToList(),
            after.Blocks.Where(b => b.Type is not (1 or 4)).ToList()); // STREAMINFO, pictures, seek table... byte for byte

        var (vendorBefore, entriesBefore) = FlacFixtures.Comments(pristine);
        var (vendorAfter, entriesAfter) = FlacFixtures.Comments(written);
        if (entriesBefore.Count > 0)
        {
            Assert.Equal(vendorBefore, vendorAfter);
        }

        var kept = entriesBefore.Where(e => !Owned.Contains(NameOf(e))).ToList();
        Assert.Equal(kept, entriesAfter.Take(kept.Count).ToList()); // every old tag, same bytes, same order
        Assert.Equal(Owned.OrderBy(x => x), entriesAfter.Skip(kept.Count).Select(NameOf).OrderBy(x => x));
        Assert.Contains("REPLAYGAIN_TRACK_GAIN=+0.40 dB", entriesAfter);
        Assert.Contains("REPLAYGAIN_TRACK_PEAK=0.912345", entriesAfter);
        Assert.Contains("REPLAYGAIN_REFERENCE_LOUDNESS=-18.00 LUFS", entriesAfter);
        Assert.Contains(FlacLoudnessTagWriter.VersionField + "=" + LoudnessCache.AnalysisVersion, entriesAfter);
        Assert.Contains(FlacLoudnessTagWriter.Md5Field + "=" + before.StreamInfoMd5, entriesAfter);
    }

    [Theory]
    [InlineData("plain16")]
    [InlineData("tags24")]
    [InlineData("cover24")]
    [InlineData("cover16")]
    [InlineData("bare")]
    [InlineData("id3")]
    [InlineData("foreign-gain")]
    public void Writing_keeps_the_audio_the_pictures_and_the_old_tags_and_the_new_tags_read_back(string kind)
    {
        var (path, pristine) = Prepare(kind);
        var created = File.GetCreationTimeUtc(path);
        var modified = File.GetLastWriteTimeUtc(path);
        var decodedBefore = FlacFixtures.DecodedMd5(path);

        var result = new FlacLoudnessTagWriter().Write(path, Values, CancellationToken.None);

        Assert.True(result.Outcome == TagWriteOutcome.Written, result.Reason);
        AssertPreserved(pristine, path);
        Assert.Equal(decodedBefore, FlacFixtures.DecodedMd5(path)); // the decoded samples are the same, by ffmpeg
        Assert.Equal(created, File.GetCreationTimeUtc(path));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        Assert.Equal(new FileInfo(path).Length, result.NewSize);
        Assert.Equal(new FileInfo(path).LastWriteTimeUtc.Ticks, result.NewWriteTicks);
        AssertNoLeftovers();

        var marked = FlacLoudnessTagWriter.ReadMarked(path)!;
        Assert.Equal(Values.IntegratedLufs, marked.IntegratedLufs, 2);
        Assert.Equal(Values.TruePeak, marked.TruePeak, 6);

        // A third reader: ffmpeg lists the tags.
        var metadata = FlacFixtures.RunFfmpeg("-i", path, "-f", "ffmetadata", "-");
        Assert.Contains("REPLAYGAIN_TRACK_GAIN=+0.40 dB", metadata, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("REPLAYGAIN_TRACK_PEAK=0.912345", metadata, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_file_with_cover_and_tags_keeps_title_artist_album_and_the_picture_for_TagLib_too()
    {
        var (path, pristine) = Prepare("cover24");
        using var before = TagLib.File.Create(pristine);
        var pictureBefore = before.Tag.Pictures[0].Data.Data;

        new FlacLoudnessTagWriter().Write(path, Values, CancellationToken.None);

        using var after = TagLib.File.Create(path);
        Assert.Equal("Amazing Grace", after.Tag.Title);
        Assert.Equal("Culto de Domingo", after.Tag.Album);
        Assert.Equal(["Josh Snodgrass"], after.Tag.Performers);
        Assert.Equal(3u, after.Tag.Track);
        Assert.Single(after.Tag.Pictures);
        Assert.Equal(pictureBefore, after.Tag.Pictures[0].Data.Data);
        Assert.Equal(24, after.Properties.BitsPerSample);
    }

    [Fact]
    public void The_padding_absorbs_the_change_so_the_audio_does_not_move_when_it_fits()
    {
        var (path, pristine) = Prepare("tags24");
        var before = FlacFixtures.Snapshot.Of(pristine);
        Assert.Contains(before.Blocks, b => b.Type == FlacBlock.Padding); // ffmpeg writes padding

        new FlacLoudnessTagWriter().Write(path, Values, CancellationToken.None);

        Assert.Equal(before.Length, new FileInfo(path).Length); // the new tags used up padding: same size, same offsets
    }

    [Fact]
    public void Writing_twice_replaces_the_values_instead_of_piling_them_up()
    {
        var (path, pristine) = Prepare("tags24");
        var writer = new FlacLoudnessTagWriter();

        writer.Write(path, new LoudnessTagValues(-25, 0.3), CancellationToken.None);
        var second = writer.Write(path, Values, CancellationToken.None);

        Assert.Equal(TagWriteOutcome.Written, second.Outcome);
        AssertPreserved(pristine, path);
        var (_, entries) = FlacFixtures.Comments(path);
        Assert.Single(entries, e => NameOf(e) == "REPLAYGAIN_TRACK_GAIN");
    }

    [Fact]
    public void Foreign_ReplayGain_tags_are_replaced_the_album_tags_stay_and_are_not_trusted_without_the_marker()
    {
        var (path, _) = Prepare("foreign-gain");

        Assert.Null(FlacLoudnessTagWriter.ReadMarked(path)); // there is a REPLAYGAIN_TRACK_GAIN but it is not ours

        new FlacLoudnessTagWriter().Write(path, Values, CancellationToken.None);

        var (_, entries) = FlacFixtures.Comments(path);
        Assert.Single(entries, e => NameOf(e) == "REPLAYGAIN_TRACK_GAIN");
        Assert.Contains("REPLAYGAIN_ALBUM_GAIN=-2.00 dB", entries);
        Assert.DoesNotContain("REPLAYGAIN_TRACK_GAIN=-1.00 dB", entries);
        Assert.NotNull(FlacLoudnessTagWriter.ReadMarked(path));
    }

    [Fact]
    public void The_marker_is_trusted_only_for_this_version_and_this_audio()
    {
        var (path, _) = Prepare("tags24");
        new FlacLoudnessTagWriter().Write(path, Values, CancellationToken.None);

        Assert.NotNull(FlacLoudnessTagWriter.ReadMarked(path));
        Assert.Null(FlacLoudnessTagWriter.ReadMarked(path, analysisVersion: LoudnessCache.AnalysisVersion + 1));

        // The audio MD5 in STREAMINFO changes (the file was re-encoded): the old tags no longer describe it.
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            var layout = FlacLayout.Read(stream);
            Assert.True(layout.Blocks[0].Data!.Length == 34);
            stream.Seek(4 + 4 + 18 + layout.Prefix.Length, SeekOrigin.Begin);
            stream.WriteByte(0xAB);
        }

        Assert.Null(FlacLoudnessTagWriter.ReadMarked(path));
    }

    [Fact]
    public void Formats_follow_ReplayGain_2_with_the_reference_at_minus_18_LUFS()
    {
        Assert.Equal("-3.21 dB", FlacLoudnessTagWriter.FormatGain(-14.79));
        Assert.Equal("+2.40 dB", FlacLoudnessTagWriter.FormatGain(-20.40));
        Assert.Equal("0.00 dB", FlacLoudnessTagWriter.FormatGain(-18));
        Assert.Equal("0.500000", FlacLoudnessTagWriter.FormatPeak(0.5));
    }

    // ---- everything that can go wrong leaves the original untouched ----

    private void AssertUntouched(string path, string fingerprint)
    {
        Assert.Equal(fingerprint, FlacFixtures.FileFingerprint(path));
        AssertNoLeftovers();
    }

    [Fact]
    public void An_exception_while_the_copy_is_made_leaves_the_original_untouched()
    {
        var (path, _) = Prepare("cover24");
        var fingerprint = FlacFixtures.FileFingerprint(path);
        var hooks = new FlacLoudnessTagWriter.WriterHooks { AfterTempWritten = _ => throw new IOException("disco cheio") };

        var result = new FlacLoudnessTagWriter(hooks: hooks).Write(path, Values, CancellationToken.None);

        Assert.Equal(TagWriteOutcome.Failed, result.Outcome);
        AssertUntouched(path, fingerprint);
    }

    [Theory]
    [InlineData("audio")]
    [InlineData("picture")]
    [InlineData("tag")]
    public void A_copy_that_does_not_match_the_original_is_never_used(string damage)
    {
        var (path, _) = Prepare("cover24");
        var fingerprint = FlacFixtures.FileFingerprint(path);
        var hooks = new FlacLoudnessTagWriter.WriterHooks
        {
            AfterTempWritten = temp =>
            {
                byte[] bytes = File.ReadAllBytes(temp);
                FlacLayout layout;
                using (var stream = new MemoryStream(bytes))
                {
                    layout = FlacLayout.Read(stream);
                }

                switch (damage)
                {
                    case "audio":
                        bytes[^200] ^= 0x01; // one bit of one audio frame
                        break;
                    case "picture":
                        var picture = layout.Blocks.First(b => b.Type == FlacBlock.Picture);
                        var at = 4 + layout.Prefix.Length + layout.Blocks.TakeWhile(b => b != picture).Sum(b => 4 + b.Length) + 4 + 100;
                        bytes[at] ^= 0x01;
                        break;
                    default:
                        var comment = layout.Blocks.First(b => b.Type == FlacBlock.VorbisComment);
                        var start = 4 + layout.Prefix.Length + layout.Blocks.TakeWhile(b => b != comment).Sum(b => 4 + b.Length) + 4;
                        bytes[start + 4 + VorbisComments.Parse(comment.Data!).Vendor.Length + 4 + 4 + 2] ^= 0x01; // a letter of the first old tag
                        break;
                }

                File.WriteAllBytes(temp, bytes);
            },
        };

        var result = new FlacLoudnessTagWriter(hooks: hooks).Write(path, Values, CancellationToken.None);

        Assert.Equal(TagWriteOutcome.Failed, result.Outcome);
        Assert.Contains("verificacao", result.Reason);
        AssertUntouched(path, fingerprint);
    }

    [Fact]
    public void An_exception_just_before_the_swap_leaves_the_original_untouched()
    {
        var (path, _) = Prepare("tags24");
        var fingerprint = FlacFixtures.FileFingerprint(path);
        var hooks = new FlacLoudnessTagWriter.WriterHooks { BeforeReplace = _ => throw new InvalidOperationException("falha") };

        var result = new FlacLoudnessTagWriter(hooks: hooks).Write(path, Values, CancellationToken.None);

        Assert.Equal(TagWriteOutcome.Failed, result.Outcome);
        AssertUntouched(path, fingerprint);
    }

    [Fact]
    public void A_swap_that_the_system_refuses_leaves_the_original_untouched()
    {
        var (path, _) = Prepare("tags24");
        var fingerprint = FlacFixtures.FileFingerprint(path);
        FileStream? holder = null;
        var hooks = new FlacLoudnessTagWriter.WriterHooks
        {
            // Another program opens the file between the check and the swap, without allowing a replace.
            BeforeReplace = _ => holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read),
        };

        TagWriteResult result;
        try
        {
            result = new FlacLoudnessTagWriter(hooks: hooks).Write(path, Values, CancellationToken.None);
        }
        finally
        {
            holder?.Dispose();
        }

        Assert.NotEqual(TagWriteOutcome.Written, result.Outcome);
        AssertUntouched(path, fingerprint);
    }

    [Fact]
    public void A_file_that_is_open_is_skipped_as_busy_and_works_after_it_is_closed()
    {
        var (path, _) = Prepare("tags24");
        var fingerprint = FlacFixtures.FileFingerprint(path);

        TagWriteResult busy;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) // like the player decoding it
        {
            busy = new FlacLoudnessTagWriter().Write(path, Values, CancellationToken.None);
        }

        Assert.Equal(TagWriteOutcome.Busy, busy.Outcome);
        AssertUntouched(path, fingerprint);
        Assert.Equal(TagWriteOutcome.Written, new FlacLoudnessTagWriter().Write(path, Values, CancellationToken.None).Outcome);
    }

    [Fact]
    public void A_read_only_a_missing_and_a_non_FLAC_file_are_not_written()
    {
        var (path, _) = Prepare("tags24");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var fingerprint = FlacFixtures.FileFingerprint(path);
        var writer = new FlacLoudnessTagWriter();

        Assert.Equal(TagWriteOutcome.NotWritable, writer.Write(path, Values, CancellationToken.None).Outcome);
        AssertUntouched(path, fingerprint);
        File.SetAttributes(path, FileAttributes.Normal);

        Assert.Equal(TagWriteOutcome.NotWritable, writer.Write(Path.Combine(_dir, "missing.flac"), Values, CancellationToken.None).Outcome);

        var mp3 = Path.Combine(_dir, "song.mp3");
        File.WriteAllBytes(mp3, [1, 2, 3, 4]);
        Assert.Equal(TagWriteOutcome.NotWritable, writer.Write(mp3, Values, CancellationToken.None).Outcome);
        Assert.False(writer.Supports(mp3));
        Assert.True(writer.Supports(path));
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(mp3));
    }

    [Fact]
    public void A_file_that_is_not_really_a_FLAC_fails_and_is_left_alone()
    {
        var path = Path.Combine(_dir, "broken.flac");
        File.WriteAllBytes(path, [.. "fLaC"u8.ToArray(), 0x80, 0, 0, 5, 1, 2, 3]);
        var fingerprint = FlacFixtures.FileFingerprint(path);

        var result = new FlacLoudnessTagWriter().Write(path, Values, CancellationToken.None);

        Assert.Equal(TagWriteOutcome.Failed, result.Outcome);
        AssertUntouched(path, fingerprint);
    }

    [Fact]
    public void A_cancelled_write_changes_nothing()
    {
        var (path, _) = Prepare("tags24");
        var fingerprint = FlacFixtures.FileFingerprint(path);
        using var source = new CancellationTokenSource();
        var hooks = new FlacLoudnessTagWriter.WriterHooks { AfterTempWritten = _ => source.Cancel() };

        var result = new FlacLoudnessTagWriter(hooks: hooks).Write(path, Values, source.Token);

        Assert.Equal(TagWriteOutcome.Cancelled, result.Outcome);
        AssertUntouched(path, fingerprint);
    }

    [Fact]
    public void Leftovers_of_a_crashed_write_are_cleaned_and_a_lost_original_comes_back_from_the_backup()
    {
        var (path, _) = Prepare("tags24");
        File.WriteAllText(path + FlacLoudnessTagWriter.TempSuffix, "junk");
        File.WriteAllText(path + FlacLoudnessTagWriter.BackupSuffix, "old backup");

        Assert.Equal(TagWriteOutcome.Written, new FlacLoudnessTagWriter().Write(path, Values, CancellationToken.None).Outcome);
        AssertNoLeftovers();

        // The crash happened after the original was moved away: only the backup exists, and it is the music.
        var other = FlacFixtures.Copy("tags24", _dir, "other.flac");
        File.Move(other, other + FlacLoudnessTagWriter.BackupSuffix);
        Assert.Equal(TagWriteOutcome.Written, new FlacLoudnessTagWriter().Write(other, Values, CancellationToken.None).Outcome);
        Assert.True(File.Exists(other));
        AssertNoLeftovers();
    }
}
