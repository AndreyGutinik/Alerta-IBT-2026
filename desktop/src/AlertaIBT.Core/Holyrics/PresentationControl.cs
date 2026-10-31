namespace AlertaIBT.Core.Holyrics;

/// <summary>How a request to turn the presentation mode on or off ended.</summary>
/// <param name="Success">Whether the mode is now in the state that was asked.</param>
/// <param name="Message">Why not (pt-BR); <c>null</c> on success.</param>
public sealed record PresentationResult(bool Success, string? Message = null)
{
    /// <summary>It worked.</summary>
    public static PresentationResult Ok { get; } = new(true);

    /// <summary>It did not, and why.</summary>
    public static PresentationResult Fail(string message) => new(false, message);
}

/// <summary>
/// The presentation mode as the API and the map of the Controle tab drive it: the keys of the presenter remote are taken everywhere and become slide commands
/// for Holyrics. Safe to call from any thread. The mode is never saved: it is off at every start.
/// </summary>
public interface IPresentationControl
{
    /// <summary>Whether the mode is on now.</summary>
    bool IsOn { get; }

    /// <summary>Turns the mode on or off. Refused (with the reason) when Holyrics is not configured or the keyboard hook cannot be installed; turning off always works.</summary>
    PresentationResult SetOn(bool on);
}
