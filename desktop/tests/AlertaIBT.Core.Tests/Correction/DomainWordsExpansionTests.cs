using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Correction;

namespace AlertaIBT.Core.Tests.Correction;

// The second, broad list of Brazilian vehicle makes and models: none of them may be "corrected" into something else.
public class DomainWordsExpansionTests(OfflineCorrectorFixture fixture) : IClassFixture<OfflineCorrectorFixture>
{
    private readonly OfflineCorrector _corrector = fixture.Corrector;

    private static readonly string[] Models =
    [
        "Sprinter", "Master", "Ducato", "Boxer", "Daily", "Kombi", "Mille", "Uno", "Palio", "Siena", "Strada", "Saveiro",
        "Toro", "Fiorino", "Doblo", "Hilux", "S10", "Ranger", "Amarok", "Montana", "Oroch", "Duster", "Kwid", "Sandero",
        "Logan", "Captur", "Fluence", "Voyage", "Gol", "Parati", "Fox", "Polo", "Virtus", "Nivus", "T-Cross", "Tiguan",
        "Jetta", "Golf", "Up", "Argo", "Cronos", "Mobi", "Pulse", "Fastback", "Corolla", "Etios", "Yaris", "Camry", "SW4",
        "RAV4", "HR-V", "Fit", "City", "Civic", "Accord", "WR-V", "CR-V", "Onix", "Prisma", "Cobalt", "Spin", "Tracker",
        "Cruze", "Equinox", "Celta", "Corsa", "Classic", "Meriva", "Astra", "Vectra", "Zafira", "Agile", "Ka", "Fiesta",
        "Focus", "Fusion", "EcoSport", "Edge", "Bronco", "208", "2008", "308", "3008", "C3", "C4", "Aircross", "Jumpy",
        "Kicks", "Versa", "March", "Frontier", "Sentra", "Tiida", "Livina", "Creta", "HB20", "HB20S", "ix35", "Tucson",
        "Santa Fe", "Azera", "Elantra", "i30", "Compass", "Renegade", "Commander", "Wrangler", "Cherokee", "Outlander",
        "ASX", "Lancer", "L200", "Pajero", "TR4", "Dolphin", "Song", "Seal", "Tang", "Haval", "Tiggo", "Arrizo", "Jolion",
        "Sorento", "Sportage", "Cerato", "Picanto", "Soul", "Bongo",

        // Motorcycles
        "Titan", "Fan", "Bros", "XRE", "Biz", "Pop", "PCX", "Elite", "Lead", "CG", "CB", "Hornet", "Fazer", "Factor",
        "Lander", "Tenere", "MT", "Ninja", "Z400", "Twister", "Falcon", "Burgman", "Intruder", "Haojue", "Dafra",
        "Shineray",
    ];

    public static TheoryData<string> Words
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var word in Models.SelectMany(model => model.Split(' ')).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                data.Add(word);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Words))]
    public void EveryWordIsADomainWord(string word)
    {
        Assert.Contains(word, DomainWords.All);
    }

    [Theory]
    [MemberData(nameof(Words))]
    public async Task EveryWordPassesThroughTheOfflineCorrectorUnchanged(string word)
    {
        foreach (var typed in new[] { word, word.ToLowerInvariant(), word.ToUpperInvariant() })
        {
            var raw = $"{typed} branco farol aceso";

            var corrected = await _corrector.CorrectAsync(raw, CancellationToken.None);

            Assert.Equal(AlertParser.Parse(raw), corrected);
        }
    }

    [Theory]
    [InlineData("Sprinter branca")]
    [InlineData("Mille preto")]
    [InlineData("sprinter branca abc1234 vidro aberto")]
    [InlineData("mille preto abc1d23")]
    public async Task SprinterAndMilleAreNotAltered(string raw)
    {
        var corrected = await _corrector.CorrectAsync(raw, CancellationToken.None);

        Assert.Equal(AlertParser.Parse(raw), corrected);
    }

    // ---- short model words only in the second list do not attract typos ----

    [Theory]
    [InlineData("lead")]
    [InlineData("song")]
    [InlineData("tang")]
    public void ShortWordsOnlyInTheSecondList_AreKnownButNeverAttractTypos(string word)
    {
        Assert.Contains(word, DomainWords.All);
        Assert.DoesNotContain(DomainWords.Candidates, candidate => candidate.Folded == word);
    }

    [Theory]
    [InlineData("seal")]
    [InlineData("city")]
    [InlineData("fit")]
    [InlineData("fan")]
    [InlineData("biz")]
    [InlineData("pop")]
    [InlineData("edge")]
    [InlineData("onix")]
    public void ShortWordsOfTheSecondListAreKnownButNeverAttractTyposEvenWhenTheFirstListHasThemToo(string word)
    {
        Assert.Contains(word, DomainWords.All);
        Assert.DoesNotContain(DomainWords.Candidates, candidate => candidate.Folded == word);
    }

    [Theory]
    [InlineData("sprinter")]
    [InlineData("daily")]
    [InlineData("elite")]
    [InlineData("falcon")]
    [InlineData("intruder")]
    [InlineData("santa")]
    [InlineData("mille")]
    [InlineData("march")]
    [InlineData("corolla")]
    public void WordsOfFiveLettersOrMoreStayCandidates(string word)
    {
        Assert.Contains(DomainWords.Candidates, candidate => candidate.Folded == word);
    }

    [Theory]
    [InlineData("sonf", "Song")]
    [InlineData("tamg", "Tang")]
    [InlineData("leaf", "Lead")]
    [InlineData("leas", "Lead")]
    public async Task ATypoIsNotPulledTowardAShortSecondListWord(string typed, string attractor)
    {
        var corrected = await _corrector.CorrectAsync($"gol branco {typed}", CancellationToken.None);

        Assert.NotNull(corrected);
        Assert.DoesNotContain(attractor, corrected.FreeText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("led", "lead")]
    [InlineData("tag", "tang")]
    [InlineData("sel", "seal")]
    [InlineData("sea", "seal")]
    public async Task ShortCommonWordsAreNotTurnedIntoAShortModel(string word, string model)
    {
        var corrected = await _corrector.CorrectAsync($"gol branco {word}", CancellationToken.None);

        Assert.NotNull(corrected);
        Assert.DoesNotContain(model, corrected.FreeText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("sprintr", "Sprinter")]
    [InlineData("corrolla", "Corolla")]
    [InlineData("coroola", "Corolla")]
    [InlineData("hilx", "Hilux")]
    [InlineData("compas", "Compass")]
    [InlineData("renegde", "Renegade")]
    public async Task TyposOfLongerModelWordsAreStillCorrected(string typed, string expected)
    {
        var corrected = await _corrector.CorrectAsync($"{typed} branco", CancellationToken.None);

        Assert.NotNull(corrected);
        Assert.Contains(expected, corrected.FreeText, StringComparison.OrdinalIgnoreCase);
    }
}
