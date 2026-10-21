using AlertaIBT.Core.Alerts;

namespace AlertaIBT.Core.Correction;

/// <summary>Something that fixes the spelling of a short alert: Gemini, or the offline dictionary.</summary>
public interface ICorrector
{
    /// <summary>
    /// Corrects <paramref name="raw"/> and reads it into an alert, or returns <c>null</c> when it could not (no key,
    /// no internet, quota, an answer that makes no sense).
    /// </summary>
    Task<ParsedAlert?> CorrectAsync(string raw, CancellationToken ct);
}
