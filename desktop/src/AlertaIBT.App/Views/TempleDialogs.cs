using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using AlertaIBT.App.Platform;
using AlertaIBT.Core.Temple;

namespace AlertaIBT.App.Views;

/// <summary>The icon of each function of "Templo menor" (the same in the dialogs and in the tooltips).</summary>
internal static class TempleIcons
{
    /// <summary>The resource key of the Lucide geometry of a function.</summary>
    public static string Key(TempleFeature feature) => feature switch
    {
        TempleFeature.Alerts => "LucideBell",
        TempleFeature.Player => "LucideMusic",
        TempleFeature.StartupAudio => "LucideSpeaker",
        TempleFeature.Ndi => "LucideMonitorPlay",
        TempleFeature.Ma2 => "LucideGamepad2",
        TempleFeature.Holyrics => "LucidePresentation",
        TempleFeature.Gemini => "LucideSparkles",
        _ => "LucideSettings",
    };

    /// <summary>A small icon, stroked with the secondary text color (or <paramref name="brushKey"/>).</summary>
    public static Path Make(string geometryKey, double size = 16, string brushKey = "IbtTextSecondaryBrush")
    {
        var path = new Path { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        if (Application.Current?.TryFindResource(geometryKey) is Geometry geometry)
        {
            path.Data = geometry;
        }

        if (Application.Current?.TryFindResource("LucideIcon") is Style style)
        {
            path.Style = style;
        }

        path.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return path;
    }
}

/// <summary>
/// The gear of "Templo menor": one switch per function that can be turned off in this mode, each with its icon and a tooltip that says what it stops. Marked = off in
/// the smaller temple. Nothing is marked the first time: the owner picks. Salvar answers the new list; Cancelar (or closing) answers nothing.
/// </summary>
internal sealed class TempleGearDialog : ThemedDialog
{
    private readonly Dictionary<TempleFeature, ToggleButton> _switches = [];

    private TempleGearDialog(IReadOnlyCollection<TempleFeature> disabled, AppWindowPolicy policy, Window? owner)
        : base("Fun\U000000E7\U000000F5es desligadas", 440, policy)
    {
        SaveButton = MakeButton("Salvar", "IbtPrimaryButton", () => Finish(Chosen()), isDefault: true);
        CancelButton = MakeButton("Cancelar", "IbtButton", () => Finish(null), isCancel: true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(CancelButton);
        buttons.Children.Add(SaveButton);

        var layout = new StackPanel();
        layout.Children.Add(MakeTitle("Fun\U000000E7\U000000F5es desligadas"));
        layout.Children.Add(MakeBody("Marque o que este templo n\U000000E3o usa."));
        var list = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var feature in TempleFeatures.All)
        {
            list.Children.Add(Row(feature, disabled.Contains(feature)));
        }

        layout.Children.Add(list);
        layout.Children.Add(buttons);
        Build(layout, owner);
        Loaded += (_, _) => SaveButton.Focus();
    }

    /// <summary>The list the owner chose; null when cancelled.</summary>
    internal IReadOnlyList<TempleFeature>? Answer { get; private set; }

    /// <summary>The switch of a function (marked = off).</summary>
    internal ToggleButton SwitchOf(TempleFeature feature) => _switches[feature];

    /// <summary>The Salvar button.</summary>
    internal Button SaveButton { get; }

    /// <summary>The Cancelar button.</summary>
    internal Button CancelButton { get; }

    /// <summary>Builds the dialog without showing it.</summary>
    internal static TempleGearDialog Create(IReadOnlyCollection<TempleFeature> disabled, AppWindowPolicy policy) =>
        new(disabled, policy, MainWindowOnScreen());

    /// <summary>Shows the dialog over the main window and waits.</summary>
    public static IReadOnlyList<TempleFeature>? Ask(IReadOnlyCollection<TempleFeature> disabled, AppWindowPolicy policy)
    {
        var dialog = Create(disabled, policy);
        dialog.ShowDialog();
        return dialog.Answer;
    }

    /// <summary>Records the answer and closes.</summary>
    internal void Finish(IReadOnlyList<TempleFeature>? answer)
    {
        Answer = answer;
        if (IsVisible)
        {
            Close();
        }
    }

    private List<TempleFeature> Chosen() => TempleFeatures.All.Where(f => _switches[f].IsChecked == true).ToList();

    private Border Row(TempleFeature feature, bool off)
    {
        var title = TempleFeatures.Title(feature);
        var toggle = new ToggleButton { IsChecked = off, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        if (Application.Current?.TryFindResource("IbtSwitch") is Style style)
        {
            toggle.Style = style;
        }

        System.Windows.Automation.AutomationProperties.SetName(toggle, title);
        _switches[feature] = toggle;

        var icon = TempleIcons.Make(TempleIcons.Key(feature), 18);
        icon.Margin = new Thickness(0, 0, 12, 0);
        var text = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        text.SetResourceReference(TextBlock.ForegroundProperty, "IbtTextBrush");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(toggle, 2);
        grid.Children.Add(toggle);

        var row = new Border { Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = grid, ToolTip = TempleFeatures.Detail(feature) };
        row.SetResourceReference(Border.BackgroundProperty, "IbtSurfaceRaisedBrush");
        row.SetResourceReference(Border.BorderBrushProperty, "IbtBorderBrush");
        return row;
    }
}

/// <summary>
/// The confirmation of "Templo menor": what will stop, what will start and the new telao size. Nothing changes before Confirmar; Cancelar is the default (Enter and Esc keep
/// everything).
/// </summary>
internal sealed class TempleConfirmDialog : ThemedDialog
{
    private TempleConfirmDialog(TemplePlan plan, AppWindowPolicy policy, Window? owner)
        : base(plan.WillBeOn ? "Ligar o Templo menor" : plan.WasOn ? "Desligar o Templo menor" : "Salvar as fun\U000000E7\U000000F5es", 440, policy)
    {
        ConfirmButton = MakeButton("Confirmar", "IbtPrimaryButton", () => Finish(true));
        CancelButton = MakeButton("Cancelar", "IbtButton", () => Finish(false), isDefault: true, isCancel: true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(CancelButton);
        buttons.Children.Add(ConfirmButton);

        var layout = new StackPanel();
        layout.Children.Add(MakeTitle(Title));
        if (plan.Stops.Count > 0)
        {
            layout.Children.Add(Group("Vai parar", "LucidePause", plan.Stops));
        }

        if (plan.Starts.Count > 0)
        {
            layout.Children.Add(Group("Volta a funcionar", "LucidePlay", plan.Starts));
        }

        layout.Children.Add(SizeLine(plan));
        layout.Children.Add(buttons);
        Build(layout, owner);
        Loaded += (_, _) => CancelButton.Focus();
    }

    /// <summary>What the owner chose: true for Confirmar. Closing the window is Cancelar.</summary>
    internal bool Answer { get; private set; }

    /// <summary>The Confirmar button.</summary>
    internal Button ConfirmButton { get; }

    /// <summary>The Cancelar button.</summary>
    internal Button CancelButton { get; }

    /// <summary>Builds the dialog without showing it.</summary>
    internal static TempleConfirmDialog Create(TemplePlan plan, AppWindowPolicy policy) => new(plan, policy, MainWindowOnScreen());

    /// <summary>Shows the dialog over the main window and waits; true only for Confirmar.</summary>
    public static bool Ask(TemplePlan plan, AppWindowPolicy policy)
    {
        var dialog = Create(plan, policy);
        dialog.ShowDialog();
        return dialog.Answer;
    }

    /// <summary>Records the answer and closes.</summary>
    internal void Finish(bool answer)
    {
        Answer = answer;
        if (IsVisible)
        {
            Close();
        }
    }

    private static UIElement Group(string heading, string iconKey, IReadOnlyList<TempleFeature> features)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(TempleIcons.Make(iconKey, 14, "IbtTextSecondaryBrush"));
        var label = new TextBlock { Text = heading, Margin = new Thickness(8, 0, 0, 0), FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "IbtTextSecondaryBrush");
        head.Children.Add(label);
        panel.Children.Add(head);
        foreach (var feature in features)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6), ToolTip = TempleFeatures.Detail(feature) };
            var icon = TempleIcons.Make(TempleIcons.Key(feature), 16, "IbtTextBrush");
            icon.Margin = new Thickness(0, 0, 10, 0);
            line.Children.Add(icon);
            var name = new TextBlock { Text = TempleFeatures.Title(feature), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            name.SetResourceReference(TextBlock.ForegroundProperty, "IbtTextBrush");
            line.Children.Add(name);
            panel.Children.Add(line);
        }

        return panel;
    }

    private static UIElement SizeLine(TemplePlan plan)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0), ToolTip = "Tamanho do tel\U000000E3o" };
        line.Children.Add(TempleIcons.Make("LucideMonitor", 16, "IbtTextSecondaryBrush"));
        var text = plan.From == plan.To
            ? plan.To.ToString()
            : plan.From + "  \U00002192  " + plan.To;
        var size = new TextBlock { Text = text, Margin = new Thickness(10, 0, 0, 0), FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        size.SetResourceReference(TextBlock.ForegroundProperty, "IbtTextBrush");
        line.Children.Add(size);
        return line;
    }
}
