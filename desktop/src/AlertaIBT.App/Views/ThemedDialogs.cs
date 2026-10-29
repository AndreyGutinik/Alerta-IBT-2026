using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using AlertaIBT.App.Platform;

namespace AlertaIBT.App.Views;

/// <summary>
/// The look of the small dialogs of the app (a question with Sim / Nao, a box that asks for a name): a dark card with a thin border, a title, a
/// short text and buttons, as a window of its own. Like every window of the app it is excluded from screen capture before its first pixel
/// (the LED is captured) and it never opens on the LED monitor. It opens over the main window when that is on screen.
/// </summary>
internal abstract class ThemedDialog : Window
{
    private readonly AppWindowPolicy _policy;

    protected ThemedDialog(string title, double width, AppWindowPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        _policy = policy;
        Title = title;
        Width = width;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.Manual;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SetResourceReference(BackgroundProperty, "IbtSurfaceBrush");
        SetResourceReference(ForegroundProperty, "IbtTextBrush");
        SetResourceReference(FontFamilyProperty, "MarbleFont");
        FontSize = 13;
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not (TextBox or Button))
            {
                DragMove();
            }
        };
    }

    /// <summary>The title as shown in the window (also its automation name).</summary>
    protected TextBlock MakeTitle(string title)
    {
        var text = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "IbtTextBrush");
        return text;
    }

    /// <summary>The quiet paragraph under the title.</summary>
    protected TextBlock MakeBody(string message)
    {
        var text = new TextBlock { Text = message, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, "IbtTextSecondaryBrush");
        return text;
    }

    /// <summary>A button of the row at the bottom: 40 px high, at least 88 wide.</summary>
    protected static Button MakeButton(string text, string styleKey, Action click, bool isDefault = false, bool isCancel = false)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 88,
            Height = 40,
            Padding = new Thickness(18, 0, 18, 0),
            IsDefault = isDefault,
            IsCancel = isCancel,
            Margin = new Thickness(8, 0, 0, 0),
        };
        if (Application.Current?.TryFindResource(styleKey) is Style style)
        {
            button.Style = style;
        }

        System.Windows.Automation.AutomationProperties.SetName(button, text);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>Puts the content in the card, sizes the window to it, makes the handle (excluded from capture) and places the window.</summary>
    protected void Build(UIElement content, Window? owner)
    {
        var card = new Border { Padding = new Thickness(24), BorderThickness = new Thickness(1), Child = content };
        card.SetResourceReference(Border.BorderBrushProperty, "IbtBorderStrongBrush");
        card.SetResourceReference(Border.BackgroundProperty, "IbtSurfaceBrush");
        Content = card;

        card.Measure(new Size(Width, double.PositiveInfinity));
        Height = Math.Ceiling(card.DesiredSize.Height);

        // The handle is made now, hidden: out of every screen capture before the first pixel.
        _policy.Exclude(this);
        if (owner is { IsVisible: true } && !ReferenceEquals(owner, this))
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            _policy.CenterOnChosen(new WindowInteropHelper(this).Handle);
        }
    }

    /// <summary>The main window, when it is on screen: the dialogs open over it.</summary>
    protected static Window? MainWindowOnScreen() =>
        Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault(window => window.IsVisible);
}

/// <summary>A question with two answers, <c>Sim</c> and <c>Nao</c> (the delete of a list or an alarm). <c>Nao</c> is the default: Enter and Esc keep everything.</summary>
internal sealed class ConfirmDialog : ThemedDialog
{
    private ConfirmDialog(string title, string message, string yes, string no, AppWindowPolicy policy, Window? owner)
        : base(title, 440, policy)
    {
        YesButton = MakeButton(yes, "IbtDangerButton", () => Finish(true));
        NoButton = MakeButton(no, "IbtButton", () => Finish(false), isDefault: true, isCancel: true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        buttons.Children.Add(YesButton);
        buttons.Children.Add(NoButton);

        var layout = new StackPanel();
        layout.Children.Add(MakeTitle(title));
        layout.Children.Add(MakeBody(message));
        layout.Children.Add(buttons);
        Build(layout, owner);
        Loaded += (_, _) => NoButton.Focus();
    }

    /// <summary>What the owner chose: true for Sim. Nothing chosen (the window was closed) is Nao.</summary>
    internal bool Answer { get; private set; }

    /// <summary>The "Sim" button.</summary>
    internal Button YesButton { get; }

    /// <summary>The "Nao" button.</summary>
    internal Button NoButton { get; }

    /// <summary>Shows the question over the main window and waits. True only when the owner answered Sim.</summary>
    public static bool Ask(string title, string message, AppWindowPolicy policy)
    {
        var dialog = Create(title, message, policy);
        dialog.ShowDialog();
        return dialog.Answer;
    }

    /// <summary>Builds the dialog without showing it (already excluded from capture and placed).</summary>
    internal static ConfirmDialog Create(string title, string message, AppWindowPolicy policy, string yes = "Sim", string no = "N\U000000E3o") =>
        new(title, message, yes, no, policy, MainWindowOnScreen());

    /// <summary>Records the answer and closes.</summary>
    internal void Finish(bool answer)
    {
        Answer = answer;
        if (IsVisible)
        {
            Close();
        }
    }
}

/// <summary>
/// A small box that asks for one name (the new name of a playlist). The <c>check</c> gives the problem with the text typed, or null: the problem
/// shows under the box and Salvar waits until the name can be used.
/// </summary>
internal sealed class InputDialog : ThemedDialog
{
    private readonly Func<string, string?> _check;

    private InputDialog(string title, string label, string initial, Func<string, string?> check, AppWindowPolicy policy, Window? owner)
        : base(title, 420, policy)
    {
        _check = check;
        Box = new TextBox { Text = initial, Height = 40, Padding = new Thickness(10, 11, 10, 0), FontSize = 13 };
        if (Application.Current?.TryFindResource("IbtInput") is Style input)
        {
            Box.Style = input;
        }

        System.Windows.Automation.AutomationProperties.SetName(Box, label);
        Problem = new TextBlock { MinHeight = 18, Margin = new Thickness(0, 6, 0, 0), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Problem.SetResourceReference(TextBlock.ForegroundProperty, "IbtWarnBrush");

        SaveButton = MakeButton("Salvar", "IbtPrimaryButton", Commit, isDefault: true);
        CancelButton = MakeButton("Cancelar", "IbtButton", () => Finish(null), isCancel: true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(CancelButton);
        buttons.Children.Add(SaveButton);

        var layout = new StackPanel();
        layout.Children.Add(MakeTitle(title));
        layout.Children.Add(MakeBody(label));
        layout.Children.Add(new Border { Height = 12 });
        layout.Children.Add(Box);
        layout.Children.Add(Problem);
        layout.Children.Add(buttons);
        Build(layout, owner);

        Box.TextChanged += (_, _) => Validate();
        Validate();
        Loaded += (_, _) =>
        {
            Box.Focus();
            Box.SelectAll();
        };
    }

    /// <summary>What was typed and accepted, or null when the owner cancelled.</summary>
    internal string? Answer { get; private set; }

    /// <summary>The text box.</summary>
    internal TextBox Box { get; }

    /// <summary>The line under the box that tells what is wrong with the name.</summary>
    internal TextBlock Problem { get; }

    /// <summary>The "Salvar" button.</summary>
    internal Button SaveButton { get; }

    /// <summary>The "Cancelar" button.</summary>
    internal Button CancelButton { get; }

    /// <summary>Shows the box over the main window and waits. The name, or null when cancelled.</summary>
    public static string? Ask(string title, string initial, Func<string, string?> check, AppWindowPolicy policy)
    {
        var dialog = Create(title, initial, check, policy);
        dialog.ShowDialog();
        return dialog.Answer;
    }

    /// <summary>Builds the dialog without showing it.</summary>
    internal static InputDialog Create(string title, string initial, Func<string, string?> check, AppWindowPolicy policy) =>
        new(title, "Novo nome", initial, check, policy, MainWindowOnScreen());

    /// <summary>Accepts the name when it can be used.</summary>
    internal void Commit()
    {
        if (_check(Box.Text) is null)
        {
            Finish(Box.Text.Trim());
        }
    }

    /// <summary>Records the answer and closes.</summary>
    internal void Finish(string? answer)
    {
        Answer = answer;
        if (IsVisible)
        {
            Close();
        }
    }

    private void Validate()
    {
        var problem = _check(Box.Text);
        Problem.Text = problem ?? string.Empty;
        SaveButton.IsEnabled = problem is null;
    }
}
