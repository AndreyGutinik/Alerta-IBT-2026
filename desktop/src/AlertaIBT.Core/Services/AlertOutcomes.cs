using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Correction;

namespace AlertaIBT.Core.Services;

/// <summary>What <see cref="AlertService.SubmitAsync"/> did with an alert.</summary>
public enum SubmitKind
{
    /// <summary>The alert is in the queue.</summary>
    Queued,

    /// <summary>The alert lacks something required (model, color or plate); see <see cref="SubmitOutcome.Missing"/>.</summary>
    Rejected,

    /// <summary>The correction refused the text as improper.</summary>
    Refused,

    /// <summary>A member's alert is longer than <see cref="AlertRules.MemberMaxLength"/> letters once cleaned.</summary>
    TooLong,

    /// <summary>The same plate is already queued, on screen or was shown a moment ago, and the sender may not repeat it.</summary>
    Duplicate,

    /// <summary>The overlay is paused (the sermon).</summary>
    PausedRefused,
}

/// <summary>The outcome of <see cref="AlertService.SubmitAsync"/>.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Text">
/// The final text of the alert (upper case, on one line) when it got that far; empty when it was refused before correction.
/// </param>
/// <param name="Missing">What the alert lacks (<c>modelo</c>, <c>cor</c>, <c>placa</c>) when it was rejected; otherwise empty.</param>
/// <param name="Position">The 1-based place in the queue when queued (1 = next to show); otherwise 0.</param>
/// <param name="DuplicateMinutesAgo">
/// Whole minutes since the same plate was shown, when that is what made it a duplicate; <c>null</c> when it is pending or
/// on screen, or there is no duplicate.
/// </param>
/// <param name="WasDuplicate">
/// Whether the plate was a duplicate: for <see cref="SubmitKind.Duplicate"/> always, and also for <see cref="SubmitKind.Queued"/>
/// when the sender is allowed to repeat it (the PC and the administrator get a warning, not a refusal).
/// </param>
/// <param name="Id">The id of the queued alert; <c>null</c> when it was not queued.</param>
/// <param name="By">Who corrected the text.</param>
/// <param name="Changed">Whether the correction changed the words of the text.</param>
public sealed record SubmitOutcome(
    SubmitKind Kind,
    string Text,
    IReadOnlyList<string> Missing,
    int Position = 0,
    int? DuplicateMinutesAgo = null,
    bool WasDuplicate = false,
    string? Id = null,
    CorrectorKind By = CorrectorKind.Offline,
    bool Changed = false);

/// <summary>The "Vai aparecer assim" line of the panels: what an alert would turn into, without sending it.</summary>
/// <param name="Text">The final text (upper case, on one line); empty when the text was refused as improper or is empty.</param>
/// <param name="Kind">Whether it would be a vehicle alert, an announcement, or refused for lack of fields.</param>
/// <param name="Missing">What the alert lacks (<c>modelo</c>, <c>cor</c>, <c>placa</c>).</param>
/// <param name="Duplicate">Whether the plate is already queued, on screen or was shown in the last 10 minutes.</param>
/// <param name="By">Who corrected the text (blue <c>Offline</c> when <see cref="CorrectorKind.Offline"/>).</param>
/// <param name="DuplicateMinutesAgo">Whole minutes since the plate was shown, when that is what makes it a duplicate.</param>
/// <param name="Changed">Whether the correction changed the words of the text.</param>
/// <param name="Refused">Whether the correction refused the text as improper (then <paramref name="Kind"/> is <see cref="RuleKind.Reject"/>).</param>
/// <param name="TooLong">Whether the sender may not send a text this long (a member over <see cref="AlertRules.MemberMaxLength"/>); then <paramref name="Kind"/> is <see cref="RuleKind.Reject"/>.</param>
public sealed record PreviewResult(
    string Text,
    RuleKind Kind,
    IReadOnlyList<string> Missing,
    bool Duplicate,
    CorrectorKind By,
    int? DuplicateMinutesAgo = null,
    bool Changed = false,
    bool Refused = false,
    bool TooLong = false);
