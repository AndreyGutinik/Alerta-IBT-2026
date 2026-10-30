using System.IO;
using Path = System.Windows.Shapes.Path;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AlertaIBT.App.Platform;
using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Backup;
using AutomationProperties = System.Windows.Automation.AutomationProperties;

namespace AlertaIBT.App.Views;

/// <summary>
/// Asks for the password of a backup. To make a backup it asks twice (the second box must match; at least 8 characters). To open one it asks once
/// and the <c>verify</c> callback gives the problem with the password typed, shown inside the window, which stays open for another try. The eye
/// button shows or hides what is typed. The password lives only in the boxes and in <see cref="Answer"/>; it is never logged or kept.
/// </summary>
internal sealed class PasswordDialog : ThemedDialog
{
    private readonly bool _confirm;
    private readonly Func<string, string?>? _verify;
    private bool _syncing;
    private bool _revealed;

    private PasswordDialog(string title, bool confirm, Func<string, string?>? verify, AppWindowPolicy policy, Window? owner)
        : base(title, 420, policy)
    {
        _confirm = confirm;
        _verify = verify;
        Hidden1 = MakePasswordBox("Senha");
        Shown1 = MakeTextBox("Senha");
        Hidden2 = MakePasswordBox("Repita a senha");
        Shown2 = MakeTextBox("Repita a senha");
        Link(Hidden1, Shown1);
        Link(Hidden2, Shown2);

        EyeButton = new Button { Width = 40, Height = 40, Margin = new Thickness(8, 0, 0, 0), Content = EyeIcon(false) };
        if (Application.Current?.TryFindResource("IbtToolButton") is Style tool)
        {
            EyeButton.Style = tool;
            EyeButton.Width = 40;
            EyeButton.Height = 40;
        }

        EyeButton.ToolTip = "Mostrar a senha";
        AutomationProperties.SetName(EyeButton, "Mostrar ou esconder a senha");
        EyeButton.Click += (_, _) => SetRevealed(!_revealed);

        Problem = new TextBlock { MinHeight = 18, Margin = new Thickness(0, 8, 0, 0), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Problem.SetResourceReference(TextBlock.ForegroundProperty, "IbtWarnBrush");

        OkButton = MakeButton(confirm ? "Continuar" : "Abrir", "IbtPrimaryButton", Commit, isDefault: true);
        CancelButton = MakeButton("Cancelar", "IbtButton", () => Finish(null), isCancel: true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(CancelButton);
        buttons.Children.Add(OkButton);

        var first = new Grid();
        first.ColumnDefinitions.Add(new ColumnDefinition());
        first.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var firstBoxes = new Grid();
        firstBoxes.Children.Add(Hidden1);
        firstBoxes.Children.Add(Shown1);
        first.Children.Add(firstBoxes);
        Grid.SetColumn(EyeButton, 1);
        first.Children.Add(EyeButton);

        var secondBoxes = new Grid { Margin = new Thickness(0, 0, 48, 0) };
        secondBoxes.Children.Add(Hidden2);
        secondBoxes.Children.Add(Shown2);
        SecondBlock = new StackPanel { Visibility = confirm ? Visibility.Visible : Visibility.Collapsed };
        SecondBlock.Children.Add(new Border { Height = 16 });
        SecondBlock.Children.Add(MakeBody("Repita a senha"));
        SecondBlock.Children.Add(new Border { Height = 6 });
        SecondBlock.Children.Add(secondBoxes);

        var layout = new StackPanel();
        layout.Children.Add(MakeTitle(title));
        layout.Children.Add(MakeBody(confirm ? "Senha (m\U000000EDnimo de 8 caracteres)" : "Senha"));
        layout.Children.Add(new Border { Height = 6 });
        layout.Children.Add(first);
        layout.Children.Add(SecondBlock);
        layout.Children.Add(Problem);
        layout.Children.Add(buttons);
        Build(layout, owner);

        SetRevealed(false);
        Validate();
        Loaded += (_, _) => Hidden1.Focus();
    }

    /// <summary>The password accepted, or null when cancelled.</summary>
    internal string? Answer { get; private set; }

    /// <summary>The first box, hidden form.</summary>
    internal PasswordBox Hidden1 { get; }

    /// <summary>The first box, shown form.</summary>
    internal TextBox Shown1 { get; }

    /// <summary>The second box (asked when making a backup), hidden form.</summary>
    internal PasswordBox Hidden2 { get; }

    /// <summary>The second box, shown form.</summary>
    internal TextBox Shown2 { get; }

    /// <summary>The eye button.</summary>
    internal Button EyeButton { get; }

    /// <summary>The line that tells what is wrong with the password.</summary>
    internal TextBlock Problem { get; }

    /// <summary>The "Continuar" / "Abrir" button.</summary>
    internal Button OkButton { get; }

    /// <summary>The "Cancelar" button.</summary>
    internal Button CancelButton { get; }

    /// <summary>The repeat block (collapsed when only one password is asked).</summary>
    internal StackPanel SecondBlock { get; }

    /// <summary>Whether what is typed is shown as letters.</summary>
    internal bool Revealed => _revealed;

    /// <summary>Shows the dialog that asks for a new password twice. Null when cancelled.</summary>
    public static string? AskNew(string title, AppWindowPolicy policy)
    {
        var dialog = CreateNew(title, policy);
        dialog.ShowDialog();
        return dialog.Answer;
    }

    /// <summary>Shows the dialog that asks for the password of a file; <paramref name="verify"/> says what is wrong with a try.</summary>
    public static string? Ask(string title, Func<string, string?> verify, AppWindowPolicy policy)
    {
        var dialog = CreateOpen(title, verify, policy);
        dialog.ShowDialog();
        return dialog.Answer;
    }

    /// <summary>Builds the new-password dialog without showing it.</summary>
    internal static PasswordDialog CreateNew(string title, AppWindowPolicy policy) => new(title, true, null, policy, MainWindowOnScreen());

    /// <summary>Builds the open-password dialog without showing it.</summary>
    internal static PasswordDialog CreateOpen(string title, Func<string, string?> verify, AppWindowPolicy policy) => new(title, false, verify, policy, MainWindowOnScreen());

    /// <summary>Types into the first box (tests and the paste of a password).</summary>
    internal void SetFirst(string text) => Shown1.Text = text;

    /// <summary>Types into the second box.</summary>
    internal void SetSecond(string text) => Shown2.Text = text;

    /// <summary>Accepts the password when it can be used.</summary>
    internal void Commit()
    {
        var text = Shown1.Text;
        if (_confirm)
        {
            if (!BackupCrypto.IsAcceptablePassword(text) || text != Shown2.Text)
            {
                return;
            }
        }
        else if (text.Length == 0)
        {
            return;
        }
        else if (_verify?.Invoke(text) is { } problem)
        {
            Problem.Text = problem;
            return;
        }

        Finish(text);
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

    private static Path EyeIcon(bool revealed)
    {
        var path = new Path { Width = 16, Height = 16, Stretch = Stretch.Uniform };
        if (Application.Current?.TryFindResource("LucideIcon") is Style style)
        {
            path.Style = style;
        }

        path.Data = Application.Current?.TryFindResource(revealed ? "LucideEyeOff" : "LucideEye") as Geometry;
        return path;
    }

    private PasswordBox MakePasswordBox(string name)
    {
        var box = new PasswordBox { Height = 40, Padding = new Thickness(10, 11, 10, 0), FontSize = 13 };
        if (Application.Current?.TryFindResource("IbtPasswordInput") is Style style)
        {
            box.Style = style;
        }

        AutomationProperties.SetName(box, name);
        return box;
    }

    private TextBox MakeTextBox(string name)
    {
        var box = new TextBox { Height = 40, Padding = new Thickness(10, 11, 10, 0), FontSize = 13 };
        if (Application.Current?.TryFindResource("IbtInput") is Style style)
        {
            box.Style = style;
        }

        AutomationProperties.SetName(box, name + " (visivel)");
        return box;
    }

    // The two boxes of one field hold the same text: the one on screen is the one that is typed in.
    private void Link(PasswordBox hidden, TextBox shown)
    {
        hidden.PasswordChanged += (_, _) =>
        {
            if (_syncing)
            {
                return;
            }

            _syncing = true;
            shown.Text = hidden.Password;
            _syncing = false;
            Validate();
        };
        shown.TextChanged += (_, _) =>
        {
            if (_syncing)
            {
                return;
            }

            _syncing = true;
            hidden.Password = shown.Text;
            _syncing = false;
            Validate();
        };
    }

    private void SetRevealed(bool revealed)
    {
        _revealed = revealed;
        Hidden1.Visibility = revealed ? Visibility.Collapsed : Visibility.Visible;
        Shown1.Visibility = revealed ? Visibility.Visible : Visibility.Collapsed;
        Hidden2.Visibility = revealed ? Visibility.Collapsed : Visibility.Visible;
        Shown2.Visibility = revealed ? Visibility.Visible : Visibility.Collapsed;
        EyeButton.Content = EyeIcon(revealed);
        EyeButton.ToolTip = revealed ? "Esconder a senha" : "Mostrar a senha";
        if (revealed)
        {
            Shown1.Focus();
        }
        else
        {
            Hidden1.Focus();
        }
    }

    private void Validate()
    {
        var first = Shown1.Text;
        if (!_confirm)
        {
            Problem.Text = string.Empty;
            OkButton.IsEnabled = first.Length > 0;
            return;
        }

        string problem = string.Empty;
        if (first.Length > 0 && !BackupCrypto.IsAcceptablePassword(first))
        {
            problem = "A senha precisa ter pelo menos 8 caracteres.";
        }
        else if (Shown2.Text.Length > 0 && first != Shown2.Text)
        {
            problem = "As senhas n\U000000E3o conferem.";
        }

        Problem.Text = problem;
        OkButton.IsEnabled = BackupCrypto.IsAcceptablePassword(first) && first == Shown2.Text;
    }
}

/// <summary>
/// What a restore would replace, one block per item that changes: the name, the value now and the value in the backup. Secrets arrive already
/// masked. Nothing has changed yet; "Continuar" goes on to the last question, "Cancelar" closes.
/// </summary>
internal sealed class RestorePreviewDialog : ThemedDialog
{
    private RestorePreviewDialog(IReadOnlyList<BackupChange> changes, AppWindowPolicy policy, Window? owner)
        : base("O que ser\U000000E1 restaurado", 600, policy)
    {
        var changed = changes.Where(change => change.Changed).ToList();
        ContinueButton = MakeButton("Continuar", "IbtPrimaryButton", () => Finish(true));
        CancelButton = MakeButton("Cancelar", "IbtButton", () => Finish(false), isDefault: true, isCancel: true);
        ContinueButton.IsEnabled = changed.Count > 0;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        buttons.Children.Add(CancelButton);
        buttons.Children.Add(ContinueButton);

        Rows = new StackPanel();
        foreach (var change in changed)
        {
            Rows.Children.Add(MakeRow(change));
        }

        var layout = new StackPanel();
        layout.Children.Add(MakeTitle("O que ser\U000000E1 restaurado"));
        if (changed.Count == 0)
        {
            layout.Children.Add(MakeBody("Nada vai mudar: o backup \U000000E9 igual \U000000E0s configura\U000000E7\U000000F5es de agora."));
        }
        else
        {
            layout.Children.Add(MakeBody(changed.Count == 1 ? "1 item ser\U000000E1 trocado." : changed.Count + " itens ser\U000000E3o trocados."));
            layout.Children.Add(new Border { Height = 12 });
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 360, Content = Rows };
            layout.Children.Add(scroll);
            if (changes.Count > changed.Count)
            {
                var same = MakeBody(changes.Count - changed.Count == 1 ? "1 item j\U000000E1 est\U000000E1 igual." : changes.Count - changed.Count + " itens j\U000000E1 est\U000000E3o iguais.");
                same.Margin = new Thickness(0, 8, 0, 0);
                same.FontSize = 12;
                layout.Children.Add(same);
            }
        }

        layout.Children.Add(buttons);
        Build(layout, owner);
        Loaded += (_, _) => CancelButton.Focus();
    }

    /// <summary>True when the owner chose Continuar; closing the window is Cancelar.</summary>
    internal bool Answer { get; private set; }

    /// <summary>The "Continuar" button.</summary>
    internal Button ContinueButton { get; }

    /// <summary>The "Cancelar" button.</summary>
    internal Button CancelButton { get; }

    /// <summary>The blocks of the items that change.</summary>
    internal StackPanel Rows { get; }

    /// <summary>Shows the preview and waits; true to go on.</summary>
    public static bool Ask(IReadOnlyList<BackupChange> changes, AppWindowPolicy policy)
    {
        var dialog = Create(changes, policy);
        dialog.ShowDialog();
        return dialog.Answer;
    }

    /// <summary>Builds the dialog without showing it.</summary>
    internal static RestorePreviewDialog Create(IReadOnlyList<BackupChange> changes, AppWindowPolicy policy) => new(changes, policy, MainWindowOnScreen());

    /// <summary>Records the answer and closes.</summary>
    internal void Finish(bool answer)
    {
        Answer = answer;
        if (IsVisible)
        {
            Close();
        }
    }

    private static Border MakeRow(BackupChange change)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(168) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (change.IsSecret && Application.Current?.TryFindResource("LucideLock") is Geometry lockIcon)
        {
            var icon = new Path { Width = 12, Height = 12, Data = lockIcon, Margin = new Thickness(0, 0, 6, 0), Stretch = Stretch.Uniform };
            if (Application.Current?.TryFindResource("LucideIcon") is Style style)
            {
                icon.Style = style;
            }

            label.Children.Add(icon);
        }

        var name = new TextBlock { Text = change.Label, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = change.Label };
        name.SetResourceReference(TextBlock.ForegroundProperty, "IbtTextSecondaryBrush");
        label.Children.Add(name);
        grid.Children.Add(label);

        var old = Value(change.Old, "IbtMutedBrush", FontWeights.Normal);
        Grid.SetColumn(old, 1);
        grid.Children.Add(old);

        var arrow = new TextBlock { Text = "\U00002192", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
        arrow.SetResourceReference(TextBlock.ForegroundProperty, "IbtMutedBrush");
        Grid.SetColumn(arrow, 2);
        grid.Children.Add(arrow);

        var @new = Value(change.New, "IbtTextBrush", FontWeights.SemiBold);
        Grid.SetColumn(@new, 3);
        grid.Children.Add(@new);

        var block = new Border { Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = grid };
        block.SetResourceReference(Border.BorderBrushProperty, "IbtBorderBrush");
        block.SetResourceReference(Border.BackgroundProperty, "IbtInsetBrush");
        return block;
    }

    private static TextBlock Value(string text, string brushKey, FontWeight weight)
    {
        var block = new TextBlock { Text = text, FontSize = 12.5, FontWeight = weight, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = text };
        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return block;
    }
}

/// <summary>The real windows of the Backup card: the system file and folder pickers over the main window, and the themed dialogs.</summary>
internal sealed class BackupUi(AppWindowPolicy policy) : IBackupUi
{
    private const string Filter = "Backup do IBT Controller (*.ibtbackup)|*.ibtbackup";

    /// <inheritdoc />
    public string? PickSavePath(string suggestedName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exportar backup",
            FileName = suggestedName,
            DefaultExt = BackupCrypto.Extension,
            Filter = Filter,
            AddExtension = true,
        };
        return Show(dialog.ShowDialog, dialog.ShowDialog) ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public string? PickOpenPath()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Restaurar backup", DefaultExt = BackupCrypto.Extension, Filter = Filter, CheckFileExists = true };
        return Show(dialog.ShowDialog, dialog.ShowDialog) ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public string? PickFolder(string? current)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Pasta das c\U000000F3pias autom\U000000E1ticas", Multiselect = false };
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        return Show(dialog.ShowDialog, dialog.ShowDialog) && !string.IsNullOrWhiteSpace(dialog.FolderName) ? dialog.FolderName : null;
    }

    /// <inheritdoc />
    public string? AskNewPassword(string title) => PasswordDialog.AskNew(title, policy);

    /// <inheritdoc />
    public string? AskPassword(string title, Func<string, string?> verify) => PasswordDialog.Ask(title, verify, policy);

    /// <inheritdoc />
    public bool ShowPreview(IReadOnlyList<BackupChange> changes) => RestorePreviewDialog.Ask(changes, policy);

    /// <inheritdoc />
    public bool ConfirmRestore()
    {
        var dialog = ConfirmDialog.Create(
            "Restaurar este backup?",
            "Os itens da lista v\U000000E3o ser\U000000E3o trocados agora. O resto do app n\U000000E3o muda.",
            policy,
            "Restaurar",
            "Cancelar");
        dialog.ShowDialog();
        return dialog.Answer;
    }

    // Over the main window, which is never on the LED. (A system file dialog cannot be hidden from capture before it shows.)
    private static bool Show(Func<bool?> noOwner, Func<Window, bool?> withOwner)
    {
        var owner = Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault(window => window.IsVisible);
        return (owner is null ? noOwner() : withOwner(owner)) == true;
    }
}
