using AlertaIBT.Core.Alerts;

namespace AlertaIBT.Core.Correction;

/// <summary>Which corrector produced an alert.</summary>
public enum CorrectorKind
{
    /// <summary>Gemini, over the internet.</summary>
    Gemini,

    /// <summary>The offline dictionary (or, when it could not run either, the plain parser).</summary>
    Offline,
}

/// <summary>What <see cref="CorrectionPipeline.RunAsync"/> decided.</summary>
/// <param name="Alert">The corrected alert, with the fixed rules applied (except when <paramref name="Refused"/>).</param>
/// <param name="By">Who corrected it.</param>
/// <param name="Changed">
/// Whether the words of the text changed (spelling, accents, a fixed rule). Reordering, upper case, punctuation, the
/// <c>#</c>, emoji and the <c>PLACA:</c> label are not changes.
/// </param>
/// <param name="Refused">
/// Gemini refused the text as improper (an empty announcement with no fields). The alert must not be shown.
/// </param>
public sealed record CorrectionResult(ParsedAlert Alert, CorrectorKind By, bool Changed, bool Refused);
