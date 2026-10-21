using System.Text;
using AlertaIBT.Core.Correction;
using AlertaIBT.Core.Tests.Services;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Tests.Correction;

// The file of extra words, always in a temp folder: never the real AppData.
public sealed class ProfanityFilterExtraWordsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alertaibt-profanity-" + Guid.NewGuid().ToString("N"));
    private readonly CapturingLogger _logger = new();

    public ProfanityFilterExtraWordsTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder left behind is harmless.
        }
    }

    private string FilePath => Path.Combine(_dir, ProfanityFilter.ExtraWordsFileName);

    private ProfanityFilter Filter() => new(FilePath, _logger);

    [Fact]
    public void The_file_name_is_the_documented_one()
    {
        Assert.Equal("palavras-bloqueadas.txt", ProfanityFilter.ExtraWordsFileName);
    }

    [Fact]
    public void A_missing_file_means_the_built_in_list_alone_and_logs_nothing()
    {
        var filter = Filter();

        Assert.False(filter.IsBlocked("culto de domingo"));
        Assert.True(filter.IsBlocked("que merda"));
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public void No_file_path_at_all_is_fine()
    {
        var filter = new ProfanityFilter();

        Assert.False(filter.IsBlocked("culto de domingo"));
        Assert.True(filter.IsBlocked("que merda"));
    }

    [Fact]
    public void Words_and_phrases_of_the_file_are_blocked_whole_and_the_built_ins_still_are()
    {
        File.WriteAllText(FilePath, "# palavras da igreja\n\nxingamento\n  Pastor Falso  \nBobao\n", new UTF8Encoding(false));
        var filter = Filter();

        Assert.True(filter.IsBlocked("que xingamento"));
        Assert.True(filter.IsBlocked("XINGAMENTO!"));
        Assert.True(filter.IsBlocked("x1ngament0")); // leetspeak and capitals do not help
        Assert.True(filter.IsBlocked("o pastor  falso chegou"));
        Assert.True(filter.IsBlocked("b.o.b.a.o"));
        Assert.True(filter.IsBlocked("que merda"));
        Assert.False(filter.IsBlocked("xingamentos")); // a whole word only
        Assert.False(filter.IsBlocked("pastor de verdade e falso nao"));
        Assert.False(filter.IsBlocked("palavras da igreja")); // the comment is not a word
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public void Accents_in_the_file_and_in_the_text_are_ignored()
    {
        File.WriteAllText(FilePath, "Cora\U000000E7\U000000E3o\n", new UTF8Encoding(false));
        var filter = Filter();

        Assert.True(filter.IsBlocked("meu coracao"));
        Assert.True(filter.IsBlocked("Cora\U000000E7\U000000E3o"));
    }

    [Fact]
    public void A_file_with_a_byte_order_mark_is_read()
    {
        File.WriteAllText(FilePath, "xingamento\n", new UTF8Encoding(true));

        Assert.True(Filter().IsBlocked("xingamento"));
    }

    [Fact]
    public void The_file_is_read_again_when_it_changes_and_dropped_when_it_goes_away()
    {
        var filter = Filter();
        Assert.False(filter.IsBlocked("xingamento"));

        File.WriteAllText(FilePath, "xingamento\n");
        File.SetLastWriteTimeUtc(FilePath, new DateTime(2026, 10, 25, 9, 0, 0, DateTimeKind.Utc));
        Assert.True(filter.IsBlocked("xingamento"));

        File.WriteAllText(FilePath, "outra\n");
        File.SetLastWriteTimeUtc(FilePath, new DateTime(2026, 10, 25, 9, 5, 0, DateTimeKind.Utc));
        Assert.False(filter.IsBlocked("xingamento"));
        Assert.True(filter.IsBlocked("outra"));

        File.Delete(FilePath);
        Assert.False(filter.IsBlocked("outra"));
        Assert.True(filter.IsBlocked("porra"));
    }

    [Fact]
    public void The_file_is_not_read_again_while_it_stays_the_same()
    {
        File.WriteAllText(FilePath, "xingamento\n");
        var stamp = new DateTime(2026, 10, 25, 9, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(FilePath, stamp);
        var filter = Filter();
        Assert.True(filter.IsBlocked("xingamento"));

        // Same last-write time, new content: not noticed (the stamp is the signal, as promised).
        File.WriteAllText(FilePath, "outra\n");
        File.SetLastWriteTimeUtc(FilePath, stamp);

        Assert.True(filter.IsBlocked("xingamento"));
        Assert.False(filter.IsBlocked("outra"));
    }

    [Fact]
    public void A_file_that_is_open_in_another_program_is_still_read()
    {
        File.WriteAllText(FilePath, "xingamento\n");
        using var editor = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        Assert.True(Filter().IsBlocked("xingamento"));
    }

    [Fact]
    public void A_corrupt_file_never_throws_and_keeps_the_built_ins()
    {
        var garbage = new byte[4096];
        new Random(7).NextBytes(garbage);
        File.WriteAllBytes(FilePath, garbage);
        var filter = Filter();

        Assert.False(filter.IsBlocked("culto de domingo"));
        Assert.True(filter.IsBlocked("que merda"));
    }

    [Fact]
    public void A_file_that_cannot_be_read_logs_one_line_without_any_word_and_keeps_the_built_ins()
    {
        using var locked = new FileStream(FilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None); // nobody else may read it
        var filter = Filter();

        Assert.False(filter.IsBlocked("culto de domingo"));
        Assert.True(filter.IsBlocked("que merda"));
        Assert.True(filter.IsBlocked("porra"));

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("palavras-bloqueadas.txt", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("merda", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Huge_lines_and_huge_files_are_bounded()
    {
        var lines = new List<string> { new string('a', 5000), "xingamento" };
        lines.AddRange(Enumerable.Range(0, 5000).Select(i => "palavra" + i));
        File.WriteAllLines(FilePath, lines);
        var filter = Filter();

        Assert.True(filter.IsBlocked("xingamento"));
        Assert.False(filter.IsBlocked(new string('a', 5000)));
    }
}
