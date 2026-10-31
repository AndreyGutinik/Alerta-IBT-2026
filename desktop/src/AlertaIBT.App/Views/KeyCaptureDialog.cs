using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using AlertaIBT.App.Platform;
using AlertaIBT.Core.Holyrics;

namespace AlertaIBT.App.Views;

/// <summary>
/// "Adicionar tecla": a small themed dialog that waits for one key (the owner presses the button of the pastor's remote) and returns its virtual-key code. A modifier alone
/// is ignored (the dialog keeps waiting), Esc or "Cancelar" gives up, and a key that cannot be used (media keys and the like) says so and keeps waiting. The key is taken in
/// the dialog's own window, so nothing is injected anywhere and the presentation mode (which leaves this app's windows alone) never sees it.
/// </summary>
internal sealed class KeyCaptureDialog : ThemedDialog
{
    private readonly TextBlock _note;

    private KeyCaptureDialog(string setName, AppWindowPolicy policy, Window? owner)
        : base("Adicionar tecla", 380, policy)
    {
        CancelButton = MakeButton("Cancelar", "IbtButton", () => Finish(null), isCancel: true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(CancelButton);

        _note = new TextBlock { MinHeight = 18, Margin = new Thickness(0, 12, 0, 0), FontSize = 12, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center };
        _note.SetResourceReference(TextBlock.ForegroundProperty, "IbtWarnBrush");

        var icon = new Path { Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
        if (Application.Current?.TryFindResource("LucideKeyboard") is System.Windows.Media.Geometry geometry)
        {
            icon.Data = geometry;
        }

        if (Application.Current?.TryFindResource("LucideIcon") is Style style)
        {
            icon.Style = style;
        }

        var layout = new StackPanel();
        layout.Children.Add(MakeTitle("Adicionar tecla em " + setName));
        layout.Children.Add(MakeBody("Aperte agora a tecla do controle."));
        layout.Children.Add(icon);
        layout.Children.Add(_note);
        layout.Children.Add(buttons);
        Build(layout, owner);
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => Focus();
    }

    /// <summary>The key that was pressed (virtual-key code), or null when cancelled.</summary>
    internal int? Result { get; private set; }

    /// <summary>The "Cancelar" button.</summary>
    internal Button CancelButton { get; }

    /// <summary>The line under the icon (why the last key was not taken).</summary>
    internal string Note => _note.Text;

    /// <summary>Shows the dialog over the main window and waits. The virtual-key code of the key pressed, or null.</summary>
    /// <param name="setName">The set the key is for (<c>Pr\U000000F3ximo</c>, <c>Anterior</c>).</param>
    /// <param name="policy">Keeps the window off the LED and out of the capture.</param>
    public static int? Ask(string setName, AppWindowPolicy policy)
    {
        var dialog = new KeyCaptureDialog(setName, policy, MainWindowOnScreen());
        dialog.ShowDialog();
        return dialog.Result;
    }

    /// <summary>Builds the dialog without showing it (for tests).</summary>
    internal static KeyCaptureDialog Create(string setName, AppWindowPolicy policy) => new(setName, policy, MainWindowOnScreen());

    /// <summary>The virtual-key code of the key of an event, whichever way WPF reports it (system key with Alt, IME).</summary>
    internal static int VirtualKeyOf(Key key, Key systemKey, Key imeProcessedKey)
    {
        var real = key switch
        {
            Key.System => systemKey,
            Key.ImeProcessed => imeProcessedKey,
            _ => key,
        };
        return KeyInterop.VirtualKeyFromKey(real);
    }

    /// <summary>What a key pressed in the dialog means: a code to take, a note to show (not usable), or nothing (a modifier alone: keep waiting).</summary>
    internal static (int? Take, string? Note) Interpret(int virtualKey)
    {
        if (virtualKey is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C || virtualKey is >= 0xA0 and <= 0xA5)
        {
            return (null, null); // Shift, Ctrl, Alt, Windows alone: wait for the real key
        }

        if (virtualKey == 0x1B)
        {
            return (null, "cancel");
        }

        return PresentationKeys.IsAllowed(virtualKey)
            ? (virtualKey, null)
            : (null, PresentationKeys.Name(virtualKey) + " n\U000000E3o pode ser usada. Tente outra.");
    }

    /// <summary>Takes a key as if it was pressed (the preview handler and the tests use it).</summary>
    internal void Offer(int virtualKey)
    {
        var (take, note) = Interpret(virtualKey);
        if (take is { } key)
        {
            Finish(key);
        }
        else if (note == "cancel")
        {
            Finish(null);
        }
        else
        {
            _note.Text = note ?? string.Empty;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true; // Space and Enter must not press "Cancelar"
        if (!e.IsRepeat)
        {
            Offer(VirtualKeyOf(e.Key, e.SystemKey, e.ImeProcessedKey));
        }
    }

    private void Finish(int? key)
    {
        Result = key;
        if (IsVisible)
        {
            Close();
        }
    }
}
