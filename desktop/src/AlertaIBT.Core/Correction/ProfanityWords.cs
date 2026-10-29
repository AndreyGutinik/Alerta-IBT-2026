namespace AlertaIBT.Core.Correction;

/// <summary>
/// The built-in word lists of <see cref="ProfanityFilter"/>. Every entry is lower case and has no accents (the filter
/// strips the accents of what it reads). Brazilian Portuguese first, then English.
/// </summary>
/// <remarks>
/// Ambiguity rules: a word that is common in vehicle, parking or church messages, or is a common name, is never listed
/// bare (pau, pinto, rola, piru, bunda, mala, porta, farol, macaco, gato, vaca, burro, galinha, cachorro, banco, fusca;
/// "marica" and "sex" are not listed: the city of Marica and the abbreviation of sexta-feira). Those body-slang words
/// (pau, pinto, rola, piru, bunda, tetas, peitos) are blocked only in context, see <see cref="ContextRules"/>. "pica" and
/// "pika" (the owner asked for them) are blocked bare, except in the compound "pica-pau" (woodpecker), see
/// <see cref="FollowExemptions"/>. Whole words only for 4 letters or fewer. Prefixes and fragments are
/// only used when they are long (6 letters or more, "fuck" being the one exception) and no Portuguese word contains them.
/// "porra" is whole-word only: "porao" (basement) and "corporacao" would be hit by a prefix or a fragment.
/// </remarks>
internal static class ProfanityWords
{
    /// <summary>Whole words (after the filter folds repeated letters, so "caralhooo" and "caralho" are the same word).</summary>
    public static readonly string[] Exact =
    [
        // strong profanity
        "porra", "porras", "porrada", "porradas", "porrinha",
        "caralho", "caralhos", "karalho", "caraio", "caralio", "krl", "crl", "krlh", "crlh", "crlho", "krlho",
        "puta", "putas", "puto", "putos", "putao", "putona", "putaria", "putarias", "puteiro", "puteiros",
        "fdp", "vsf", "vtnc", "vtmnc", "vtmc", "tnc", "tmnc", "pqp", "pnc",
        "foda", "fodas", "foder", "fode", "fodeu", "fodido", "fodida", "fodidos", "fodidas", "fodendo", "fodam", "fodi",
        "fodao", "fodona", "fodinha",
        "piroca", "pirocas", "pirocao", "pirocudo", "pirocuda",
        "merda", "merdas", "merdinha", "merdao", "merdona", "merdoso", "merdosa", "merdada",
        "bosta", "bostas", "bostinha",
        "cacete", "cacetes", "cacetada", "cacetadas", "caceta",
        "fudeu", "fude", "fuder", "fudido", "fudida", "fudidos", "fudidas", "fudendo", "fudase", "fodasse",
        "bostao",

        // body parts used as insults
        "cu", "cus", "cuzao", "cuzona", "cuzinho", "cuzinhos", "cuzoes",
        "xereca", "xerecas", "xerequinha", "xota", "xotas", "xoxota", "xoxotas", "xoxotinha", "xoxotinhas", "cona", "conas",
        "boceta", "bocetas", "bocetinha", "chochota", "chochotas", "xochota", "xochotas", "pepeca", "pepecas", "priquito", "priquitos",
        "pica", "picas", "pika", "pikas", "jeba", "jebas", "bilau", "bilaus", "colhao", "colhoes",
        "bundao", "bundona", "bundinha", "bundoes",

        // slurs and insults
        "viado", "viados", "viadao", "viadagem", "veado", "veados",
        "bicha", "bichas", "bichona", "bichonas", "bichinha", "bichinhas",
        "sapatao", "sapatona", "sapatonas", "paneleiro", "paneleiros", "boiola", "baitola", "traveco",
        "arrombado", "arrombada", "arrombados", "arrombadas",
        "babaca", "babacas", "babaquice",
        "otario", "otaria", "otarios", "otarias",
        "imbecil", "imbecis", "idiota", "idiotas",
        "retardado", "retardada", "retardados", "retardadas",
        "mongol", "mongoloide", "mongoloides",
        "desgracado", "desgracada", "desgracados", "desgracadas",
        "vagabundo", "vagabunda", "vagabundos", "vagabundas",
        "vadia", "vadias",
        "safado", "safada", "safados", "safadas", "safadinho", "safadinha", "safadeza",
        "corno", "corna", "cornos", "cornudo", "cornuda",
        "cretino", "cretina", "cretinos", "cretinas",
        "escroto", "escrota", "escrotos", "escrotas",
        "estupido", "estupida", "estupidos", "estupidas",
        "biscate", "biscates", "quenga", "quengas",
        "crioulo", "crioula", "crioulos", "crioulas",

        // sexual
        "sexo", "transar", "transou", "transando", "transei", "transaram",
        "masturbacao", "masturbar", "masturbando", "orgasmo", "orgasmos",
        "porno", "pornos", "pornografia", "porn", "nude", "nudes",
        "tarado", "tarada", "tarados", "taradas",
        "tesao", "tesudo", "tesuda", "boquete", "boquetes", "queca", "quecas", "trepada", "trepadas", "ejacular", "ejaculacao",
        "xvideos", "xvideo", "pornhub", "xnxx",
        "estupro", "estupros", "estuprar", "estuprador", "estupradora",
        "pedofilo", "pedofila", "pedofilia",

        // English
        "fck", "fuk", "shit", "shits", "shitty", "bullshit", "shithead",
        "pussy", "pussies", "cunt", "cunts", "dick", "dicks", "dickhead",
        "wtf", "stfu", "slut", "sluts", "whore", "whores", "bastard", "bastards", "faggot", "faggots",
        "cock", "cocks", "cocksucker", "tits", "titties", "boobs", "milf", "wanker", "twat", "dildo", "blowjob", "horny",
        "retard", "retards", "retarded", "debiloide",
    ];

    /// <summary>
    /// Whole words compared as typed, without folding repeated letters: folded, "nigger" becomes "niger", the country.
    /// </summary>
    public static readonly string[] Raw = ["nigger", "niggers", "nigga", "niggas"];

    /// <summary>Whole words that start with one of these (6 letters or more, folded).</summary>
    public static readonly string[] Prefixes = ["putinh", "viadinh"];

    /// <summary>Fragments that give a word away wherever they sit (long and unique to the vulgar word, folded).</summary>
    public static readonly string[] Fragments =
    [
        "caralh", "karalh", "buceta", "punhet", "siriric", "fodase", "fuck", "bitch", "asshol",
        "filhodaputa", "filhadaputa", "vaitomarnocu", "tomarnocu", "tomanocu", "putaquepariu", "vaisefoder",
    ];

    /// <summary>
    /// A listed word that is also an ordinary word in one context: it is let through when one of the words on the right
    /// comes shortly before it (at most 3 words back, and not past an earlier copy of the same word). "arrombado" is a
    /// slur, but also what a car or a door is after a break-in; "Grao-Mongol" is a city. Batch 20 added the other ordinary
    /// senses (see the comment inside the list); the compounds that follow the word are in <see cref="FollowExemptions"/>.
    /// </summary>
    public static readonly (string[] Words, string[] After)[] Exemptions =
    [
        (
            ["arrombado", "arrombada", "arrombados", "arrombadas"],
            [
                "carro", "carros", "veiculo", "veiculos", "automovel", "auto", "moto", "motos", "caminhao", "porta", "portas",
                "vidro", "vidros", "portao", "cadeado", "janela", "cofre", "trinco", "fechadura", "bagageiro", "malas",
            ]),
        (["mongol"], ["grao", "imperio", "povo", "rally", "exercito", "tribo"]),

        // Ordinary words (batch 20) that the list also holds. Each is let through only next to a word that makes the sense plain.
        // "sexo" as a field of a form or a group of people; "cacete" as the bread; "nude" as a colour; "corno" as the horn of a
        // ram (the shofar); "veado" as the animal; "crioulo" as a breed or a language; "Moby Dick" and "Dick Vigarista".
        (["sexo"], ["nome", "idade", "ambos", "mesmo", "outro", "genero", "cadastro", "ficha", "campo", "formulario", "dados"]),
        (["cacete", "cacetes"], ["pao", "paes", "padaria", "fornada", "bandeja"]),
        (
            ["nude"],
            [
                "tom", "tons", "cor", "cores", "bege", "rosa", "vestido", "esmalte", "batom", "sapato", "sandalia", "bolsa", "blusa",
                "salto", "maquiagem", "tonalidade", "paleta", "calca", "saia", "bota", "capa", "tinta", "pintura",
            ]),
        (
            ["corno", "cornos"],
            [
                "toque", "tocar", "tocou", "tocando", "soprar", "sopro", "soprou", "soprando", "carneiro", "carneiros", "uncao",
                "azeite", "oleo", "altar", "jubileu", "trombeta", "shofar", "chifre", "chifres", "bode", "touro", "cabra",
            ]),
        (
            ["veado", "veados"],
            ["carne", "chifre", "chifres", "pele", "cacada", "cacar", "caca", "manada", "cervo", "cervos", "corca", "bambi", "rebanho"]),
        (
            ["crioulo", "crioula", "crioulos", "crioulas"],
            [
                "cavalo", "cavalos", "egua", "eguas", "galinha", "galinhas", "feijao", "milho", "porco", "porcos", "lingua", "linguas",
                "raca", "gado", "boi", "bois", "ovelha", "carneiro", "arroz", "semente", "sementes", "agulha", "dialeto", "idioma",
            ]),
        (["dick"], ["moby", "philip", "k", "tracy", "van"]),
    ];

    /// <summary>
    /// Slang that is blocked only next to a word that makes it body slang: the word before it (the very next word back,
    /// never past punctuation) is one of <c>Before</c>, and the word after it is not one of <c>NotNext</c>. Bare, these
    /// words are ordinary: "Pinto Silva", "Joao Pinto", "pau de selfie", "pomba rola", "Rola Moca" (a street), "Bunda" (an
    /// ethnic group and a city), "peito" and "tetas" in a nursing or health notice.
    /// The owner decided (batch 13) that these never pass, whatever follows: any possessive before "pau" ("meu pau de selfie"),
    /// "seu/esse/este/o pinto" besides "meu/teu pinto", and "a rola". The price is a few rare false positives ("Seu Pinto",
    /// "o Pinto Silva"): the bare surname ("Pinto Silva", "Pinto Coelho") and the bare "pau" still pass.
    /// </summary>
    public static readonly (string[] Words, string[] Before, string[] NotNext)[] ContextRules =
    [
        // Any possessive before pau is blocked, even "meu pau de selfie" (the owner's decision).
        (
            ["pau", "paus"],
            ["meu", "minha", "teu", "tua", "seu", "sua", "meus", "minhas", "teus", "tuas", "seus", "suas"],
            []),
        (
            ["pau", "paus", "rola", "rolas", "piru", "pirus", "bunda", "bundas"],
            [
                "meu", "minha", "teu", "tua", "seu", "sua", "meus", "minhas", "teus", "tuas", "seus", "suas",
                "esse", "essa", "esses", "essas", "este", "esta", "chupa", "chupar", "chupando", "chupou", "mama", "mamar", "mamando",
                "lambe", "lamber", "lambendo", "lambeu",
            ],
            ["de", "brasil", "a"]),

        // "a rola" / "as rolas" (the owner's decision); "a Rola Moca" is still the street.
        (["rola", "rolas"], ["a", "as"], ["moca"]),
        (
            ["pinto", "pintos"],
            [
                "meu", "minha", "teu", "tua", "meus", "minhas", "teus", "tuas", "seu", "sua", "seus", "suas",
                "esse", "essa", "esses", "essas", "este", "esta", "estes", "estas",
                "chupa", "chupar", "chupando", "chupou", "mama", "mamar", "mamando", "lambe", "lamber", "lambendo", "lambeu",
            ],
            []),

        // "o pinto" (the owner's decision); the plural "os pintos" (chicks on a farm) is not asked for and passes.
        (["pinto"], ["o"], []),
        (
            ["teta", "tetas", "tetinha", "tetinhas", "peitos", "peitinhos"],
            [
                "meu", "minha", "teu", "tua", "seu", "sua", "meus", "minhas", "teus", "tuas", "seus", "suas",
                "esse", "essa", "esses", "essas", "chupa", "chupar", "chupando", "chupou", "mama", "mamar", "mamando",
                "lambe", "lamber", "lambendo", "lambeu",
            ],
            []),
    ];

    /// <summary>
    /// Listed words that are never rebuilt out of two pieces ("pi-ca" is, "cor-no", "via do" and "por-nos", as in "por-nos de acordo", are ordinary text): see
    /// <c>ProfanityFilter</c>, split words.
    /// </summary>
    public static readonly string[] NeverJoined = ["corno", "viado", "veado", "pornos"];

    /// <summary>
    /// A listed word that is let through when one of the words on the right comes right after it: "pica-pau" is a
    /// woodpecker (the filter reads the hyphen as a space).
    /// </summary>
    public static readonly (string[] Words, string[] Next)[] FollowExemptions =
    [
        (["pica", "picas", "pika", "pikas"], ["pau", "paus"]),
        (["sexo"], ["masculino", "feminino", "oposto", "biologico", "fragil", "forte"]),
        (["corno", "cornos"], ["de"]),
        (["dick"], ["vigarista", "tracy", "dastardly", "grayson"]),
        (["mongol"], ["imperio", "rally", "khan", "povo", "tribo", "exercito", "guerreiro", "guerreiros", "tartaro", "gengis", "genghis"]),
        (["crioulo", "crioula", "crioulos", "crioulas"], ["haitiano", "haitiana", "cabo", "verde", "verdiano", "caboverdiano"]),
    ];
}
