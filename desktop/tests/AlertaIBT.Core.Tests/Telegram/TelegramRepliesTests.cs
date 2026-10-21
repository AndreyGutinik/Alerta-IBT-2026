using AlertaIBT.Core.Telegram;

namespace AlertaIBT.Core.Tests.Telegram;

public class TelegramRepliesTests
{
    private const string Check = "\U00002705";
    private const string Cross = "\U0000274C";
    private const string Warning = "\U000026A0\U0000FE0F";
    private const string Hourglass = "\U000023F3";
    private const string HourglassDone = "\U0000231B";
    private const string Pause = "\U000023F8\U0000FE0F";

    [Fact]
    public void Shown_PutsTheShownTextOnTheSecondLine()
    {
        Assert.Equal(
            Check + " Mensagem exibida no tel\U000000E3o.\nGOL BRANCO | PLACA: ABC-1D23 | VIDRO ABERTO",
            TelegramReplies.Shown("GOL BRANCO | PLACA: ABC-1D23 | VIDRO ABERTO"));
    }

    [Fact]
    public void Missing_ListsASingleField()
    {
        Assert.Equal(
            Cross + " Aviso n\U000000E3o foi para o tel\U000000E3o. Faltou placa. Mande assim: #corolla prata abc1d23 vidro aberto",
            TelegramReplies.Missing(["placa"]));
    }

    [Fact]
    public void Missing_JoinsTwoFieldsWithE()
    {
        Assert.Equal(
            Cross + " Aviso n\U000000E3o foi para o tel\U000000E3o. Faltou cor e placa. Mande assim: #corolla prata abc1d23 vidro aberto",
            TelegramReplies.Missing(["cor", "placa"]));
    }

    [Fact]
    public void Missing_JoinsThreeFieldsWithCommaAndE()
    {
        Assert.Equal(
            Cross + " Aviso n\U000000E3o foi para o tel\U000000E3o. Faltou modelo, cor e placa. Mande assim: #corolla prata abc1d23 vidro aberto",
            TelegramReplies.Missing(["modelo", "cor", "placa"]));
    }

    [Fact]
    public void Missing_EmptyListThrows()
    {
        Assert.Throws<ArgumentException>(() => TelegramReplies.Missing([]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_NullOrBlankFieldThrows(string? blank)
    {
        Assert.Throws<ArgumentException>(() => TelegramReplies.Missing(["modelo", blank!, "placa"]));
        Assert.Throws<ArgumentException>(() => TelegramReplies.Missing([blank!]));
    }

    [Fact]
    public void Missing_NullThrows()
    {
        Assert.Throws<ArgumentNullException>(() => TelegramReplies.Missing(null!));
    }

    [Fact]
    public void DuplicateQueued_IsExact()
    {
        Assert.Equal(Warning + " Esse carro j\U000000E1 est\U000000E1 na fila.", TelegramReplies.DuplicateQueued);
    }

    [Theory]
    [InlineData(7, 7)]
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    public void DuplicateShown_ShowsAtLeastOneMinute(int minutes, int shown)
    {
        Assert.Equal(
            Warning + " Esse carro j\U000000E1 passou h\U000000E1 " + shown + " min.",
            TelegramReplies.DuplicateShown(minutes));
    }

    [Fact]
    public void TooLong_IsExactAndNamesTheLimit()
    {
        Assert.Equal(
            Cross + " Aviso n\U000000E3o foi para o tel\U000000E3o. Ficou grande demais (m\U000000E1ximo 200 letras).",
            TelegramReplies.TooLong(200));
        Assert.Contains("(m\U000000E1ximo 150 letras)", TelegramReplies.TooLong(150));
    }

    [Fact]
    public void LedOff_IsExact()
    {
        Assert.Equal(
            Hourglass + " Tel\U000000E3o desligado. O aviso est\U000000E1 na fila e passa quando ele ligar.",
            TelegramReplies.LedOff);
    }

    [Fact]
    public void Expired_IsExact()
    {
        Assert.Equal(HourglassDone + " O aviso expirou sem passar no tel\U000000E3o.", TelegramReplies.Expired);
    }

    [Fact]
    public void Paused_IsExact()
    {
        Assert.Equal(
            Warning + " O pastor iniciou a prega\U000000E7\U000000E3o. O overlay foi pausado para n\U000000E3o atrapalhar o culto.",
            TelegramReplies.Paused);
    }

    [Fact]
    public void PausedRefused_IsExact()
    {
        Assert.Equal(
            Pause + " Prega\U000000E7\U000000E3o em andamento: o tel\U000000E3o n\U000000E3o est\U000000E1 mostrando avisos.",
            TelegramReplies.PausedRefused);
    }
}
