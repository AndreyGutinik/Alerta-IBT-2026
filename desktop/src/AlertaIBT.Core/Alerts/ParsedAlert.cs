namespace AlertaIBT.Core.Alerts;

/// <summary>
/// An alert after it was read from what a person typed: the vehicle, the plate and the reason, plus the whole text
/// in case it is not about a vehicle at all.
/// </summary>
/// <param name="Model">The vehicle model (<c>COROLLA</c>), or an empty string when there is none.</param>
/// <param name="Color">The color with its modifier, if any (<c>AZUL MARINHO</c>), or an empty string.</param>
/// <param name="Plate">The plate in display form (<c>ABC-1234</c>, <c>ABC-1D23</c>), or <c>null</c> when there is none.</param>
/// <param name="Reason">Why the alert was sent (<c>FAROL ACESO, VIDRO ABERTO</c>), or an empty string.</param>
/// <param name="FreeText">
/// The whole message, cleaned: no emoji, no leading <c>#</c>, on one line, in upper case, with punctuation as typed.
/// It is what an announcement shows when there is no plate.
/// </param>
public sealed record ParsedAlert(string Model, string Color, string? Plate, string Reason, string FreeText);

/// <summary>Where an alert came from.</summary>
public enum AlertSource
{
    /// <summary>The panel of the PC.</summary>
    Pc,

    /// <summary>A message in the Telegram group.</summary>
    Telegram,

    /// <summary>The site or the app of the phone.</summary>
    Site,
}

/// <summary>Who sent an alert, which decides what it must say to be accepted.</summary>
public enum SenderRole
{
    /// <summary>A church member: model, color and plate are all required.</summary>
    Member,

    /// <summary>An administrator of the site: only the plate is required for a vehicle alert.</summary>
    Admin,

    /// <summary>The operator at the PC: only the plate is required for a vehicle alert.</summary>
    Operator,
}
