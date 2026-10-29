using AlertaIBT.App.Platform;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.App.Views;

/// <summary>The "Sair do IBT Controller?" confirmation: a window like any other, so it is out of every screen capture before it is shown.</summary>
internal static class ExitDialog
{
    /// <summary>Builds the dialog (not shown), already excluded from capture.</summary>
    public static Wpf.Ui.Controls.MessageBox Create(ILogger? logger = null)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Sair do IBT Controller?",
            Content = "Os avisos param de aparecer no tel\U000000E3o at\U000000E9 o app ser aberto de novo.",
            PrimaryButtonText = "Sair",
            CloseButtonText = "Cancelar",
        };
        CaptureExclusion.Apply(box, logger);
        return box;
    }
}
