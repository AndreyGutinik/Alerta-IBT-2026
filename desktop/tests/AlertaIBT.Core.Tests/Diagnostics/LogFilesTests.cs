using System.Text;
using AlertaIBT.Core.Diagnostics;

namespace AlertaIBT.Core.Tests.Diagnostics;

public sealed class LogFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alerta-ibt-logs-" + Guid.NewGuid().ToString("N"));

    public LogFilesTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string Write(string name, params string[] lines)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        return path;
    }

    private static string Line(string time, string level, string message) => $"2026-10-20 {time} [{level}] Test: {message}";

    [Fact]
    public void The_newest_entries_come_first_across_files_and_stop_at_the_limit()
    {
        Write("alertaibt-20261019.log", Line("23:00:00.000", "INF", "ontem 1"), Line("23:30:00.000", "INF", "ontem 2"));
        Write("alertaibt-20261020.log", Line("08:00:00.000", "INF", "hoje 1"), Line("09:00:00.000", "WRN", "hoje 2"));
        var logs = new LogFiles(_dir);

        var all = logs.Newest();
        var two = logs.Newest(2);

        Assert.Equal(["hoje 2", "hoje 1", "ontem 2", "ontem 1"], all.Select(e => e.Text[(e.Text.IndexOf(": ", StringComparison.Ordinal) + 2)..]));
        Assert.Equal(2, two.Count);
        Assert.Contains("hoje 2", two[0].Text);
    }

    [Fact]
    public void Severity_is_read_from_the_level_tag()
    {
        Write(
            "alertaibt-20261020.log",
            Line("08:00:00.000", "INF", "a"),
            Line("08:00:01.000", "WRN", "b"),
            Line("08:00:02.000", "ERR", "c"),
            Line("08:00:03.000", "FTL", "d"),
            Line("08:00:04.000", "DBG", "e"));

        var severities = new LogFiles(_dir).Newest().Select(e => e.Severity).Reverse().ToList();

        Assert.Equal([LogSeverity.Info, LogSeverity.Warning, LogSeverity.Error, LogSeverity.Error, LogSeverity.Other], severities);
    }

    [Fact]
    public void The_lines_of_an_exception_belong_to_the_entry_above_them()
    {
        Write(
            "alertaibt-20261020.log",
            Line("08:00:00.000", "ERR", "falhou"),
            "System.InvalidOperationException: boom",
            "   at Something.Run()",
            Line("08:00:01.000", "INF", "depois"));

        var entries = new LogFiles(_dir).Newest();

        Assert.Equal(2, entries.Count);
        Assert.Contains("depois", entries[0].Text);
        Assert.Equal(LogSeverity.Error, entries[1].Severity);
        Assert.Contains("boom", entries[1].Text);
        Assert.Contains("Something.Run", entries[1].Text);
    }

    [Fact]
    public void A_missing_folder_or_no_files_is_just_empty()
    {
        Assert.Empty(new LogFiles(Path.Combine(_dir, "nope")).Newest());
        Assert.Equal(string.Empty, new LogFiles(Path.Combine(_dir, "nope")).AllText());
        Assert.Empty(new LogFiles(_dir).Newest());
    }

    [Fact]
    public void Other_files_in_the_folder_are_not_logs()
    {
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "2026-10-20 08:00:00.000 [INF] not a log");
        Write("alertaibt-20261020.log", Line("08:00:00.000", "INF", "real"));

        var entries = new LogFiles(_dir).Newest();

        Assert.Single(entries);
        Assert.Contains("real", entries[0].Text);
    }

    [Fact]
    public void AllText_is_every_file_oldest_first_and_export_writes_it()
    {
        Write("alertaibt-20261019.log", Line("23:00:00.000", "INF", "ontem"));
        Write("alertaibt-20261020.log", Line("08:00:00.000", "INF", "hoje"));
        var logs = new LogFiles(_dir);

        var text = logs.AllText();
        var target = Path.Combine(_dir, "export.txt");
        logs.ExportTo(target);

        Assert.True(text.IndexOf("ontem", StringComparison.Ordinal) < text.IndexOf("hoje", StringComparison.Ordinal));
        Assert.Equal(text, File.ReadAllText(target));
    }

    [Fact]
    public void A_file_held_open_for_writing_by_the_logger_can_still_be_read_and_is_emptied_by_clear()
    {
        var held = Write("alertaibt-20261020.log", Line("08:00:00.000", "INF", "no arquivo aberto"));
        Write("alertaibt-20261019.log", Line("23:00:00.000", "INF", "velho"));
        using var logger = new FileStream(held, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var logs = new LogFiles(_dir);

        Assert.Equal(2, logs.Newest().Count);
        var touched = logs.Clear();

        Assert.Equal(2, touched);
        Assert.Empty(logs.Newest());
        Assert.False(File.Exists(Path.Combine(_dir, "alertaibt-20261019.log"))); // not held: deleted
        Assert.Equal(0, new FileInfo(held).Length); // held: emptied

        var line = Encoding.UTF8.GetBytes(Line("09:00:00.000", "INF", "depois de limpar") + "\n");
        logger.Write(line); // the logger keeps working
        logger.Flush();
        Assert.Single(logs.Newest());
    }

    [Fact]
    public void A_big_file_is_read_from_its_end_and_never_starts_with_half_an_entry()
    {
        var path = Path.Combine(_dir, "alertaibt-20261020.log");
        using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            for (var i = 0; i < 20000; i++)
            {
                writer.WriteLine(Line("08:00:00.000", "INF", $"linha {i:D6} " + new string('x', 60)));
            }
        }

        var entries = new LogFiles(_dir).Newest(100000);

        Assert.InRange(entries.Count, 1000, 19999);
        Assert.Contains("linha 019999", entries[0].Text);
        Assert.All(entries, e => Assert.StartsWith("2026-10-20 ", e.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void Accents_survive_the_trip()
    {
        Write("alertaibt-20261020.log", Line("08:00:00.000", "INF", "Servi\U000000E7o de avisos iniciado"));

        Assert.Contains("Servi\U000000E7o", new LogFiles(_dir).Newest()[0].Text);
    }
}
