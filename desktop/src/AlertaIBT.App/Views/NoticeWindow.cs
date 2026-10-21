using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using AlertaIBT.App.Platform;

namespace AlertaIBT.App.Views;

/// <summary>
/// A small modal notice (title, text, one or two buttons) in the look of the app, used for the one-time warning about the old
/// app. It replaces the system message box, which cannot be hidden from screen capture before it is shown: this window is
/// excluded from capture before its first pixel and opens on a monitor that is not the LED.
/// </summary>
internal sealed class NoticeWindow : Window
{
    private readonly AppWindowPolicy _policy;

    private NoticeWindow(string title, string message, bool askYesNo, AppWindowPolicy policy)
    {
        _policy = policy;
        Title = title;
        Width = 560;
        Height = 330;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SetResourceReference(BackgroundProperty, "IbtBackgroundBrush");
        SetResourceReference(ForegroundProperty, "IbtTextBrush");
        SetResourceReference(FontFamilyProperty, "MarbleFont");

        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        var scroll = new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 16) };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (askYesNo)
        {
            buttons.Children.Add(MakeButton("Sim", () => Finish(true), isDefault: true, margin: new Thickness(0, 0, 8, 0)));
            buttons.Children.Add(MakeButton("N\U000000E3o", () => Finish(false), isDefault: false, margin: default));
        }
        else
        {
            buttons.Children.Add(MakeButton("OK", () => Finish(false), isDefault: true, margin: default));
        }

        var layout = new Grid { Margin = new Thickness(24) };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(scroll, 0);
        Grid.SetRow(buttons, 1);
        layout.Children.Add(scroll);
        layout.Children.Add(buttons);
        Content = layout;

        // The handle is made now, hidden: out of every screen capture before the first pixel (the LED is captured).
        _policy.Exclude(this);
    }

    /// <summary>What the owner chose: true for "Sim".</summary>
    internal bool Answer { get; private set; }

    /// <summary>Shows the notice and waits for the answer. Returns true when the owner answered "Sim".</summary>
    /// <param name="title">The title.</param>
    /// <param name="message">The text.</param>
    /// <param name="askYesNo">Sim/N\U000000E3o when true, only OK when false.</param>
    /// <param name="policy">Where windows may open and the capture exclusion.</param>
    public static bool Ask(string title, string message, bool askYesNo, AppWindowPolicy policy)
    {
        var window = Create(title, message, askYesNo, policy);
        window.ShowDialog();
        return window.Answer;
    }

    /// <summary>Builds the window, excluded from capture and placed off the LED, without showing it.</summary>
    internal static NoticeWindow Create(string title, string message, bool askYesNo, AppWindowPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var window = new NoticeWindow(title, message, askYesNo, policy);
        policy.CenterOnChosen(new WindowInteropHelper(window).Handle);
        return window;
    }

    /// <summary>Records the answer and closes the notice.</summary>
    internal void Finish(bool answer)
    {
        Answer = answer;
        Close();
    }

    private static Button MakeButton(string text, Action click, bool isDefault, Thickness margin)
    {
        var button = new Button { Content = text, MinWidth = 90, Margin = margin, IsDefault = isDefault, IsCancel = !isDefault };
        button.Click += (_, _) => click();
        return button;
    }
}
