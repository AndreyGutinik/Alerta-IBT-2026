using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Correction;

namespace AlertaIBT.Core.Tests.Correction;

// Gemini may reorder a message typed out of order (model, color, plate, reason), but never lose the color or the plate and never
// add a word that was not typed. The lock compares words as a set, so the order does not matter to it.
public class SafetyLockReorderTests
{
    private sealed class FakeCorrector(ParsedAlert? answer) : ICorrector
    {
        public Task<ParsedAlert?> CorrectAsync(string raw, CancellationToken ct) => Task.FromResult(answer);
    }

    private static ParsedAlert Ai(string model, string color, string? plate, string reason) =>
        new(model, color, plate, reason, string.Join(' ', new[] { model, color, plate, reason }.Where(s => !string.IsNullOrEmpty(s))));

    private static Task<CorrectionResult> Run(string raw, ParsedAlert gemini)
    {
        var pipeline = new CorrectionPipeline(new FakeCorrector(gemini), new FakeCorrector(null), () => FixedRules.Empty, TimeSpan.FromSeconds(5));
        return pipeline.RunAsync(raw, true, CancellationToken.None);
    }

    [Fact]
    public void The_prompt_asks_for_the_reordering_with_the_four_examples()
    {
        var prompt = GeminiPrompt.System;

        Assert.Contains("fora de ordem", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("modelo, cor, placa, motivo", prompt, StringComparison.Ordinal);
        Assert.Contains("fiat abc1234 uno preto = modelo Fiat Uno, cor preto", prompt, StringComparison.Ordinal);
        Assert.Contains("Prata, Gol, ABC-1234 = modelo Gol, cor prata", prompt, StringComparison.Ordinal);
        Assert.Contains("gol farol aceso branco abc1234 = modelo Gol, cor branco, motivo farol aceso", prompt, StringComparison.Ordinal);
        Assert.Contains("a placa ABC1234 gol branco esta com farol aceso = modelo Gol, cor branco, motivo esta com farol aceso", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fiat abc1234 uno preto", "Fiat Uno", "Preto", "ABC-1234", "")]
    [InlineData("Prata, Gol, ABC-1234", "Gol", "Prata", "ABC-1234", "")]
    [InlineData("gol farol aceso branco abc1234", "Gol", "Branco", "ABC-1234", "farol aceso")]
    [InlineData("a placa ABC1234 gol branco esta com farol aceso", "Gol", "Branco", "ABC-1234", "esta com farol aceso")]
    public async Task A_faithful_reordering_passes_the_lock(string raw, string model, string color, string plate, string reason)
    {
        var result = await Run(raw, Ai(model, color, plate, reason));

        Assert.Equal(CorrectorKind.Gemini, result.By);
        Assert.Equal(model, result.Alert.Model);
        Assert.Equal(color, result.Alert.Color);
        Assert.Equal(plate, result.Alert.Plate);
    }

    [Theory]
    [InlineData("fiat abc1234 uno preto", "Fiat Palio", "Preto", "ABC-1234", "")]
    [InlineData("Prata, Gol, ABC-1234", "Gol", "Prata", "ABC-1234", "vidro aberto")]
    [InlineData("gol farol aceso branco abc1234", "Gol", "Branco", "ABC-1234", "farol aceso alarme disparando")]
    public async Task A_reordered_answer_with_an_invented_word_fails_the_lock(string raw, string model, string color, string plate, string reason)
    {
        var result = await Run(raw, Ai(model, color, plate, reason));

        Assert.Equal(CorrectorKind.Offline, result.By);
    }

    [Theory]
    [InlineData("fiat abc1234 uno preto", "Fiat Uno", "", "ABC-1234", "")]
    [InlineData("Prata, Gol, ABC-1234", "Gol", "", "ABC-1234", "")]
    [InlineData("gol farol aceso branco abc1234", "Gol", "Branco", null, "farol aceso")]
    [InlineData("Prata, Gol, ABC-1234", "Gol", "Preta", "ABC-1234", "")]
    public async Task A_reordered_answer_that_drops_the_color_or_the_plate_or_changes_the_color_fails_the_lock(
        string raw, string model, string color, string? plate, string reason)
    {
        var result = await Run(raw, Ai(model, color, plate, reason));

        Assert.Equal(CorrectorKind.Offline, result.By);
    }
}
