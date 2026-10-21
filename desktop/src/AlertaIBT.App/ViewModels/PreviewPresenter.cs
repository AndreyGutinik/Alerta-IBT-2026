using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Correction;
using AlertaIBT.Core.Services;

namespace AlertaIBT.App.ViewModels;

/// <summary>The color family of the "Vai aparecer assim" line.</summary>
public enum PreviewTone
{
    /// <summary>Nothing to show.</summary>
    None,

    /// <summary>Green: a vehicle alert, corrected online.</summary>
    Ok,

    /// <summary>Blue: an announcement (no plate), or corrected offline.</summary>
    Info,

    /// <summary>Red: it will not go to the LED.</summary>
    Error,
}

/// <summary>What the "Vai aparecer assim" block shows.</summary>
/// <param name="Tone">The color family.</param>
/// <param name="IconKey">The resource key of the Lucide geometry in front of the line.</param>
/// <param name="Label">The small heading (<c>Vai aparecer assim:</c>).</param>
/// <param name="Text">The text that would reach the bar, or the reason it would not.</param>
/// <param name="Note">A warning under the text (a repeated plate); empty when there is none.</param>
public sealed record PreviewView(PreviewTone Tone, string IconKey, string Label, string Text, string Note)
{
    /// <summary>No preview.</summary>
    public static PreviewView Empty { get; } = new(PreviewTone.None, string.Empty, string.Empty, string.Empty, string.Empty);

    /// <summary>Whether there is anything to show.</summary>
    public bool IsEmpty => Tone == PreviewTone.None;
}

/// <summary>
/// Turns what the alert service says about a typed text into the preview of the panels (spec 7.1): green "Vai aparecer assim"
/// for a vehicle alert, blue when the corrector is offline or the text is an announcement (no plate), a yellow warning for a
/// repeated plate, red when it will not go to the LED.
/// </summary>
public static class PreviewPresenter
{
    /// <summary>The heading shared by the green, blue and red lines.</summary>
    public const string OkLabel = "Vai aparecer assim:";

    private const string WandIcon = "LucideWandSparkles";
    private const string OfflineIcon = "LucideWifiOff";
    private const string ErrorIcon = "LucideCircleAlert";

    /// <summary>The red line for something that will not go to the LED.</summary>
    /// <param name="reason">Why, in a few words (<c>conte\U000000FAdo impr\U000000F3prio</c>).</param>
    public static PreviewView Error(string reason) =>
        new(PreviewTone.Error, ErrorIcon, "N\U000000E3o vai para o tel\U000000E3o:", reason, string.Empty);

    /// <summary>The preview for <paramref name="result"/>.</summary>
    public static PreviewView Present(PreviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Refused)
        {
            return Error("conte\U000000FAdo impr\U000000F3prio");
        }

        if (result.TooLong)
        {
            return Error($"texto grande demais (m\U000000E1ximo {AlertRules.MemberMaxLength} letras)");
        }

        if (result.Kind == RuleKind.Reject)
        {
            return Error(result.Missing.Count > 0 ? "falta " + string.Join(", ", result.Missing) : "nada para mostrar");
        }

        var offline = result.By == CorrectorKind.Offline;
        var free = result.Kind == RuleKind.Free;
        var tags = new List<string>(2);
        if (free)
        {
            tags.Add("aviso livre");
        }

        if (offline)
        {
            tags.Add("offline");
        }

        var label = tags.Count == 0 ? OkLabel : $"Vai aparecer assim ({string.Join(", ", tags)}):";
        var tone = tags.Count == 0 ? PreviewTone.Ok : PreviewTone.Info;
        return new PreviewView(tone, offline ? OfflineIcon : WandIcon, label, result.Text, DuplicateNote(result));
    }

    private static string DuplicateNote(PreviewResult result)
    {
        if (!result.Duplicate)
        {
            return string.Empty;
        }

        return result.DuplicateMinutesAgo is { } minutes
            ? $"Esse carro j\U000000E1 passou h\U000000E1 {Math.Max(1, minutes)} min. Pode enviar mesmo assim."
            : "Esse carro j\U000000E1 est\U000000E1 na fila. Pode enviar mesmo assim.";
    }
}
