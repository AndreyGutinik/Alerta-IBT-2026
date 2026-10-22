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
    ];

    public static readonly string[] MustPass =
    [
        "Pinto Silva", "pinto", "Seu Pinto", "Seu Pinto Silva chegou", "esse Pinto Silva", "o pinto amarelinho", "Pinto",
        "Joao Pinto", "Bunda", "bunda", "Rola Moca", "Rola Mo\U000000E7a", "Rua Rola Moca", "a rola", "uma rola no telhado",
        "o que rola hoje", "isso rola amanha", "pau", "pau de selfie", "meu pau de selfie", "meu pau de sebo",
        "pau-brasil", "meu pau brasil", "pica-pau", "pica pau", "Pica-Pau amarelo", "picape branca", "picape", "pick-up branca",
        "pickup prata abc1d23", "picar o papel", "picada na trilha", "Pico do Jaragua", "pico de luz", "picanha na cantina",
        "pikachu", "Picasso", "Picazo", "Pica Pau", "Costa e Silva", "Costa e Silva favor comparecer", "peito", "dor no peito",
        "peitos de frango", "tetas", "teta", "bundas", "sala de amamentacao", "colher de pau", "pomba rola", "rolar", "rolamento",
        "pinto de ouro", "jebediah", "bilhete", "colheita", "colhedor", "cocktail", "coca", "cockpit", "peacock", "tit",
        "milfolhas", "pornhubs", "tarde", "retardo", "retarda",
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
