using System.Text;

namespace AlertaIBT.Core.Correction;

/// <summary>
/// Fixed rules the app brings by itself, written as the same <c>wrong => right</c> lines the user types, so there is one
/// way to say it. <see cref="FixedRulesProvider"/> puts them after the user's own rules: a rule of the user's with the same
/// left side wins (the first of two equal left sides is the one that counts).
/// </summary>
public static class BuiltInFixedRules
{
    // A dictionary word that is wrong in a parking notice: "video aberto" is a window ("vidro"), not a clip. The rule needs
    // the adjective, so "video do culto" stays as it is. Folded without accents: the rules ignore case and accents.
    private static readonly string[] WindowAdjectives =
    [
        "aberto", "aberta", "abertos", "abertas",
        "fechado", "fechada", "fechados", "fechadas",
        "quebrado", "quebrada", "quebrados", "quebradas",
        "trincado", "trincada", "trincados", "trincadas",
        "riscado", "riscada", "riscados", "riscadas",
        "baixado", "baixada", "baixados", "baixadas",
        "levantado", "levantada", "levantados", "levantadas",
        "entreaberto", "entreaberta", "entreabertos", "entreabertas",
    ];

    /// <summary>The built-in rules, one per line.</summary>
    public static string Text { get; } = Build();

    private static string Build()
    {
        StringBuilder lines = new();
        foreach (var (wrong, right) in new[] { ("video", "vidro"), ("videos", "vidros") })
        {
            foreach (var adjective in WindowAdjectives)
            {
                lines.Append(wrong).Append(' ').Append(adjective).Append(" => ").Append(right).Append(' ').Append(adjective).Append('\n');
            }
        }

        return lines.ToString();
    }
}
