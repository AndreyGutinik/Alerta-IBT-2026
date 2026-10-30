using AlertaIBT.Core.Loudness;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Loudness;

/// <summary>The cache (keys, invalidation, corruption) and the background scanner (priority, throttle, pause, resume, cancel, tags), with a fake clock and fake decoder.</summary>
public sealed class LoudnessScannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ibt-loudness-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));

    public LoudnessScannerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A thread may still hold a file for a moment.
        }
    }

    private string File_(string name, int size = 100)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    private string[] Files(int count, string extension = ".flac") =>
        Enumerable.Range(1, count).Select(i => File_($"t{i:00}{extension}")).ToArray();

    private LoudnessCache NewCache() => new(Path.Combine(_dir, "cache", "loudness-cache.json"), _clock);

    private sealed class FakeMeasurer(FakeTimeProvider clock, TimeSpan work) : ILoudnessMeasurer
    {
        public List<string> Measured { get; } = [];

        public Func<string, LoudnessResult?> Result { get; set; } = _ => new LoudnessResult(-20, 0.5, 0.5, 100);

        public ThreadPriority? Priority { get; private set; }

        public LoudnessResult? Measure(string path, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Priority = Thread.CurrentThread.Priority;
            Measured.Add(path);
            clock.Advance(work);
            return Result(path);
        }
    }

    private sealed class FakeWriter : ILoudnessTagWriter
    {
        public List<string> Written { get; } = [];

        public Func<string, TagWriteResult>? Outcome { get; set; }

        public bool Supports(string path) => path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase);

        public TagWriteResult Write(string path, LoudnessTagValues values, CancellationToken cancellation)
        {
            Written.Add(path);
            if (Outcome is { } outcome)
            {
                return outcome(path);
            }

            // Pretend to add the tags: the size grows and the time moves, like a real write would.
            File.AppendAllText(path, "tags");
            var info = new FileInfo(path);
            return new TagWriteResult(TagWriteOutcome.Written, null, info.Length, info.LastWriteTimeUtc.Ticks);
        }
    }

    private static TagWriteResult Ok(string path)
    {
        var info = new FileInfo(path);
        return new TagWriteResult(TagWriteOutcome.Written, null, info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private static ScannerOptions Options() => new() { MaxCpuShare = 0.25, TagInterval = TimeSpan.FromSeconds(4), IdleAfterActivity = TimeSpan.FromSeconds(3), BusyRetry = TimeSpan.FromSeconds(30) };

    // ---- the cache ----

    [Fact]
    public void A_stored_measurement_is_a_hit_until_the_file_changes()
    {
        var cache = NewCache();
        var path = File_("a.flac", 100);
        cache.Set(LoudnessCache.Stamp(path, new LoudnessEntry { Path = path, Status = MeasureStatus.Measured, Lufs = -18, TruePeak = 0.5 })!);

        Assert.Equal(-18, cache.Get(path)!.Lufs);

        File.AppendAllText(path, "x"); // size changes
        Assert.Null(cache.Get(path));

        cache.Set(LoudnessCache.Stamp(path, new LoudnessEntry { Path = path, Status = MeasureStatus.Measured, Lufs = -17 })!);
        Assert.NotNull(cache.Get(path));
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)); // time changes, size does not
        Assert.Null(cache.Get(path));
    }

    [Fact]
    public void A_different_analysis_version_or_a_missing_file_is_a_miss()
    {
        var cache = NewCache();
        var path = File_("a.flac");
        var stamped = LoudnessCache.Stamp(path, new LoudnessEntry { Path = path, Status = MeasureStatus.Measured, Lufs = -18 })!;

        cache.Set(stamped with { Version = LoudnessCache.AnalysisVersion + 1 });
        Assert.Null(cache.Get(path));

        cache.Set(stamped);
        File.Delete(path);
        Assert.Null(cache.Get(path));
        Assert.Null(cache.Get(Path.Combine(_dir, "never.flac")));
    }

    [Fact]
    public void The_cache_is_written_atomically_and_read_back_and_saves_are_spaced()
    {
        var cache = NewCache();
        var path = File_("a.flac");
        cache.Set(LoudnessCache.Stamp(path, new LoudnessEntry { Path = path, Status = MeasureStatus.Measured, Lufs = -18.25, SamplePeak = 0.4, TruePeak = 0.45, Tags = TagState.Written })!);

        cache.SaveIfDirty(TimeSpan.FromSeconds(10));
        Assert.False(File.Exists(cache.FilePath)); // too soon after the start
        _clock.Advance(TimeSpan.FromSeconds(11));
        cache.SaveIfDirty(TimeSpan.FromSeconds(10));

        Assert.True(File.Exists(cache.FilePath));
        Assert.False(File.Exists(cache.FilePath + ".tmp"));
        var again = new LoudnessCache(cache.FilePath, _clock);
        Assert.False(again.Load());
        var entry = again.Get(path)!;
        Assert.Equal(-18.25, entry.Lufs);
        Assert.Equal(0.45, entry.TruePeak);
        Assert.Equal(TagState.Written, entry.Tags);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("{\"analysisVersion\":1,\"entries\":[{\"path\":")]
    [InlineData("[1,2,3]")]
    public void A_corrupted_cache_is_set_aside_and_rebuilt_empty(string content)
    {
        var cache = NewCache();
        Directory.CreateDirectory(Path.GetDirectoryName(cache.FilePath)!);
        File.WriteAllText(cache.FilePath, content);

        var damaged = cache.Load();

        Assert.True(damaged);
        Assert.Equal(0, cache.Count);
        Assert.True(File.Exists(cache.FilePath + ".bad"));
        var path = File_("a.flac");
        cache.Set(LoudnessCache.Stamp(path, new LoudnessEntry { Path = path, Status = MeasureStatus.Measured, Lufs = -18 })!);
        cache.SaveIfDirty(TimeSpan.Zero);
        Assert.False(new LoudnessCache(cache.FilePath, _clock).Load());
    }

    // ---- the scanner ----

    [Fact]
    public void It_measures_the_unmeasured_files_one_at_a_time_and_skips_the_measured_ones()
    {
        var cache = NewCache();
        var files = Files(4);
        var done = LoudnessCache.Stamp(files[1], new LoudnessEntry { Path = files[1], Status = MeasureStatus.Measured, Lufs = -20 })!;
        cache.Set(done);
        var measurer = new FakeMeasurer(_clock, TimeSpan.FromSeconds(1));
        using var scanner = new LoudnessScanner(cache, measurer, clock: _clock, options: Options());
        scanner.Configure(files);

        var kinds = new List<ScanStepKind>();
        for (var i = 0; i < 5; i++)
        {
            kinds.Add(scanner.ProcessNext(CancellationToken.None).Kind);
        }

        Assert.Equal([files[0], files[2], files[3]], measurer.Measured);
        Assert.Equal(ScanStepKind.Idle, kinds[^1]);
        Assert.Equal(new ScanProgress(4, 4, false, 0, 0), scanner.Progress);
        Assert.NotNull(scanner.Get(files[0]));
    }

    [Fact]
    public void A_file_that_changed_is_measured_again_and_a_failed_or_silent_one_is_not_retried()
    {
        var cache = NewCache();
        var files = Files(3);
        var measurer = new FakeMeasurer(_clock, TimeSpan.Zero)
        {
            Result = p => p == files[1] ? null : p == files[2] ? new LoudnessResult(null, 0, 0, 5) : new LoudnessResult(-20, 0.5, 0.5, 5),
        };
        using var scanner = new LoudnessScanner(cache, measurer, clock: _clock, options: Options());
        scanner.Configure(files);
        for (var i = 0; i < 4; i++)
        {
            scanner.ProcessNext(CancellationToken.None);
        }

        Assert.Equal(MeasureStatus.Failed, cache.Get(files[1])!.Status);
        Assert.Equal(MeasureStatus.Silent, cache.Get(files[2])!.Status);
        Assert.Null(scanner.Get(files[1])); // plays at 0 dB
        Assert.Null(scanner.Get(files[2]));
        Assert.Equal(3, measurer.Measured.Count);

        File.AppendAllText(files[0], "changed");
        scanner.ProcessNext(CancellationToken.None);
        scanner.ProcessNext(CancellationToken.None);

        Assert.Equal(4, measurer.Measured.Count);
        Assert.Equal(files[0], measurer.Measured[^1]);
    }

    [Fact]
    public void After_each_file_it_waits_so_the_scan_stays_at_a_quarter_of_a_core()
    {
        var cache = NewCache();
        var files = Files(2);
        var measurer = new FakeMeasurer(_clock, TimeSpan.FromSeconds(2));
        using var scanner = new LoudnessScanner(cache, measurer, clock: _clock, options: Options());
        scanner.Configure(files);

        var step = scanner.ProcessNext(CancellationToken.None);

        Assert.Equal(ScanStepKind.Measured, step.Kind);
        Assert.Equal(TimeSpan.FromSeconds(6), step.Wait); // 2 s of work is 25 % of 8 s
    }

    [Fact]
    public void The_share_of_the_core_is_adjustable()
    {
        var cache = NewCache();
        var measurer = new FakeMeasurer(_clock, TimeSpan.FromSeconds(3));
        using var scanner = new LoudnessScanner(cache, measurer, clock: _clock, options: Options() with { MaxCpuShare = 0.5 });
        scanner.Configure(Files(1));

        Assert.Equal(TimeSpan.FromSeconds(3), scanner.ProcessNext(CancellationToken.None).Wait);
    }

    [Fact]
    public void It_pauses_while_the_user_changes_the_playlists_and_goes_on_after_the_quiet_time()
    {
        var cache = NewCache();
        var files = Files(2);
        var measurer = new FakeMeasurer(_clock, TimeSpan.Zero);
        using var scanner = new LoudnessScanner(cache, measurer, clock: _clock, options: Options());
        scanner.Configure(files);
        scanner.NotifyUserActivity();

        var paused = scanner.ProcessNext(CancellationToken.None);
        Assert.Equal(ScanStepKind.Waiting, paused.Kind);
        Assert.Equal(TimeSpan.FromSeconds(3), paused.Wait);
        Assert.Empty(measurer.Measured);

        _clock.Advance(TimeSpan.FromSeconds(1));
        scanner.NotifyUserActivity(); // more activity: the quiet time starts over
        Assert.Equal(TimeSpan.FromSeconds(3), scanner.ProcessNext(CancellationToken.None).Wait);

        _clock.Advance(TimeSpan.FromSeconds(3.1));
        Assert.Equal(ScanStepKind.Measured, scanner.ProcessNext(CancellationToken.None).Kind);
    }

    [Fact]
    public void A_track_that_started_unmeasured_is_measured_first_and_even_during_the_pause()
    {
        var cache = NewCache();
        var files = Files(4);
        var measurer = new FakeMeasurer(_clock, TimeSpan.Zero);
        using var scanner = new LoudnessScanner(cache, measurer, clock: _clock, options: Options());
        scanner.Configure(files);
        scanner.NotifyUserActivity();
        scanner.Request(files[3]);

        var step = scanner.ProcessNext(CancellationToken.None);

        Assert.Equal(files[3], step.Path);
        Assert.Equal([files[3]], measurer.Measured);
        scanner.Request(files[3]); // already measured: nothing to do
        Assert.Equal(ScanStepKind.Waiting, scanner.ProcessNext(CancellationToken.None).Kind); // back to the pause
        Assert.Single(measurer.Measured);
    }

    [Fact]
    public void Progress_counts_measured_over_total_and_is_raised_on_change()
    {
        var cache = NewCache();
        var files = Files(5);
        var measurer = new FakeMeasurer(_clock, TimeSpan.Zero);
        using var scanner = new LoudnessScanner(cache, measurer, clock: _clock, options: Options());
        var seen = new List<ScanProgress>();
        scanner.ProgressChanged += seen.Add;

        scanner.Configure(files);
        scanner.ProcessNext(CancellationToken.None);
        scanner.ProcessNext(CancellationToken.None);

        Assert.Equal(new ScanProgress(0, 5, true, 0, 0), seen[0]);
        Assert.Equal(new ScanProgress(2, 5, true, 0, 0), seen[^1]);
        Assert.Equal(new ScanProgress(2, 5, true, 0, 0), scanner.Progress);
    }

    [Fact]
    public void The_playlist_in_use_goes_first_and_the_others_after_it()
    {
        var cache = NewCache();
        var main = Files(2);
        var other = new[] { File_("o1.flac"), File_("o2.flac") };
        var measurer = new FakeMeasurer(_clock, TimeSpan.Zero);
        using var scanner = new LoudnessScanner(cache, measurer, clock: _clock, options: Options());
        scanner.Configure(main, other);

        for (var i = 0; i < 4; i++)
        {
            scanner.ProcessNext(CancellationToken.None);
        }

        Assert.Equal([.. main, .. other], measurer.Measured);
        Assert.Equal(4, scanner.Progress.Total);
    }

    [Fact]
    public void After_a_restart_it_goes_on_where_it_stopped()
    {
        var cache = NewCache();
        var files = Files(5);
        var measurer = new FakeMeasurer(_clock, TimeSpan.Zero);
        using (var first = new LoudnessScanner(cache, measurer, clock: _clock, options: Options()))
        {
            first.Configure(files);
            first.ProcessNext(CancellationToken.None);
            first.ProcessNext(CancellationToken.None);
        }

        var reloaded = new LoudnessCache(cache.FilePath, _clock);
        reloaded.Load();
        var measurer2 = new FakeMeasurer(_clock, TimeSpan.Zero);
        using var second = new LoudnessScanner(reloaded, measurer2, clock: _clock, options: Options());
        second.Configure(files);
        Assert.Equal(2, second.Progress.Measured);
        for (var i = 0; i < 4; i++)
        {
            second.ProcessNext(CancellationToken.None);
        }

        Assert.Equal(files.Skip(2).ToList(), measurer2.Measured);
        Assert.Equal(5, second.Progress.Measured);
    }

    [Fact]
    public void A_cancelled_measurement_stops_the_step_and_leaves_no_entry()
    {
        var cache = NewCache();
        var files = Files(2);
        var measurer = new FakeMeasurer(_clock, TimeSpan.Zero);
        using var scanner = new LoudnessScanner(cache, measurer, clock: _clock, options: Options());
        scanner.Configure(files);
        using var source = new CancellationTokenSource();
        source.Cancel();

        Assert.Throws<OperationCanceledException>(() => scanner.ProcessNext(source.Token));

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task The_background_thread_runs_at_below_normal_priority_finishes_the_work_and_stops_on_dispose()
    {
        var cache = NewCache();
        var files = Files(4);
        var measurer = new FakeMeasurer(new FakeTimeProvider(), TimeSpan.Zero);
        var scanner = new LoudnessScanner(cache, measurer, options: Options() with { MaxCpuShare = 1.0 });
        scanner.Configure(files);
        scanner.Start();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (scanner.Progress.Measured < 4 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Equal(4, scanner.Progress.Measured);
        Assert.Equal(ThreadPriority.BelowNormal, measurer.Priority);
        scanner.Dispose();
        Assert.True(File.Exists(cache.FilePath)); // written on exit
    }

    // ---- the tags ----

    [Fact]
    public void Tags_are_written_one_file_per_interval_after_measuring_and_the_cache_key_follows_the_new_size()
    {
        var cache = NewCache();
        var files = Files(3);
        var writer = new FakeWriter();
        var measurer = new FakeMeasurer(_clock, TimeSpan.Zero);
        using var scanner = new LoudnessScanner(cache, measurer, writer, () => true, clock: _clock, options: Options());
        scanner.Configure(files);

        var step = scanner.ProcessNext(CancellationToken.None); // measures the first (no tag was due before: the interval counts from the start of time)
        Assert.Equal(ScanStepKind.Measured, step.Kind);
        step = scanner.ProcessNext(CancellationToken.None);
        Assert.Equal(ScanStepKind.TagsWritten, step.Kind);
        Assert.Equal(files[0], step.Path);
        Assert.Equal(TimeSpan.FromSeconds(4), step.Wait);

        // Right after a write the next tag waits its turn: the scan measures meanwhile.
        Assert.Equal(ScanStepKind.Measured, scanner.ProcessNext(CancellationToken.None).Kind);
        Assert.Single(writer.Written);
        _clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(ScanStepKind.TagsWritten, scanner.ProcessNext(CancellationToken.None).Kind);
        Assert.Equal([files[0], files[1]], writer.Written);

        // The file grew by 4 bytes: the entry still matches it (it would otherwise be measured again).
        Assert.NotNull(cache.Get(files[0]));
        Assert.Equal(TagState.Written, cache.Get(files[0])!.Tags);
        Assert.Equal(1, measurer.Measured.Count(p => p == files[0]));
    }

    [Fact]
    public void With_the_switch_off_or_for_non_FLAC_files_no_tag_is_written()
    {
        var cache = NewCache();
        var flac = Files(1);
        var mp3 = new[] { File_("song.mp3") };
        var writer = new FakeWriter();
        var on = false;
        var measurer = new FakeMeasurer(_clock, TimeSpan.Zero);
        using var scanner = new LoudnessScanner(cache, measurer, writer, () => on, clock: _clock, options: Options());
        scanner.Configure([.. flac, .. mp3]);

        for (var i = 0; i < 4; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(10));
            scanner.ProcessNext(CancellationToken.None);
        }

        Assert.Empty(writer.Written);
        Assert.Equal(TagState.Pending, cache.Get(flac[0])!.Tags);
        Assert.Equal(TagState.NotApplicable, cache.Get(mp3[0])!.Tags);

        on = true; // the owner switches it on later: the FLAC that was waiting gets its tags
        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(ScanStepKind.TagsWritten, scanner.ProcessNext(CancellationToken.None).Kind);
        Assert.Equal([flac[0]], writer.Written);
    }

    [Fact]
    public void A_file_the_player_holds_is_skipped_and_retried_later_and_a_failure_is_never_retried()
    {
        var cache = NewCache();
        var files = Files(2);
        var playing = files[0];
        var writer = new FakeWriter { Outcome = p => p == files[1] ? new TagWriteResult(TagWriteOutcome.Failed, "verificacao falhou") : Ok(p) };
        var measurer = new FakeMeasurer(_clock, TimeSpan.Zero);
        using var scanner = new LoudnessScanner(cache, measurer, writer, () => true, p => p == playing, _clock, options: Options());
        scanner.Configure(files);
        scanner.ProcessNext(CancellationToken.None);
        scanner.ProcessNext(CancellationToken.None); // both measured

        _clock.Advance(TimeSpan.FromSeconds(10));
        var step = scanner.ProcessNext(CancellationToken.None); // the playing one is skipped, the other fails
        Assert.Equal(ScanStepKind.TagsSkipped, step.Kind);
        Assert.Equal([files[1]], writer.Written);
        Assert.Equal(TagState.DoNotRetry, cache.Get(files[1])!.Tags);
        Assert.Equal("verificacao falhou", cache.Get(files[1])!.Note);
        Assert.Equal(TagState.Pending, cache.Get(files[0])!.Tags);

        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(ScanStepKind.Waiting, scanner.ProcessNext(CancellationToken.None).Kind); // still within the busy retry time
        Assert.Single(writer.Written);

        playing = string.Empty; // the track ended
        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(ScanStepKind.TagsWritten, scanner.ProcessNext(CancellationToken.None).Kind);
        Assert.Equal([files[1], files[0]], writer.Written);

        _clock.Advance(TimeSpan.FromSeconds(100));
        Assert.Equal(ScanStepKind.Idle, scanner.ProcessNext(CancellationToken.None).Kind);
        Assert.Equal(2, writer.Written.Count); // the failed file is never tried again
    }

    [Fact]
    public void A_busy_answer_from_the_writer_is_retried_later_and_not_counted_as_a_failure()
    {
        var cache = NewCache();
        var files = Files(1);
        var busy = true;
        var writer = new FakeWriter { Outcome = _ => busy ? new TagWriteResult(TagWriteOutcome.Busy, "em uso") : Ok(files[0]) };
        var measurer = new FakeMeasurer(_clock, TimeSpan.Zero);
        using var scanner = new LoudnessScanner(cache, measurer, writer, () => true, clock: _clock, options: Options());
        scanner.Configure(files);
        scanner.ProcessNext(CancellationToken.None);

        Assert.Equal(ScanStepKind.TagsSkipped, scanner.ProcessNext(CancellationToken.None).Kind);
        Assert.Equal(TagState.Pending, cache.Get(files[0])!.Tags);

        busy = false;
        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(ScanStepKind.TagsWritten, scanner.ProcessNext(CancellationToken.None).Kind);
    }
}
