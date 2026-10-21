using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Correction;
using AlertaIBT.Core.Services;

namespace AlertaIBT.Core.Tests.Services;

// "video aberto" in a parking notice is a "vidro aberto": the built-in rules fix it on every path.
public class AlertServiceVideoTests
{
    private const string Raw = "#fox prata abc1d54 video aberto";
    private const string Expected = "FOX PRATA | PLACA: ABC-1D54 | VIDRO ABERTO";

    private static ServiceHarness Quiet(
        string userRules = "",
        Func<string, CancellationToken, Task<ParsedAlert?>>? gemini = null)
    {
        var rules = new FixedRulesProvider(() => userRules).Current();
        var h = new ServiceHarness(gemini, start: false, rules: rules);
        h.Display.SetLed(false);
        h.Service.Start();
        return h;
    }

    [Fact]
    public async Task Offline_video_aberto_becomes_vidro_aberto()
    {
        using var h = Quiet(); // Gemini is down

        var outcome = await h.Pc(Raw);

        Assert.Equal(SubmitKind.Queued, outcome.Kind);
        Assert.Equal(Expected, outcome.Text);
        Assert.Equal(CorrectorKind.Offline, outcome.By);
        Assert.True(outcome.Changed);
    }

    [Fact]
    public async Task With_Gemini_ok_video_aberto_becomes_vidro_aberto_too()
    {
        var ai = new ParsedAlert("Fox", "Prata", "ABC-1D54", "video aberto", "Fox Prata ABC-1D54 video aberto");
        using var h = Quiet(gemini: (_, _) => Task.FromResult<ParsedAlert?>(ai));

        var outcome = await h.Pc(Raw);

        Assert.Equal(Expected, outcome.Text);
        Assert.Equal(CorrectorKind.Gemini, outcome.By);
    }

    [Fact]
    public async Task A_user_rule_for_the_same_words_wins_on_every_path()
    {
        const string rule = "video aberto => tela aberta";
        const string expected = "FOX PRATA | PLACA: ABC-1D54 | TELA ABERTA";
        var ai = new ParsedAlert("Fox", "Prata", "ABC-1D54", "video aberto", "Fox Prata ABC-1D54 video aberto");

        using var offline = Quiet(rule);
        using var online = Quiet(rule, (_, _) => Task.FromResult<ParsedAlert?>(ai));

        Assert.Equal(expected, (await offline.Pc(Raw)).Text);
        Assert.Equal(expected, (await online.Pc(Raw)).Text);
    }

    [Fact]
    public async Task The_preview_shows_vidro_aberto_as_well()
    {
        using var h = Quiet();

        var preview = await h.Service.PreviewAsync(Raw, SenderRole.Operator, TestContext.Current.CancellationToken);

        Assert.Equal(Expected, preview.Text);
    }

    [Fact]
    public async Task A_telegram_member_gets_the_same_text()
    {
        using var h = Quiet();

        var outcome = await h.Telegram(Raw);

        Assert.Equal(Expected, outcome.Text);
    }
}
