using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AlertaIBT.App.Platform;
using AlertaIBT.App.Tests.Platform;
using AlertaIBT.App.Views;

namespace AlertaIBT.App.Tests.Views;

// The two small windows of the Som tab: the Sim / Nao question (delete a list or an alarm) and the box that asks for a new name. Dark like the app,
// out of every capture before their first pixel, and nothing is chosen by accident.
public sealed class ThemedDialogTests
{
    private static AppWindowPolicy Policy() => PolicyFakes.Policy(null, [PolicyFakes.Primary()], (0, 0));

    [Fact]
    public void The_question_is_a_dark_window_with_Sim_and_Nao_hidden_from_capture_and_Nao_is_the_default()
    {
        WpfTestHost.Run(() =>
        {
            var dialog = ConfirmDialog.Create("Excluir a lista \U0000201CIgreja\U0000201D?", "S\U000000F3 a lista sai do app. As m\U000000FAsicas continuam na pasta.", Policy());

            var handle = new WindowInteropHelper(dialog).Handle;
            Assert.NotEqual(IntPtr.Zero, handle);
            Assert.False(dialog.IsVisible);
            Assert.True(CaptureExclusion.IsExcluded(handle));
            Assert.Equal("Sim", dialog.YesButton.Content);
            Assert.Equal("N\U000000E3o", dialog.NoButton.Content);
            Assert.True(dialog.NoButton.IsDefault);
            Assert.True(dialog.NoButton.IsCancel);
            Assert.False(dialog.YesButton.IsDefault);
            Assert.Equal(40, dialog.YesButton.Height);

            // dark: the surface of the cards, the light text of the app
            Assert.Equal(((SolidColorBrush)Application.Current.FindResource("IbtSurfaceBrush")).Color, ((SolidColorBrush)dialog.Background).Color);
            Assert.Equal(((SolidColorBrush)Application.Current.FindResource("IbtTextBrush")).Color, ((SolidColorBrush)dialog.Foreground).Color);
            Assert.Equal(WindowStyle.None, dialog.WindowStyle);
        });
    }

    [Fact]
    public void Nao_and_a_window_that_closes_without_an_answer_keep_everything_and_Sim_says_yes()
    {
        WpfTestHost.Run(() =>
        {
            var no = ConfirmDialog.Create("t", "m", Policy());
            no.NoButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.False(no.Answer);

            var silent = ConfirmDialog.Create("t", "m", Policy());
            Assert.False(silent.Answer); // nothing chosen is No

            var yes = ConfirmDialog.Create("t", "m", Policy());
            yes.YesButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.True(yes.Answer);
        });
    }

    [Fact]
    public void The_name_box_refuses_a_bad_name_shows_why_and_returns_the_trimmed_name_otherwise()
    {
        WpfTestHost.Run(() =>
        {
            var dialog = InputDialog.Create("Renomear a lista", "Louvor", text => text.Trim().Length == 0 ? "Digite um nome." : text.Trim() == "Igreja" ? "J\U000000E1 existe uma lista com esse nome." : null, Policy());

            var handle = new WindowInteropHelper(dialog).Handle;
            Assert.True(CaptureExclusion.IsExcluded(handle));
            Assert.Equal("Louvor", dialog.Box.Text);
            Assert.True(dialog.SaveButton.IsEnabled);
            Assert.Equal(string.Empty, dialog.Problem.Text);
            Assert.Equal("Salvar", dialog.SaveButton.Content);
            Assert.Equal("Cancelar", dialog.CancelButton.Content);
            Assert.True(dialog.CancelButton.IsCancel);

            dialog.Box.Text = "Igreja";
            Assert.False(dialog.SaveButton.IsEnabled);
            Assert.Equal("J\U000000E1 existe uma lista com esse nome.", dialog.Problem.Text);
            dialog.Commit();
            Assert.Null(dialog.Answer); // a name with a problem never gets through

            dialog.Box.Text = "   ";
            Assert.False(dialog.SaveButton.IsEnabled);
            Assert.Equal("Digite um nome.", dialog.Problem.Text);

            dialog.Box.Text = "  Louvor de domingo ";
            Assert.True(dialog.SaveButton.IsEnabled);
            dialog.Commit();
            Assert.Equal("Louvor de domingo", dialog.Answer);

            var cancelled = InputDialog.Create("t", "x", _ => null, Policy());
            cancelled.CancelButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Null(cancelled.Answer);
        });
    }
}
