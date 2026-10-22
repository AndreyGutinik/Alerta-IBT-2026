using AlertaIBT.Core.Correction;

namespace AlertaIBT.Core.Tests.Correction;

// The slang the owner asked to block (pica, pika) and the words added after reading public Portuguese word lists.
// Source kept ASCII-only.
public class ProfanityFilterSlangTests
{
    public static readonly string[] NewWords =
    [
        "pica", "picas", "pika", "pikas", "jeba", "bilau", "colhao", "colhoes", "pepeca", "priquito", "boceta", "bocetinha",
        "chochota", "xochota", "xoxotinha", "bundao", "bundona", "bundinha", "caceta", "fudeu", "fude", "fuder", "fudido",
        "fudida", "fudendo", "fudase", "fodasse", "bostao", "cuzinhos", "cuzoes", "safadinha", "safadinho",
        "safadeza", "queca", "trepada", "ejacular", "ejaculacao", "xvideos", "pornhub", "xnxx", "cock", "cocksucker", "tits",
        "boobs", "milf", "wanker", "twat", "dildo", "blowjob", "horny", "retard", "retarded", "debiloide",
    ];

    public static readonly string[] MustBlock =
    [
        "minha pika", "minha pica", "pica", "PIKA", "p1ka", "p.i.c.a", "minha pica grande", "chupa minha pica", "picaaa",
        "pikaaa", "P I C A", "p!ca", "pica dura",
        "meu pau", "chupa pau", "chupa meu pau", "seu pau", "essa rola", "minha rola", "chupa rola", "meu pinto", "minha pinto",
        "chupa pinto", "minha bunda", "sua bunda", "meus peitos", "minhas tetas", "seus peitos", "minha teta", "meu piru",
        "meu p4u", "m1nha r0la", "chupa o meu pau", "Meu Pau.",
        "seu pinto", "esse pinto", "este pinto", "o pinto", "meu pinto", "teu pinto", "Seu Pinto", "o Pinto Silva", "esse p1nto", "s3u pinto",
        "meu pau de selfie", "meu pau de sebo", "meu pau brasil", "minha pau", "seu pau de selfie", "sua pau", "tua pau", "teu pau", "meus paus", "m3u pau de selfie",
        "a rola", "minha rola", "sua rola", "as rolas", "a r0la", "o carro e a rola",
        "pi.ca", "pi-ca", "pi_ca", "p*ca", "pi ca", "pi  ca", "PI CA", "pi.ka", "pi-kaa", "pu ta", "pu.ta", "fo-da", "bi-cha", "mer-da", "por-ra", "carro prata abc1d23 pi.ca", "pi-ca grande", "pi\U000000A0ca",
    ];

    public static readonly string[] MustPass =
    [
        "Pinto Silva", "pinto", "Pinto Coelho", "Pinto Silva chegou", "Dr Pinto Coelho", "Pinto",
        "Joao Pinto", "Bunda", "bunda", "Rola Moca", "Rola Mo\U000000E7a", "Rua Rola Moca", "a Rola Moca", "uma rola no telhado",
        "o que rola hoje", "isso rola amanha", "pau", "pau de selfie", "pau de sebo",
        "pau-brasil", "pica-pau", "pica pau", "Pica-Pau amarelo", "picape branca", "picape", "pick-up branca",
        "pickup prata abc1d23", "picar o papel", "picada na trilha", "Pico do Jaragua", "pico de luz", "picanha na cantina",
        "pikachu", "Picasso", "Picazo", "Pica Pau", "Costa e Silva", "Costa e Silva favor comparecer", "peito", "dor no peito",
        "peitos de frango", "tetas", "teta", "bundas", "sala de amamentacao", "colher de pau", "pomba rola", "rolar", "rolamento",
        "pinto de ouro", "jebediah", "bilhete", "colheita", "colhedor", "cocktail", "coca", "cockpit", "peacock", "tit",
        "milfolhas", "pornhubs", "tarde", "retardo", "retarda",
        "os pintos da granja", "um pinto amarelinho", "pintos e galinhas", "cor no carro", "cor-no carro", "via do portao", "via-do portao",
        "pi cado", "pi cas", "pi-cas-pau", "po ra", "vai do", "com ela", "pi", "ca", "pi-ca-pau", "pica-pau pi ca pau",
    ];

    public static TheoryData<string> NewWordsPlain() => new(NewWords);

    public static TheoryData<string> NewWordsDisguised() => new(NewWords.Select(w => "carro prata abc1d23 " + w.ToUpperInvariant() + "!!"));

    public static TheoryData<string> MustBlockData() => new(MustBlock);

    public static TheoryData<string> MustPassData() => new(MustPass);

    [Theory]
    [MemberData(nameof(NewWordsPlain))]
    public void New_word_is_blocked(string text) => Assert.True(ProfanityFilter.IsImproper(text), text);

    [Theory]
    [MemberData(nameof(NewWordsDisguised))]
    public void New_word_is_blocked_in_a_sentence_in_capitals(string text) => Assert.True(ProfanityFilter.IsImproper(text), text);

    [Theory]
    [MemberData(nameof(MustBlockData))]
    public void Slang_is_blocked(string text) => Assert.True(ProfanityFilter.IsImproper(text), text);

    [Theory]
    [MemberData(nameof(MustPassData))]
    public void Ordinary_use_passes(string text) => Assert.False(ProfanityFilter.IsImproper(text), text);
}
