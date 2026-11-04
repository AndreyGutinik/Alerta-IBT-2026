namespace AlertaIBT.Core.Temple;

/// <summary>
/// One function of the app that the "Templo menor" mode can switch off (the smaller temple has its own PC and does not use everything).
/// A function that is off really stops (services, ports, hotkeys, timers) and disappears from the interface.
/// </summary>
public enum TempleFeature
{
    /// <summary>Avisos: the bar on the LED, Telegram, the site connector, the queue and the history (the whole Inicio tab).</summary>
    Alerts,

    /// <summary>Player de musicas e despertadores.</summary>
    Player,

    /// <summary>Volumes ao iniciar e saidas de audio (the bottom row of the Som tab).</summary>
    StartupAudio,

    /// <summary>NDI / camera no telao.</summary>
    Ndi,

    /// <summary>Controle pela MA2 / Art-Net.</summary>
    Ma2,

    /// <summary>Controle do Holyrics: slides, video, logo, cronometro, Modo apresentacao.</summary>
    Holyrics,

    /// <summary>Gemini / Correcao (the offline corrector keeps working when the alerts stay on).</summary>
    Gemini,
}

/// <summary>The stable names (settings file, API) and the order of the <see cref="TempleFeature"/> values.</summary>
public static class TempleFeatures
{
    /// <summary>Every feature, in the order the dialog lists them.</summary>
    public static IReadOnlyList<TempleFeature> All { get; } =
        [TempleFeature.Alerts, TempleFeature.Player, TempleFeature.StartupAudio, TempleFeature.Ndi, TempleFeature.Ma2, TempleFeature.Holyrics, TempleFeature.Gemini];

    /// <summary>The name written to <c>settings.json</c> and shown by the API.</summary>
    public static string Key(TempleFeature feature) => feature switch
    {
        TempleFeature.Alerts => "alerts",
        TempleFeature.Player => "player",
        TempleFeature.StartupAudio => "audio",
        TempleFeature.Ndi => "ndi",
        TempleFeature.Ma2 => "ma2",
        TempleFeature.Holyrics => "holyrics",
        TempleFeature.Gemini => "gemini",
        _ => throw new ArgumentOutOfRangeException(nameof(feature)),
    };

    /// <summary>The feature with this name (ignoring case), or false.</summary>
    public static bool TryParse(string? key, out TempleFeature feature)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(Key(candidate), key?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                feature = candidate;
                return true;
            }
        }

        feature = default;
        return false;
    }

    /// <summary>The name shown to people (pt-BR).</summary>
    public static string Title(TempleFeature feature) => feature switch
    {
        TempleFeature.Alerts => "Avisos",
        TempleFeature.Player => "Player e despertadores",
        TempleFeature.StartupAudio => "Volumes e sa\U000000EDdas de \U000000E1udio",
        TempleFeature.Ndi => "NDI / c\U000000E2mera no tel\U000000E3o",
        TempleFeature.Ma2 => "Controle pela MA2",
        TempleFeature.Holyrics => "Controle do Holyrics",
        TempleFeature.Gemini => "Gemini / Corre\U000000E7\U000000E3o",
        _ => string.Empty,
    };

    /// <summary>What turning the function off stops, in a few words (the tooltip of the dialog and the confirmation list).</summary>
    public static string Detail(TempleFeature feature) => feature switch
    {
        TempleFeature.Alerts => "A barra no LED, o Telegram, o site, a fila e o hist\U000000F3rico. Esconde a aba In\U000000EDcio.",
        TempleFeature.Player => "A m\U000000FAsica, os despertadores, as teclas de m\U000000EDdia e a medi\U000000E7\U000000E3o de volume das m\U000000FAsicas.",
        TempleFeature.StartupAudio => "Os volumes ao iniciar, a trava da sa\U000000EDda padr\U000000E3o e a sa\U000000EDda do Holyrics.",
        TempleFeature.Ndi => "A c\U000000E2mera no tel\U000000E3o, os receptores NDI e os atalhos F1-F4 e Ctrl+Espa\U000000E7o. Esconde a aba NDI.",
        TempleFeature.Ma2 => "A porta Art-Net da grandMA2.",
        TempleFeature.Holyrics => "Slides, v\U000000EDdeo, logo, cron\U000000F4metro, modo apresenta\U000000E7\U000000E3o e as setas do painel r\U000000E1pido.",
        TempleFeature.Gemini => "As chamadas ao Gemini e a se\U000000E7\U000000E3o Corre\U000000E7\U000000E3o. O corretor offline continua se os avisos ficarem ligados.",
        _ => string.Empty,
    };
}
