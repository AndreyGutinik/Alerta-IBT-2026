using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AlertaIBT.App.Platform;
using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Ndi;
using AutomationProperties = System.Windows.Automation.AutomationProperties;

namespace AlertaIBT.App.Views;

/// <summary>
/// The window of one camera button: the name the owner gives and where the picture comes from, a source the finder found (click it) or a fixed
/// address typed by hand (<c>host</c> or <c>host:port</c>). Salvar waits until both can be used.
/// </summary>
internal sealed class CameraDialog : ThemedDialog
{
    private readonly Func<string, string, string?> _check;

    private CameraDialog(CameraEditInput input, AppWindowPolicy policy, Window? owner)
        : base(input.Title, 460, policy)
    {
        _check = input.Check;
        NameBox = MakeBox(input.Name, "Nome do bot\U000000E3o");
        SourceBox = MakeBox(input.Source, "Fonte NDI ou IP da c\U000000E2mera");
        Problem = new TextBlock { MinHeight = 18, Margin = new Thickness(0, 6, 0, 0), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Problem.SetResourceReference(TextBlock.ForegroundProperty, "IbtWarnBrush");

        SaveButton = MakeButton("Salvar", "IbtPrimaryButton", Commit, isDefault: true);
        CancelButton = MakeButton("Cancelar", "IbtButton", () => Finish(null), isCancel: true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(CancelButton);
        buttons.Children.Add(SaveButton);

        SourceList = new StackPanel();
        if (input.Sources.Count == 0)
        {
            var none = MakeBody("Nenhuma fonte NDI encontrada ainda. Digite o IP da c\U000000E2mera acima.");
            none.Margin = new Thickness(12, 10, 12, 10);
            SourceList.Children.Add(none);
        }
        else
        {
            foreach (var source in input.Sources)
            {
                SourceList.Children.Add(MakeSourceRow(source));
            }
        }

        var listFrame = new Border { MaxHeight = 156, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 8, 0, 0), Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = SourceList } };
        listFrame.SetResourceReference(Border.BorderBrushProperty, "IbtBorderBrush");
        listFrame.SetResourceReference(Border.BackgroundProperty, "IbtInsetBrush");

        var layout = new StackPanel();
        layout.Children.Add(MakeTitle(input.Title));
        layout.Children.Add(MakeBody("Nome"));
        layout.Children.Add(Spacer(6));
        layout.Children.Add(NameBox);
        layout.Children.Add(Spacer(16));
        layout.Children.Add(MakeBody("Fonte: escolha da lista ou digite o IP"));
        layout.Children.Add(Spacer(6));
        layout.Children.Add(SourceBox);
        layout.Children.Add(listFrame);
        layout.Children.Add(Problem);
        layout.Children.Add(buttons);
        Build(layout, owner);

        NameBox.TextChanged += (_, _) => Validate();
        SourceBox.TextChanged += (_, _) => Validate();
        Validate();
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>What was typed and accepted, or null when the owner cancelled.</summary>
    internal CameraEditResult? Answer { get; private set; }

    /// <summary>The name box.</summary>
    internal TextBox NameBox { get; }

    /// <summary>The source box (a name from the list or an address).</summary>
    internal TextBox SourceBox { get; }

    /// <summary>The rows of the sources the finder found.</summary>
    internal StackPanel SourceList { get; }

    /// <summary>The line that tells what is wrong.</summary>
    internal TextBlock Problem { get; }

    /// <summary>The "Salvar" button.</summary>
    internal Button SaveButton { get; }

    /// <summary>The "Cancelar" button.</summary>
    internal Button CancelButton { get; }

    /// <summary>Shows the window over the main window and waits. The answer, or null when cancelled.</summary>
    public static CameraEditResult? Ask(CameraEditInput input, AppWindowPolicy policy)
    {
        var dialog = Create(input, policy);
        dialog.ShowDialog();
        return dialog.Answer;
    }

    /// <summary>Builds the window without showing it.</summary>
    internal static CameraDialog Create(CameraEditInput input, AppWindowPolicy policy) => new(input, policy, MainWindowOnScreen());

    /// <summary>Accepts the name and source when they can be used.</summary>
    internal void Commit()
    {
        if (_check(NameBox.Text, SourceBox.Text) is null)
        {
            Finish(new CameraEditResult(NameBox.Text.Trim(), SourceBox.Text.Trim()));
        }
    }

    /// <summary>Records the answer and closes.</summary>
    internal void Finish(CameraEditResult? answer)
    {
        Answer = answer;
        if (IsVisible)
        {
            Close();
        }
    }

    private static Border Spacer(double height) => new() { Height = height };

    private static TextBox MakeBox(string text, string automationName)
    {
        var box = new TextBox { Text = text, Height = 40, Padding = new Thickness(10, 11, 10, 0), FontSize = 13 };
        if (Application.Current?.TryFindResource("IbtInput") is Style input)
        {
            box.Style = input;
        }

        AutomationProperties.SetName(box, automationName);
        return box;
    }

    private Button MakeSourceRow(NdiSourceInfo source)
    {
        var text = new TextBlock { Text = source.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var row = new Button { Content = text, Height = 32, Padding = new Thickness(12, 0, 12, 0), HorizontalContentAlignment = HorizontalAlignment.Left, FontWeight = FontWeights.Normal, ToolTip = string.IsNullOrEmpty(source.Address) ? source.Name : $"{source.Name}\n{source.Address}" };
        if (Application.Current?.TryFindResource("IbtListRowButton") is Style flat)
        {
            row.Style = flat;
        }

        AutomationProperties.SetName(row, source.Name);
        row.Click += (_, _) => SourceBox.Text = source.Name;
        return row;
    }

    private void Validate()
    {
        var problem = _check(NameBox.Text, SourceBox.Text);

        // Nothing is said about a field the owner has not touched yet; the button just waits.
        Problem.Text = SourceBox.Text.Length == 0 && NameBox.IsFocused ? string.Empty : problem ?? string.Empty;
        SaveButton.IsEnabled = problem is null;
    }
}

/// <summary>The window of the fixed IPs: the finder also asks these addresses (for cameras on another network). One box with a "+", one line per address with a trash.</summary>
internal sealed class IpListDialog : ThemedDialog
{
    private readonly List<string> _ips;

    private IpListDialog(IReadOnlyList<string> ips, AppWindowPolicy policy, Window? owner)
        : base("IPs fixos", 440, policy)
    {
        _ips = ips.ToList();
        Box = new TextBox { Height = 40, Padding = new Thickness(10, 11, 10, 0), FontSize = 13 };
        if (Application.Current?.TryFindResource("IbtInput") is Style input)
        {
            Box.Style = input;
        }

        AutomationProperties.SetName(Box, "IP ou nome da c\U000000E2mera");
        AddButton = new Button { Width = 40, Height = 40, Margin = new Thickness(8, 0, 0, 0), Content = LucideGlyph("LucidePlus"), ToolTip = "Adicionar este IP" };
        if (Application.Current?.TryFindResource("IbtToolButton") is Style tool)
        {
            AddButton.Style = tool;
            AddButton.Width = 40;
            AddButton.Height = 40;
        }

        AutomationProperties.SetName(AddButton, "Adicionar IP");
        AddButton.Click += (_, _) => AddTyped();

        Rows = new StackPanel();
        Problem = new TextBlock { MinHeight = 18, Margin = new Thickness(0, 6, 0, 0), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Problem.SetResourceReference(TextBlock.ForegroundProperty, "IbtWarnBrush");

        SaveButton = MakeButton("Salvar", "IbtPrimaryButton", () => Finish(_ips.ToList()), isDefault: false);
        CancelButton = MakeButton("Cancelar", "IbtButton", () => Finish(null), isCancel: true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(CancelButton);
        buttons.Children.Add(SaveButton);

        var entry = new Grid();
        entry.ColumnDefinitions.Add(new ColumnDefinition());
        entry.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(AddButton, 1);
        entry.Children.Add(Box);
        entry.Children.Add(AddButton);

        var listFrame = new Border { Height = 168, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 12, 0, 0), Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = Rows } };
        listFrame.SetResourceReference(Border.BorderBrushProperty, "IbtBorderBrush");
        listFrame.SetResourceReference(Border.BackgroundProperty, "IbtInsetBrush");

        var layout = new StackPanel();
        layout.Children.Add(MakeTitle("IPs fixos"));
        layout.Children.Add(MakeBody("Endere\U000000E7os onde procurar c\U000000E2meras fora da sua rede, como 192.168.1.50 ou 10.0.0.8:5961."));
        layout.Children.Add(new Border { Height = 12 });
        layout.Children.Add(entry);
        layout.Children.Add(Problem);
        layout.Children.Add(listFrame);
        layout.Children.Add(buttons);
        Redraw();
        Build(layout, owner);

        Box.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                AddTyped();
                e.Handled = true;
            }
        };
        Loaded += (_, _) => Box.Focus();
    }

    /// <summary>The new list, or null when the owner cancelled.</summary>
    internal IReadOnlyList<string>? Answer { get; private set; }

    /// <summary>The box for a new address.</summary>
    internal TextBox Box { get; }

    /// <summary>The "+" button.</summary>
    internal Button AddButton { get; }

    /// <summary>The rows of the addresses.</summary>
    internal StackPanel Rows { get; }

    /// <summary>The line that tells what is wrong.</summary>
    internal TextBlock Problem { get; }

    /// <summary>The "Salvar" button.</summary>
    internal Button SaveButton { get; }

    /// <summary>The "Cancelar" button.</summary>
    internal Button CancelButton { get; }

    /// <summary>Shows the window over the main window and waits. The new list, or null when cancelled.</summary>
    public static IReadOnlyList<string>? Ask(IReadOnlyList<string> ips, AppWindowPolicy policy)
    {
        var dialog = Create(ips, policy);
        dialog.ShowDialog();
        return dialog.Answer;
    }

    /// <summary>Builds the window without showing it.</summary>
    internal static IpListDialog Create(IReadOnlyList<string> ips, AppWindowPolicy policy) => new(ips, policy, MainWindowOnScreen());

    /// <summary>Adds the typed address when it can be used.</summary>
    internal void AddTyped()
    {
        var text = Box.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (NdiSourceParser.Parse(text) is not { Url: not null })
        {
            Problem.Text = "Digite s\U000000F3 o IP ou o nome do computador (sem espa\U000000E7os), como 192.168.1.50.";
            return;
        }

        if (_ips.Contains(text, StringComparer.OrdinalIgnoreCase))
        {
            Problem.Text = "Esse IP j\U000000E1 est\U000000E1 na lista.";
            return;
        }

        _ips.Add(text);
        Box.Clear();
        Problem.Text = string.Empty;
        Redraw();
    }

    /// <summary>Records the answer and closes.</summary>
    internal void Finish(IReadOnlyList<string>? answer)
    {
        Answer = answer;
        if (IsVisible)
        {
            Close();
        }
    }

    private static Path LucideGlyph(string key)
    {
        var path = new Path { Width = 16, Height = 16 };
        if (Application.Current?.TryFindResource("LucideIcon") is Style style)
        {
            path.Style = style;
        }

        if (Application.Current?.TryFindResource(key) is Geometry geometry)
        {
            path.Data = geometry;
        }

        return path;
    }

    private void Redraw()
    {
        Rows.Children.Clear();
        if (_ips.Count == 0)
        {
            var none = MakeBody("Nenhum IP fixo. As c\U000000E2meras da sua rede aparecem sozinhas.");
            none.Margin = new Thickness(12, 16, 12, 16);
            Rows.Children.Add(none);
            return;
        }

        foreach (var ip in _ips.ToList())
        {
            var row = new Grid { Height = 40, Margin = new Thickness(12, 0, 4, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new TextBlock { Text = ip, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var remove = new Button { Content = LucideGlyph("LucideTrash2"), ToolTip = "Remover este IP", Margin = new Thickness(4, 0, 0, 0) };
            if (Application.Current?.TryFindResource("IbtDangerToolButton") is Style danger)
            {
                remove.Style = danger;
            }

            AutomationProperties.SetName(remove, $"Remover {ip}");
            remove.Click += (_, _) =>
            {
                _ips.Remove(ip);
                Redraw();
            };
            Grid.SetColumn(remove, 1);
            row.Children.Add(text);
            row.Children.Add(remove);
            Rows.Children.Add(row);
        }
    }
}
