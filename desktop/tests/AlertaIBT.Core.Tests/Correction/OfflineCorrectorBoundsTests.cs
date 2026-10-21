using System.Diagnostics;
using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Correction;

namespace AlertaIBT.Core.Tests.Correction;

// Unlimited senders (the operator) can type a lot; the offline corrector only works on the start of it.
public class OfflineCorrectorBoundsTests(OfflineCorrectorFixture fixture) : IClassFixture<OfflineCorrectorFixture>
{
    private readonly OfflineCorrector _corrector = fixture.Corrector;

    [Fact]
    public async Task AFiveThousandCharacterMessage_IsFast_AndItsTailIsLeftAsTyped()
    {
        var raw = string.Join(' ', Enumerable.Repeat("farrol", 700));
        Assert.True(raw.Length > 4500);

        var watch = Stopwatch.StartNew();
        var alert = await _corrector.CorrectAsync(raw, CancellationToken.None);
        watch.Stop();

        Assert.NotNull(alert);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
        var words = alert.FreeText.Split(' ');
        Assert.Equal(700, words.Length);
        Assert.Equal("FAROL", words[0]);
        Assert.Equal("FAROL", words[79]);
        Assert.All(words.Skip(80), word => Assert.Equal("FARROL", word));
    }

    [Fact]
    public async Task OnlyTheFirstTwoThousandCharactersAreCorrected()
    {
        var filler = new string('a', 49) + "1"; // a digit: left alone at once, so the test stays fast
        var raw = "farrol " + string.Join(' ', Enumerable.Repeat(filler, 42)) + " farrol";
        Assert.True(raw.Length > 2100);
        Assert.True(raw.LastIndexOf("farrol", StringComparison.Ordinal) > 2000);

        var alert = await _corrector.CorrectAsync(raw, CancellationToken.None);

        Assert.NotNull(alert);
        Assert.StartsWith("FAROL ", alert.FreeText, StringComparison.Ordinal);
        Assert.EndsWith(" FARROL", alert.FreeText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AShortMessage_IsStillFullyCorrected()
    {
        var alert = await _corrector.CorrectAsync("corolla prata abc1d23 vidro abrto", CancellationToken.None);

        Assert.NotNull(alert);
        Assert.Equal("VIDRO ABERTO", alert.Reason);
    }
}
