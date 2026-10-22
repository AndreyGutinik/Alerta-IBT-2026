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
    /// slur, but also what a car or a door is after a break-in; "Grao-Mongol" is a city.
    /// </summary>
    public static readonly (string[] Words, string[] After)[] Exemptions =
    [
        (
            ["arrombado", "arrombada", "arrombados", "arrombadas"],
            [
                "carro", "carros", "veiculo", "veiculos", "automovel", "auto", "moto", "motos", "caminhao", "porta", "portas",
                "vidro", "vidros", "portao", "cadeado", "janela", "cofre", "trinco", "fechadura", "bagageiro", "malas",
            ]),
        (["mongol"], ["grao"]),
    ];

    /// <summary>
    /// Slang that is blocked only next to a word that makes it body slang: the word before it (the very next word back,
    /// never past punctuation) is one of <c>Before</c>, and the word after it is not one of <c>NotNext</c>. Bare, these
    /// words are ordinary: "Pinto Silva", "Seu Pinto", "pau de selfie", "pomba rola", "Rola Moca" (a street), "Bunda" (an
    /// ethnic group and a city), "peito" and "tetas" in a nursing or health notice. Articles (a, o, uma) are not used as a
    /// context: "o pinto" is a chick and "a rola" is a bird or a verb. "seu" is not a context for pinto (Seu Pinto is a
    /// title and a surname), and esse/essa are not either ("esse Pinto").
    /// </summary>
    public static readonly (string[] Words, string[] Before, string[] NotNext)[] ContextRules =
    [
        (
            ["pau", "paus", "rola", "rolas", "piru", "pirus", "bunda", "bundas"],
            [
                "meu", "minha", "teu", "tua", "seu", "sua", "meus", "minhas", "teus", "tuas", "seus", "suas",
                "esse", "essa", "esses", "essas", "este", "esta", "chupa", "chupar", "chupando", "chupou", "mama", "mamar", "mamando",
                "lambe", "lamber", "lambendo", "lambeu",
            ],
            ["de", "brasil", "a"]),
        (
            ["pinto", "pintos"],
            ["meu", "minha", "teu", "tua", "meus", "minhas", "teus", "tuas", "chupa", "chupar", "chupando", "chupou", "mama", "mamar", "mamando", "lambe", "lamber", "lambendo", "lambeu"],
            []),
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
    /// A listed word that is let through when one of the words on the right comes right after it: "pica-pau" is a
    /// woodpecker (the filter reads the hyphen as a space).
    /// </summary>
    public static readonly (string[] Words, string[] Next)[] FollowExemptions =
    [
        (["pica", "picas", "pika", "pikas"], ["pau", "paus"]),
    ];
}
