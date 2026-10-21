namespace AlertaIBT.Core.Alerts;

/// <summary>What to do with an alert.</summary>
public enum RuleKind
{
    /// <summary>Show it as a vehicle alert (<c>MODELO COR | PLACA: ABC-1234 | MOTIVO</c>).</summary>
    Vehicle,

    /// <summary>Show it as an announcement, with no structure.</summary>
    Free,

    /// <summary>Refuse it: something that is required is missing.</summary>
    Reject,
}

/// <summary>The verdict of <see cref="AlertRules.Decide"/>.</summary>
/// <param name="Kind">What to do with the alert.</param>
/// <param name="Missing">
/// What the alert lacks (see <see cref="AlertRules.Missing"/>), whatever the verdict: a refusal tells the sender,
/// and a vehicle alert or an announcement may be missing parts too.
/// </param>
public sealed record RuleDecision(RuleKind Kind, IReadOnlyList<string> Missing);

/// <summary>The rules about what an alert must say, which depend on who sent it.</summary>
public static class AlertRules
{
    /// <summary>
    /// The most letters an alert from a member (Telegram, site) may have once cleaned and formatted: a longer one would hold
    /// the LED for minutes (Telegram allows 4096 characters). The PC operator and the administrator have no limit.
    /// </summary>
    public const int MemberMaxLength = 200;

    /// <summary>
    /// Whether <paramref name="formattedText"/> (the cleaned, formatted text that would reach the bar) is longer than
    /// <paramref name="role"/> may send. Only the operator and the administrator are unlimited; an unknown role gets the limit of a member.
    /// </summary>
    public static bool IsTooLong(string formattedText, SenderRole role)
    {
        ArgumentNullException.ThrowIfNull(formattedText);

        return role is not (SenderRole.Admin or SenderRole.Operator) && formattedText.Length > MemberMaxLength;
    }

    /// <summary>
    /// What the alert lacks, as <c>"modelo"</c>, <c>"cor"</c> and <c>"placa"</c>, in that order. Empty when it is
    /// complete.
    /// </summary>
    public static IReadOnlyList<string> Missing(ParsedAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        List<string> missing = [];
        if (string.IsNullOrWhiteSpace(alert.Model))
        {
            missing.Add("modelo");
        }

        if (string.IsNullOrWhiteSpace(alert.Color))
        {
            missing.Add("cor");
        }

        if (string.IsNullOrWhiteSpace(alert.Plate))
        {
            missing.Add("placa");
        }

        return missing;
    }

    /// <summary>
    /// Decides what to do with <paramref name="alert"/> sent by <paramref name="role"/>.
    /// </summary>
    /// <remarks>
    /// Nothing to show (no text, or only <c>#</c> and emoji) is refused for everybody. A member needs model, color and
    /// plate. The operator and the administrator only need the plate: with it the alert is shown as a vehicle alert
    /// (the parts that are missing vanish from the text), without it as an announcement. An unknown role gets the
    /// rules of a member.
    /// </remarks>
    public static RuleDecision Decide(ParsedAlert alert, SenderRole role)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var missing = Missing(alert);
        return new RuleDecision(KindFor(alert, role, missing), missing);
    }

    private static RuleKind KindFor(ParsedAlert alert, SenderRole role, IReadOnlyList<string> missing)
    {
        if (string.IsNullOrWhiteSpace(alert.FreeText))
        {
            return RuleKind.Reject;
        }

        if (role is SenderRole.Admin or SenderRole.Operator)
        {
            return string.IsNullOrWhiteSpace(alert.Plate) ? RuleKind.Free : RuleKind.Vehicle;
        }

        return missing.Count == 0 ? RuleKind.Vehicle : RuleKind.Reject;
    }
}
