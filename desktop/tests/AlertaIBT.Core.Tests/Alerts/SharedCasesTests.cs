using System.Text;
using System.Text.Json;
using AlertaIBT.Core.Alerts;

namespace AlertaIBT.Core.Tests.Alerts;

// Runs shared/parser-cases.json, the file the site's TypeScript parser has to pass too. The test project copies it
// next to the test assembly.
public class SharedCasesTests
{
    private const string FileName = "parser-cases.json";

    private static readonly string[] CaseProperties = ["raw", "role", "expectedText", "missing", "kind"];
    private static readonly string[] Roles = ["member", "admin", "operator"];
    private static readonly string[] Kinds = ["vehicle", "free", "reject"];
    private static readonly string[] MissingFields = ["modelo", "cor", "placa"];

    public static IEnumerable<TheoryDataRow<string, string, string, string[], string>> Cases() =>
        LoadCases().Select(c => new TheoryDataRow<string, string, string, string[], string>(
            c.Raw,
            c.Role,
            c.ExpectedText,
            c.Missing,
            c.Kind));

    [Theory]
    [MemberData(nameof(Cases))]
    public void Pipeline_MatchesTheSharedCase(string raw, string role, string expectedText, string[] missing, string kind)
    {
        var alert = AlertParser.Parse(raw);
        var decision = AlertRules.Decide(alert, Enum.Parse<SenderRole>(role, ignoreCase: true));

        Assert.Equal(Enum.Parse<RuleKind>(kind, ignoreCase: true), decision.Kind);
        Assert.Equal(missing, decision.Missing);
        Assert.Equal(expectedText, TextToShow(decision.Kind, alert));
    }

    [Fact]
    public void File_IsUtf8WithoutBom_SoAnyJsonParserReadsIt()
    {
        var bytes = File.ReadAllBytes(PathToFile());

        var startsWithBom = bytes is [0xEF, 0xBB, 0xBF, ..];
        Assert.False(startsWithBom);
    }

    [Fact]
    public void File_HasOnlyDocumentedCases()
    {
        var cases = LoadCases();

        Assert.NotEmpty(cases);
        foreach (var c in cases)
        {
            var label = $"{c.Role}: {c.Raw}";
            Assert.True(Roles.Contains(c.Role), $"unknown role in case '{label}'");
            Assert.True(Kinds.Contains(c.Kind), $"unknown kind in case '{label}'");
            Assert.True(
                c.Missing.SequenceEqual(MissingFields.Where(field => c.Missing.Contains(field))),
                $"missing must hold only modelo, cor, placa, in that order and without repeats, in case '{label}'");
            Assert.True(
                (c.Kind == "reject") == (c.ExpectedText.Length == 0),
                $"expectedText is empty exactly when the kind is reject, in case '{label}'");
        }

        Assert.Equal(cases.Count, cases.Select(c => (c.Raw, c.Role)).Distinct().Count());
    }

    private static string TextToShow(RuleKind kind, ParsedAlert alert) => kind switch
    {
        RuleKind.Vehicle => AlertFormatter.Format(alert),
        RuleKind.Free => AlertFormatter.FormatFree(alert.FreeText),
        _ => string.Empty,
    };

    private static string PathToFile() => Path.Combine(AppContext.BaseDirectory, FileName);

    private static List<SharedCase> LoadCases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(PathToFile(), Encoding.UTF8));
        return document.RootElement.EnumerateArray().Select(ReadCase).ToList();
    }

    private static SharedCase ReadCase(JsonElement element)
    {
        var properties = element.EnumerateObject().Select(p => p.Name).Order().ToArray();
        if (!properties.SequenceEqual(CaseProperties.Order()))
        {
            throw new InvalidDataException(
                $"A case must have exactly {string.Join(", ", CaseProperties)}, but has {string.Join(", ", properties)}.");
        }

        return new SharedCase(
            element.GetProperty("raw").GetString()!,
            element.GetProperty("role").GetString()!,
            element.GetProperty("expectedText").GetString()!,
            element.GetProperty("missing").EnumerateArray().Select(m => m.GetString()!).ToArray(),
            element.GetProperty("kind").GetString()!);
    }

    private sealed record SharedCase(string Raw, string Role, string ExpectedText, string[] Missing, string Kind);
}
