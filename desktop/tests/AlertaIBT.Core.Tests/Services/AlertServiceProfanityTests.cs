using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Correction;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Telegram;

namespace AlertaIBT.Core.Tests.Services;

// The profanity filter inside the service: whoever sends it and whether or not Gemini is up, nothing vulgar is queued.
public sealed class AlertServiceProfanityTests : IDisposable
{
    private const string Vulgar = "vai tomar no cu seu idiota";
    private const string Clean = "corolla prata abc1d23 vidro aberto";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "alertaibt-svc-profanity-" + Guid.NewGuid().ToString("N"));

    public AlertServiceProfanityTests()
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
            // Harmless.
        }
    }

    private static ServiceHarness Quiet(
        Func<string, CancellationToken, Task<ParsedAlert?>>? gemini = null,
        ProfanityFilter? filter = null,
        FixedRules? rules = null)
    {
        var h = new ServiceHarness(gemini, start: false, filter: filter, rules: rules);
        h.Display.SetLed(false);
        h.Service.Start();
        return h;
    }

    private static Task<ParsedAlert?> GeminiSays(ParsedAlert alert) => Task.FromResult<ParsedAlert?>(alert);

    private static void AssertRefusedAndNothingQueued(ServiceHarness h, SubmitOutcome outcome, string raw)
    {
        Assert.Equal(SubmitKind.Refused, outcome.Kind);
        Assert.Equal(string.Empty, outcome.Text);
        Assert.Empty(h.Queue.Pending);
        var entry = Assert.Single(h.Queue.History);
        Assert.Equal(HistoryState.Rejected, entry.State);
        Assert.Equal(HistoryReasons.ImproperContent, entry.Reason);
        Assert.Equal(raw, entry.Item.Original);
    }

    [Fact]
    public async Task Telegram_member_with_Gemini_offline_is_refused_without_asking_anybody_and_is_told_the_usual_reason()
    {
        using var h = Quiet(); // Gemini returns null: it is down

        var outcome = await h.Telegram(Vulgar);

        AssertRefusedAndNothingQueued(h, outcome, Vulgar);
        Assert.Equal([TelegramReplies.Refused], h.Notifier.Sent);
        Assert.Equal(0, h.Gemini.Calls);
        Assert.Equal(0, h.Offline.Calls);
    }

    [Fact]
    public async Task The_reply_text_is_the_existing_improper_content_refusal()
    {
        using var h = Quiet();

        await h.Telegram(Vulgar);

        Assert.Equal("\U0000274C Aviso n\U000000E3o foi para o tel\U000000E3o: conte\U000000FAdo impr\U000000F3prio.", Assert.Single(h.Notifier.Sent));
    }

    [Fact]
    public async Task Telegram_member_with_Gemini_online_is_refused_before_Gemini_is_asked()
    {
        using var h = Quiet((_, _) => GeminiSays(new ParsedAlert("Corolla", "Prata", "ABC-1D23", "vidro aberto", "Corolla Prata ABC-1D23 vidro aberto")));

        var outcome = await h.Telegram("corolla prata abc1d23 porra");

        AssertRefusedAndNothingQueued(h, outcome, "corolla prata abc1d23 porra");
        Assert.Equal(0, h.Gemini.Calls);
    }

    [Theory]
    [InlineData(SenderRole.Operator, AlertSource.Pc)]
    [InlineData(SenderRole.Admin, AlertSource.Site)]
    public async Task The_PC_operator_and_the_site_administrator_are_filtered_too(SenderRole role, AlertSource source)
    {
        using var h = Quiet();

        var outcome = await h.Service.SubmitAsync(Vulgar, source, role, false, null, TestContext.Current.CancellationToken);

        AssertRefusedAndNothingQueued(h, outcome, Vulgar);
        Assert.Empty(h.Notifier.Sent); // only the Telegram group is answered
        Assert.Equal(0, h.Gemini.Calls);
    }

    [Fact]
    public async Task The_filter_works_with_the_correction_switched_off()
    {
        using var h = Quiet();
        h.Settings = new AppSettings { Correction = new CorrectionSettings { Enabled = false } };

        var outcome = await h.Pc("gol branco abc1234 caralho");

        AssertRefusedAndNothingQueued(h, outcome, "gol branco abc1234 caralho");
    }

    [Fact]
    public async Task A_disguised_word_is_caught_too()
    {
        using var h = Quiet();

        var outcome = await h.Telegram("gol branco abc1d23 f d p");

        Assert.Equal(SubmitKind.Refused, outcome.Kind);
        Assert.Empty(h.Queue.Pending);
    }

    [Fact]
    public async Task A_plate_that_spells_a_listed_word_is_not_a_reason_to_refuse()
    {
        using var h = Quiet();

        var outcome = await h.Telegram("gol branco FDP1A23 farol aceso");

        Assert.Equal(SubmitKind.Queued, outcome.Kind);
        Assert.Equal("GOL BRANCO | PLACA: FDP-1A23 | FAROL ACESO", outcome.Text);
    }

    [Fact]
    public async Task Raw_text_clean_but_Gemini_output_vulgar_is_refused_after_the_correction()
    {
        // "bosa" is one edit from "bosta": the safety lock lets Gemini fix it, and the final text is what gets filtered.
        var ai = new ParsedAlert(string.Empty, string.Empty, null, string.Empty, "Pais do Pedro, so bosta");
        using var h = Quiet((_, _) => GeminiSays(ai));

        var outcome = await h.Telegram("pais do pedro, so bosa");

        AssertRefusedAndNothingQueued(h, outcome, "pais do pedro, so bosa");
        Assert.Equal(1, h.Gemini.Calls); // the raw text was clean, so Gemini was asked
        Assert.Equal([TelegramReplies.Refused], h.Notifier.Sent);
    }

    [Fact]
    public async Task A_fixed_rule_that_brings_in_a_word_is_caught_in_the_final_text()
    {
        using var h = Quiet(rules: FixedRules.Parse("carro => merda"));

        var outcome = await h.Pc("carro prata abc1d23 farol aceso");

        AssertRefusedAndNothingQueued(h, outcome, "carro prata abc1d23 farol aceso");
    }

    [Fact]
    public async Task Preview_of_vulgar_text_is_refused_without_asking_Gemini_and_writes_nothing()
    {
        using var h = Quiet();

        var preview = await h.Service.PreviewAsync(Vulgar, SenderRole.Operator, TestContext.Current.CancellationToken);

        Assert.True(preview.Refused);
        Assert.Equal(RuleKind.Reject, preview.Kind);
        Assert.Equal(string.Empty, preview.Text);
        Assert.Equal(0, h.Gemini.Calls);
        Assert.Empty(h.Queue.Pending);
        Assert.Empty(h.Queue.History);
        Assert.Empty(h.Notifier.Sent);
    }

    [Fact]
    public async Task Preview_of_a_rule_that_brings_in_a_word_is_refused()
    {
        using var h = Quiet(rules: FixedRules.Parse("carro => merda"));

        var preview = await h.Service.PreviewAsync("carro prata abc1d23", SenderRole.Operator, TestContext.Current.CancellationToken);

        Assert.True(preview.Refused);
        Assert.Equal(string.Empty, preview.Text);
    }

    [Fact]
    public async Task Preview_of_clean_text_is_not_refused()
    {
        using var h = Quiet();

        var preview = await h.Service.PreviewAsync(Clean, SenderRole.Operator, TestContext.Current.CancellationToken);

        Assert.False(preview.Refused);
        Assert.Equal("COROLLA PRATA | PLACA: ABC-1D23 | VIDRO ABERTO", preview.Text);
    }

    [Fact]
    public async Task The_log_says_the_filter_blocked_a_message_with_the_source_and_sender_but_never_the_text()
    {
        using var h = Quiet();

        await h.Telegram(Vulgar);

        var lines = h.Logger.Entries.Select(e => e.Message).ToArray();
        var line = Assert.Single(lines, l => l.Contains("Filtro", StringComparison.Ordinal));
        Assert.Contains("Telegram", line, StringComparison.Ordinal);
        Assert.Contains("42", line, StringComparison.Ordinal);
        foreach (var each in lines)
        {
            Assert.DoesNotContain("idiota", each, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("tomar", each, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Words_of_the_extra_file_are_refused_too_and_the_file_is_picked_up_when_it_changes()
    {
        var file = Path.Combine(_dir, ProfanityFilter.ExtraWordsFileName);
        using var h = Quiet(filter: new ProfanityFilter(file));

        var before = await h.Pc("favor retirar o carro, seu palhaco");
        Assert.Equal(SubmitKind.Queued, before.Kind);

        File.WriteAllText(file, "# da igreja\npalhaco\n");
        File.SetLastWriteTimeUtc(file, new DateTime(2026, 10, 25, 9, 0, 0, DateTimeKind.Utc));
        var after = await h.Pc("favor retirar o carro, seu palhaco");

        Assert.Equal(SubmitKind.Refused, after.Kind);
        Assert.Equal(HistoryReasons.ImproperContent, Assert.Single(h.Queue.History).Reason);
    }

    [Fact]
    public async Task A_missing_extra_file_changes_nothing_for_normal_alerts()
    {
        var file = Path.Combine(_dir, "nao-existe.txt");
        using var h = Quiet(filter: new ProfanityFilter(file));

        var outcome = await h.Telegram(Clean);

        Assert.Equal(SubmitKind.Queued, outcome.Kind);
        Assert.Single(h.Queue.Pending);
    }

    [Fact]
    public async Task Clean_alerts_are_unaffected_and_still_reach_the_queue()
    {
        using var h = Quiet();

        var outcome = await h.Telegram(Clean);

        Assert.Equal(SubmitKind.Queued, outcome.Kind);
        Assert.Equal("COROLLA PRATA | PLACA: ABC-1D23 | VIDRO ABERTO", outcome.Text);
        Assert.Empty(h.Queue.History);
    }
}
