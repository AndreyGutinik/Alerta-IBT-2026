using AlertaIBT.Core.Alerts;

namespace AlertaIBT.Core.Queue;

/// <summary>One alert waiting for, or taking, its turn on the screen.</summary>
/// <param name="Id">Unique id of the alert; every action on the queue refers to it.</param>
/// <param name="Text">What the bar shows.</param>
/// <param name="Plate">The plate in display form (<c>ABC-1D23</c>), or <c>null</c> when the alert has none.</param>
/// <param name="Source">Where the alert came from.</param>
/// <param name="Urgent">Whether the alert jumps ahead of the normal ones.</param>
/// <param name="CreatedAt">When the alert was created; the 30 minute expiry counts from here.</param>
/// <param name="SenderId">Who sent it (Telegram id, site user), or <c>null</c> when unknown.</param>
/// <param name="Original">The text exactly as the sender wrote it, or <c>null</c> when there is none.</param>
/// <param name="Corrected">Whether the correction changed the words of the text (the window then shows "Corrigido. Original: ...").</param>
public sealed record QueueItem(
    string Id,
    string Text,
    string? Plate,
    AlertSource Source,
    bool Urgent,
    DateTimeOffset CreatedAt,
    string? SenderId,
    string? Original,
    bool Corrected = false);
