using System.Collections.Frozen;
using AlertaIBT.Core.Alerts;

namespace AlertaIBT.Core.Correction;

/// <summary>
/// Words the pt-BR dictionary does not know but that must never be "corrected": car makes and the models most common in
/// Brazil, a few motorcycles, the colors and their modifiers, and the words of the usual parking reasons.
/// </summary>
public static class DomainWords
{
    // Written without accents; the set compares folded (see FoldingComparer), so "Onix", "ONIX" and the accented
    // spelling are the same entry.
    private static readonly string[] Vehicles =
    [
        // Chevrolet
        "chevrolet", "gm", "onix", "prisma", "celta", "corsa", "classic", "agile", "cobalt", "spin", "tracker", "s10",
        "montana", "cruze", "camaro", "equinox", "trailblazer", "meriva", "zafira", "vectra", "astra", "omega",
        "kadett", "monza", "opala", "chevette", "blazer", "silverado", "bolt", "captiva",

        // Volkswagen
        "volkswagen", "vw", "gol", "voyage", "fox", "polo", "virtus", "saveiro", "golf", "jetta", "passat", "tiguan",
        "taos", "nivus", "tcross", "t-cross", "amarok", "kombi", "fusca", "parati", "santana", "spacefox", "crossfox",
        "brasilia", "variant", "touareg", "gti",

        // Fiat
        "fiat", "uno", "palio", "siena", "strada", "toro", "mobi", "argo", "cronos", "pulse", "fastback", "fiorino",
        "doblo", "idea", "punto", "linea", "bravo", "stilo", "marea", "tempra", "elba", "premio", "ducato", "freemont",
        "tipo", "titano",

        // Toyota
        "toyota", "corolla", "hilux", "etios", "yaris", "camry", "rav4", "sw4", "prius", "bandeirante",

        // Hyundai
        "hyundai", "hb20", "hb20s", "creta", "tucson", "ix35", "i30", "azera", "elantra", "veloster", "hr", "hd80",
        "kona", "ioniq",

        // Honda
        "honda", "civic", "fit", "city", "hrv", "hr-v", "crv", "cr-v", "accord", "wrv", "wr-v", "zrv",

        // Renault
        "renault", "kwid", "sandero", "logan", "duster", "oroch", "captur", "clio", "symbol", "megane", "scenic",
        "fluence", "master", "kangoo", "stepway", "twingo", "zoe", "boreal", "kardian",

        // Jeep
        "jeep", "renegade", "compass", "commander", "wrangler", "cherokee", "gladiator", "willys",

        // Nissan
        "nissan", "kicks", "versa", "march", "sentra", "frontier", "livina", "tiida", "leaf", "altima",

        // Ford
        "ford", "ka", "fiesta", "focus", "fusion", "ecosport", "ranger", "edge", "courier", "escort", "belina",
        "corcel", "maverick", "f1000", "territory", "bronco", "mustang", "transit",

        // Peugeot
        "peugeot", "208", "207", "206", "2008", "307", "308", "3008", "408", "partner", "expert", "boxer", "hoggar",

        // Citroen
        "citroen", "c3", "c4", "cactus", "aircross", "picasso", "xsara", "jumpy", "jumper", "berlingo", "basalt",

        // Kia
        "kia", "sportage", "sorento", "cerato", "picanto", "soul", "stonic", "carnival", "bongo", "niro", "seltos",

        // Mitsubishi
        "mitsubishi", "lancer", "pajero", "outlander", "asx", "l200", "triton", "eclipse", "tr4",

        // BYD, GWM / Haval, Caoa Chery and other newcomers
        "byd", "dolphin", "seal", "yuan", "atto", "gwm", "haval", "ora", "poer", "jolion", "caoa", "chery", "tiggo",
        "arrizo", "celer", "omoda", "jaecoo", "jac", "lifan", "effa", "suzuki", "jimny", "vitara", "swift",
        "ssangyong", "kyron", "actyon",

        // Other makes
        "mercedes", "benz", "bmw", "audi", "volvo", "mini", "cooper", "defender", "discovery", "evoque", "porsche",
        "subaru", "lexus", "dodge", "chrysler", "pontiac", "tesla",

        // Motorcycles
        "moto", "motocicleta", "cg", "titan", "fan", "biz", "pop", "bros", "xre", "fazer", "factor", "nmax", "pcx",
        "yamaha", "kawasaki", "ninja", "cb", "cbr", "twister", "hornet", "lander", "crosser", "tenere", "xtz", "ybr",
        "burgman", "triumph", "harley", "davidson", "ducati", "dafra", "shineray", "haojue",
    ];

    // A second, broad list: vans, utilities, trucks, hatches, sedans, SUVs and motorcycles common in Brazilian car parks.
    // Several repeat the list above on purpose (the sets drop duplicates by their folded spelling), so this one can be
    // read on its own: nothing here may be "corrected" into another word ("mille" into "mile", "sprinter" into "sprinte").
    private static readonly string[] MoreVehicles =
    [
        // Vans, utilities and trucks
        "sprinter", "master", "ducato", "boxer", "daily", "kombi", "mille", "uno", "palio", "siena", "strada", "saveiro",
        "toro", "fiorino", "doblo", "hilux", "s10", "ranger", "amarok", "montana", "oroch",

        // Renault, Volkswagen, Fiat
        "duster", "kwid", "sandero", "logan", "captur", "fluence", "voyage", "gol", "parati", "fox", "polo", "virtus",
        "nivus", "t-cross", "tiguan", "jetta", "golf", "up", "argo", "cronos", "mobi", "pulse", "fastback",

        // Toyota, Honda
        "corolla", "etios", "yaris", "camry", "sw4", "rav4", "hr-v", "fit", "city", "civic", "accord", "wr-v", "cr-v",

        // Chevrolet
        "onix", "prisma", "cobalt", "spin", "tracker", "cruze", "equinox", "celta", "corsa", "classic", "meriva", "astra",
        "vectra", "zafira", "agile",

        // Ford
        "ka", "fiesta", "focus", "fusion", "ecosport", "edge", "bronco",

        // Peugeot, Citroen
        "208", "2008", "308", "3008", "c3", "c4", "aircross", "jumpy",

        // Nissan
        "kicks", "versa", "march", "frontier", "sentra", "tiida", "livina",

        // Hyundai
        "creta", "hb20", "hb20s", "ix35", "tucson", "santa", "fe", "azera", "elantra", "i30",

        // Jeep, Mitsubishi
        "compass", "renegade", "commander", "wrangler", "cherokee", "outlander", "asx", "lancer", "l200", "pajero", "tr4",

        // Chinese makes
        "dolphin", "song", "seal", "tang", "haval", "tiggo", "arrizo", "jolion",

        // Kia
        "sorento", "sportage", "cerato", "picanto", "soul", "bongo",

        // Motorcycles
        "titan", "fan", "bros", "xre", "biz", "pop", "pcx", "elite", "lead", "cg", "cb", "hornet", "fazer", "factor",
        "lander", "tenere", "mt", "ninja", "z400", "twister", "falcon", "burgman", "intruder", "haojue", "dafra",
        "shineray",
    ];

    // The reason words, spelled properly (with their accents): the correctors give these back.
    private static readonly string[] Reasons =
    [
        "farol", "far\U000000F3is", "aceso", "acesa", "acesos", "acesas", "vidro", "vidros", "aberto", "aberta",
        "abertos", "abertas", "porta", "portas", "porta-malas", "pisca", "pisca-alerta", "alerta", "alarme",
        "disparado", "disparando", "pneu", "pneus", "furado", "murcho", "estacionado", "estacionada", "estacionados",
        "parado", "parada", "bloqueando", "garagem", "port\U000000E3o", "sa\U000000EDda", "entrada", "vaga", "idoso",
        "deficiente", "contram\U000000E3o", "proibido", "local", "cal\U000000E7ada", "faixa", "dono", "comparecer",
        "recep\U000000E7\U000000E3o", "urgente", "luz", "luzes", "ligado", "ligada", "som", "chave",
    ];

    private static readonly FrozenDictionary<string, string> Spellings = Vehicles
        .Concat(MoreVehicles)
        .Concat(Reasons)
        .Concat(Vocabulary.Colors)
        .Concat(Vocabulary.ColorModifiers)
        .DistinctBy(Vocabulary.Fold)
        .ToFrozenDictionary(Vocabulary.Fold, word => word, StringComparer.Ordinal);

    // The words of the second list with 4 letters or fewer ("lead", "tang", "seal", "fan"): they stay in All, so they are
    // never "corrected" themselves, but they do not attract typos: "led" is not a typo of "lead", "tag" is not "tang".
    private static readonly FrozenSet<string> ShortModelWords = MoreVehicles
        .Select(Vocabulary.Fold)
        .Where(word => word.Length <= 4)
        .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Every word, folded (lower case, no accents) for the comparison; <c>Contains</c> ignores case and accents, so
    /// <c>"ONIX"</c> and the accented spelling of Onix are found as well.
    /// </summary>
    public static IReadOnlySet<string> All { get; } = Spellings.Keys.ToFrozenSet(FoldingComparer.Instance);

    /// <summary>
    /// The domain words of three letters or more without digits, folded and as they should be spelled, for the
    /// correctors to look for near matches (the short words of the second list are left out, see ShortModelWords).
    /// </summary>
    internal static IReadOnlyList<(string Folded, string Spelling)> Candidates { get; } =
        Spellings
            .Where(entry => entry.Key.Length >= 3 && !entry.Key.Any(char.IsDigit) && !ShortModelWords.Contains(entry.Key))
            .Select(entry => (entry.Key, entry.Value))
            .ToArray();

    /// <summary>The proper spelling (accents included) of <paramref name="word"/> when it is a domain word.</summary>
    internal static bool TryGetSpelling(string word, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? spelling) =>
        Spellings.TryGetValue(Vocabulary.Fold(word), out spelling);

    // Equality of two words as the correctors see it: same letters ignoring case and accents.
    private sealed class FoldingComparer : IEqualityComparer<string>
    {
        public static FoldingComparer Instance { get; } = new();

        public bool Equals(string? x, string? y) =>
            string.Equals(x is null ? null : Vocabulary.Fold(x), y is null ? null : Vocabulary.Fold(y), StringComparison.Ordinal);

        public int GetHashCode(string obj) => Vocabulary.Fold(obj).GetHashCode();
    }
}
