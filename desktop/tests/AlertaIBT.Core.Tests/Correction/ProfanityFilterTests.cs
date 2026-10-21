using AlertaIBT.Core.Correction;

namespace AlertaIBT.Core.Tests.Correction;

// Source kept ASCII-only: accents are written as \U escapes.
public class ProfanityFilterTests
{
    // Everything here is normal parking or church language (or a name, a place, a car) and must never be blocked.
    public static readonly string[] Clean =
    [
        "gol branco abc1d23 farol aceso",
        "polo prata abc1d23 vidro aberto",
        "pulse vermelho abc1d23 pisca alerta ligado",
        "toro preta abc1d23 estacionada na vaga de idoso",
        "uno vermelho abc1d23 porta aberta",
        "kicks branco abc1d23 alarme disparando",
        "up prata abc1d23 luz acesa",
        "fit cinza abc1d23 vidro aberto",
        "mobi azul abc1d23 farol aceso",
        "kwid laranja abc1d23 bloqueando saida",
        "fox prata abc1d54 video aberto",
        "civic preto abc1d23 vidro aberto",
        "fusca azul abc1d23 farol aceso",
        "carro com macaco no porta malas",
        "o macaco do carro caiu",
        "Pinto Silva favor comparecer a recepcao",
        "familia Santa Rita comparecer na recepcao",
        "Santa Rita",
        "Cuiab\U000000E1",
        "Cuiaba",
        "Cunha",
        "Escudo",
        "Cubatao",
        "Cubat\U000000E3o",
        "Rocha",
        "o computador do culto",
        "cuidado com a curva",
        "bucha de banho na cantina",
        "pica-pau",
        "pauta da reuniao",
        "pau de selfie",
        "a pomba rola no telhado",
        "gato preto na entrada",
        "vaca na estrada",
        "burro de carga",
        "galinha na cozinha",
        "cachorro solto no estacionamento",
        "o dono do carro prata placa abc1234 favor retirar",
        "escola dominical comeca as 9h",
        "ensaio do louvor as 19h",
        "culto de domingo as 18h",
        "batismo hoje",
        "vamos orar juntos",
        "Bem-vindos a igreja",
        "pais do pedro comparecer na recepcao",
        "sala das criancas lotada",
        "cantina aberta",
        "bolo de aniversario na cozinha",
        "banheiro feminino fechado",
        "cuzcuz na cantina",
        "deputado visita a igreja",
        "disputa de vagas no estacionamento",
        "reputacao e testemunho",
        "amputar nao e assunto",
        "veiculo no porao com farol aceso",
        "porta e portao abertos",
        "sexta-feira culto as 19h",
        "sexto andar",
        "mercado e merenda das criancas",
        "Corneta no louvor",
        "Corn\U000000E9lio Silva",
        "idioma ingles na escola",
        "otimo culto hoje",
        "viaduto e viagem",
        "missao na Mongolia",
        "bicho de pelucia perdido",
        "panelas na cozinha",
        "escritorio fechado",
        "Safira Rocha",
        "cunhado do pastor",
        "Gol branco FDP1A23",
        "Uno preto CRL-1234",
        "Corsa preto PUT1A23",
        "Atencao! Carro prata abc1d23 com farol aceso!",
        "*Aviso* carro prata abc1d23 farol aceso",
        "Cuidado!",
        "Up! prata abc1d23",
        "Bloco A B C",
        "Setor A, B e C",
        "Fila 1 2 3",
        "Honda CR-V prata abc1d23",
        "Citroen C4 branco abc1d23",
        "Hyundai i30 preto abc1d23",
        "Audi A3 cinza abc1d23",
        "BMW X1 azul abc1d23",
        "S10 branca abc1d23 farol aceso",
        "Jeep Compass cinza abc1d23",
        "T-Cross preto abc1d23",
        "HB20 prata abc1d23",
        "Corolla prata abc1d23 vidro aberto",
        "carro arrombado no estacionamento",
        "porta arrombada do carro prata abc1d23",
        "vidro do carro arrombado",
        "fds de louvor",
        "Maric\U000000E1 RJ",
        "Cu\U000000EDdado com o degrau",
        "R$ 50 de oferta",
        "mesa classe passagem massagem",
        "compasso computacao",
        "caramba que carro bonito",
        "Parauapebas Carajas",
        "analise e assistencia",
        "seu Joao C. U. Silva",
        "Foz do Iguacu",
        "Gr\U000000E3o-Mongol MG",
    ];

    // Vulgar, offensive, sexual, plain or disguised: all must be blocked.
    public static readonly string[] Improper =
    [
        "porra",
        "que porra e essa",
        "caralho",
        "vai tomar no cu",
        "filho da puta",
        "fdp",
        "vsf",
        "vtnc",
        "tnc",
        "tmnc",
        "pqp",
        "krl",
        "crl",
        "pnc",
        "merda",
        "que merda de culto",
        "bosta",
        "buceta",
        "xereca",
        "xota",
        "cuzao",
        "piroca",
        "viado",
        "bicha",
        "bichona",
        "sapatao",
        "paneleiro",
        "arrombado",
        "babaca",
        "otario",
        "imbecil",
        "idiota",
        "retardado",
        "mongol",
        "desgracado",
        "vagabundo",
        "vagabunda",
        "vadia",
        "safada",
        "safado",
        "corno",
        "cretino",
        "escroto",
        "sexo",
        "vamos transar",
        "punheta",
        "siririca",
        "porno",
        "nudes",
        "tarado",
        "fuck",
        "shit",
        "bitch",
        "dick",
        "pussy",
        "asshole",
        "wtf",
        "stfu",
        "foda-se",
        "fodase",
        "gol prata abc1d23 porra",
        "caralhooo",
        "fdddp",
        "p.o.r.r.a",
        "p o r r a",
        "p-o-r-r-a",
        "p*rra",
        "p0rr4",
        "m3rd4",
        "c4r4lh0",
        "put@",
        "f0d@",
        "$exo",
        "put4 que pariu",
        "PORRA!!!",
        "Caralho!",
        "CARALHO",
        "c.a.r.a.l.h.o",
        "b u c e t a",
        "v.s.f",
        "f d p",
        "FILHO DA PUTA",
        "filhodaputa",
        "vaitomarnocu",
        "p\U000000F4rra",
        "m\U000000E9rda",
        "id!ota",
        "1d10t4",
        "caraaaaalho",
        "meeerda",
        "puuuta",
        "arr0mbad0",
        "v1ado",
        "b1ch@",
        "n u d e s",
        "vadi4",
        "s4f4d0",
        "fucking",
        "motherfucker",
        "Fala porra, vai se foder",
        "seu cu",
        "tomar no cu",
        "cuz4o",
        "karalho",
        "cacete",
        "putaria",
        "puta merda",
        "veado",
        "estupro",
        "seu arrombado, saia daqui",
        "carro arrombado, seu arrombado",
        "seu mongol",
    ];

    public static TheoryData<string> CleanData() => new(Clean);

    public static TheoryData<string> ImproperData() => new(Improper);

    [Fact]
    public void The_lists_are_big_enough()
    {
        Assert.True(Clean.Length >= 60, "need at least 60 clean sentences");
        Assert.True(Improper.Length >= 80, "need at least 80 improper variants");
    }

    [Theory]
    [MemberData(nameof(CleanData))]
    public void Normal_text_passes(string text) => Assert.False(ProfanityFilter.IsImproper(text), text);

    [Theory]
    [MemberData(nameof(ImproperData))]
    public void Vulgar_text_is_blocked(string text) => Assert.True(ProfanityFilter.IsImproper(text), text);

    [Fact]
    public void Blank_text_is_never_improper()
    {
        Assert.False(ProfanityFilter.IsImproper(string.Empty));
        Assert.False(ProfanityFilter.IsImproper("   \n "));
    }

    [Fact]
    public void A_vulgar_word_buried_in_a_long_clean_text_is_found()
    {
        var text = string.Join(' ', Enumerable.Repeat("carro prata abc1d23 farol aceso", 40)) + " vai tomar no cu";

        Assert.True(ProfanityFilter.IsImproper(text));
    }

    [Fact]
    public void A_very_long_text_is_checked_quickly()
    {
        var text = string.Join(' ', Enumerable.Repeat("p 1 2 3 4 5 * a ! @ $ carro", 2000));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        ProfanityFilter.IsImproper(text);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), clock.Elapsed.ToString());
    }
}
