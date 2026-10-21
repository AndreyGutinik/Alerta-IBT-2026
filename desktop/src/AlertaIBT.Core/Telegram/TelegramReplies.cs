namespace AlertaIBT.Core.Telegram;

/// <summary>The exact texts the bot answers in the group.</summary>
public static class TelegramReplies
{
    /// <summary>The alert was accepted and is on the screen.</summary>
    /// <param name="text">The text that went to the screen.</param>
    public static string Shown(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return "\U00002705 Mensagem exibida no tel\U000000E3o.\n" + text;
    }

    /// <summary>The alert was refused because it lacks something required.</summary>
    /// <param name="campos">What it lacks, as <c>"modelo"</c>, <c>"cor"</c> and <c>"placa"</c>.</param>
    public static string Missing(IReadOnlyList<string> campos)
    {
        ArgumentNullException.ThrowIfNull(campos);
        if (campos.Count == 0)
        {
            throw new ArgumentException("At least one missing field is required.", nameof(campos));
        }

        if (campos.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A missing field cannot be null or blank.", nameof(campos));
        }

        return "\U0000274C Aviso n\U000000E3o foi para o tel\U000000E3o. Faltou " + JoinPortuguese(campos)
            + ". Mande assim: #corolla prata abc1d23 vidro aberto";
    }

    /// <summary>The correction refused the text as improper, so it never reached the screen.</summary>
    public const string Refused = "\U0000274C Aviso n\U000000E3o foi para o tel\U000000E3o: conte\U000000FAdo impr\U000000F3prio.";

    /// <summary>The alert is longer than a member may send.</summary>
    /// <param name="max">The most letters allowed.</param>
    public static string TooLong(int max)
    {
        return "\U0000274C Aviso n\U000000E3o foi para o tel\U000000E3o. Ficou grande demais (m\U000000E1ximo " + max + " letras).";
    }

    /// <summary>The same car is already waiting in the queue.</summary>
    public const string DuplicateQueued = "\U000026A0\U0000FE0F Esse carro j\U000000E1 est\U000000E1 na fila.";

    /// <summary>The same car was shown a moment ago.</summary>
    /// <param name="minutos">Minutes since it was shown; anything under one is told as one.</param>
    public static string DuplicateShown(int minutos)
    {
        return $"\U000026A0\U0000FE0F Esse carro j\U000000E1 passou h\U000000E1 {Math.Max(1, minutos)} min.";
    }

    /// <summary>The alert is queued, but the LED screen is off.</summary>
    public const string LedOff = "\U000023F3 Tel\U000000E3o desligado. O aviso est\U000000E1 na fila e passa quando ele ligar.";

    /// <summary>The alert waited too long and was dropped.</summary>
    public const string Expired = "\U0000231B O aviso expirou sem passar no tel\U000000E3o.";

    /// <summary>The pastor started the sermon and the overlay was paused.</summary>
    public const string Paused = "\U000026A0\U0000FE0F O pastor iniciou a prega\U000000E7\U000000E3o. O overlay foi pausado para n\U000000E3o atrapalhar o culto.";

    /// <summary>An alert arrived during the sermon and was refused.</summary>
    public const string PausedRefused = "\U000023F8\U0000FE0F Prega\U000000E7\U000000E3o em andamento: o tel\U000000E3o n\U000000E3o est\U000000E1 mostrando avisos.";

    /// <summary>Joins as Portuguese does: <c>a</c>; <c>a e b</c>; <c>a, b e c</c>.</summary>
    private static string JoinPortuguese(IReadOnlyList<string> items)
    {
        return items.Count == 1
            ? items[0]
            : string.Join(", ", items.Take(items.Count - 1)) + " e " + items[^1];
    }
}
