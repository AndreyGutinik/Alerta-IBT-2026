using System.Reflection;
using System.Windows.Input;
using System.Windows.Interop;
using AlertaIBT.App.Tests.ViewModels;
using AlertaIBT.App.Tray;
using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Holyrics;
using AlertaIBT.Core.Services;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.App.Tests.Tray;

internal sealed class FakeRemote : IHolyricsRemote
{
    public List<HolyricsCommand> Posted { get; } = [];

    public bool IsConfigured { get; set; } = true;

    public bool ArrowsEnabled { get; set; } = true;

    public bool ArrowsActive => IsConfigured && ArrowsEnabled;

    public bool Post(HolyricsCommand command)
    {
        Posted.Add(command);
        return true;
    }
}

// The Left and Right arrows of the quick panel field go to Holyrics, only with the field focused and the connection set up.
public sealed class QuickPanelArrowsTests
{
    private sealed class Rig : IDisposable
    {
        private readonly HwndSource _source = new(new HwndSourceParameters("arrows-test") { Width = 1, Height = 1 });

        public Rig(IHolyricsRemote? remote)
        {
            var clock = new FakeTimeProvider();
            ViewModel = new QuickPanelViewModel(new FakeGateway(clock), new StatusHub(), clock: clock, holyrics: remote);
            Window = new QuickPanel(ViewModel);
            Window.InputBox.Text = "Honda Civic preto";
            Window.InputBox.CaretIndex = 5;
        }

        public QuickPanelViewModel ViewModel { get; }

        public QuickPanel Window { get; }

        public void Dispose()
        {
            _source.Dispose();
            ViewModel.Dispose();
        }

        // A real routed PreviewKeyDown on the field, as the keyboard makes it (IsRepeat set the way the input system does).
        public KeyEventArgs Press(Key key, bool repeat = false)
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, _source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            if (repeat)
            {
                typeof(KeyEventArgs).GetField("_isRepeat", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(args, true);
                Assert.True(args.IsRepeat);
            }

            Window.InputBox.RaiseEvent(args);
            return args;
        }
    }

    [Fact]
    public void Right_is_next_slide_and_Left_previous_slide_and_the_caret_does_not_move()
    {
        WpfTestHost.Run(() =>
        {
            var remote = new FakeRemote();
            using var rig = new Rig(remote);
            rig.Window.InputHasFocus = () => true;

            var right = rig.Press(Key.Right);
            var left = rig.Press(Key.Left);

            Assert.True(right.Handled);
            Assert.True(left.Handled);
            Assert.Equal([new HolyricsCommand(HolyricsActionKind.SlideNext), new HolyricsCommand(HolyricsActionKind.SlidePrevious)], remote.Posted);
            Assert.Equal(5, rig.Window.InputBox.CaretIndex);
            Assert.Equal("Honda Civic preto", rig.Window.InputBox.Text);
        });
    }

    [Fact]
    public void A_key_repeated_by_holding_it_is_taken_but_not_sent()
    {
        WpfTestHost.Run(() =>
        {
            var remote = new FakeRemote();
            using var rig = new Rig(remote);
            rig.Window.InputHasFocus = () => true;

            rig.Press(Key.Right);
            var held = Enumerable.Range(0, 10).Select(_ => rig.Press(Key.Right, repeat: true)).ToList();

            Assert.Single(remote.Posted);
            Assert.All(held, e => Assert.True(e.Handled)); // the caret does not run along the text either
            Assert.Equal(5, rig.Window.InputBox.CaretIndex);
        });
    }

    [Fact]
    public void Without_the_focus_in_the_field_the_arrows_are_left_alone()
    {
        WpfTestHost.Run(() =>
        {
            var remote = new FakeRemote();
            using var rig = new Rig(remote);
            rig.Window.InputHasFocus = () => false;

            var args = rig.Press(Key.Right);

            Assert.False(args.Handled);
            Assert.Empty(remote.Posted);
        });
    }

    [Fact]
    public void With_the_switch_off_the_arrows_behave_normally()
    {
        WpfTestHost.Run(() =>
        {
            var remote = new FakeRemote { ArrowsEnabled = false };
            using var rig = new Rig(remote);
            rig.Window.InputHasFocus = () => true;

            var args = rig.Press(Key.Left);

            Assert.False(args.Handled);
            Assert.Empty(remote.Posted);
        });
    }

    [Fact]
    public void Without_a_configured_connection_the_arrows_move_the_caret_as_always()
    {
        WpfTestHost.Run(() =>
        {
            var remote = new FakeRemote { IsConfigured = false };
            using var rig = new Rig(remote);
            rig.Window.InputHasFocus = () => true;

            var args = rig.Press(Key.Right);

            Assert.False(args.Handled);
            Assert.Empty(remote.Posted);
        });
    }

    [Fact]
    public void Without_a_holyrics_object_at_all_the_arrows_are_never_taken()
    {
        WpfTestHost.Run(() =>
        {
            using var rig = new Rig(null);
            rig.Window.InputHasFocus = () => true;

            Assert.False(rig.Press(Key.Right).Handled);
        });
    }

    [Fact]
    public void Other_keys_are_never_taken()
    {
        WpfTestHost.Run(() =>
        {
            var remote = new FakeRemote();
            using var rig = new Rig(remote);
            rig.Window.InputHasFocus = () => true;

            Assert.False(rig.Press(Key.Up).Handled);
            Assert.False(rig.Press(Key.Down).Handled);
            Assert.False(rig.Press(Key.Home).Handled);
            Assert.False(rig.Press(Key.A).Handled);
            Assert.Empty(remote.Posted);
        });
    }

    [Fact]
    public void The_view_model_takes_the_arrow_only_when_active_and_never_sends_a_repeat()
    {
        var remote = new FakeRemote();
        var clock = new FakeTimeProvider();
        using var vm = new QuickPanelViewModel(new FakeGateway(clock), new StatusHub(), clock: clock, holyrics: remote);

        Assert.True(vm.TryForwardArrow(next: true, isRepeat: false));
        Assert.True(vm.TryForwardArrow(next: false, isRepeat: true));
        Assert.Single(remote.Posted);

        remote.ArrowsEnabled = false;
        Assert.False(vm.TryForwardArrow(next: true, isRepeat: false));
        Assert.Single(remote.Posted);
    }

    [Fact]
    public void The_window_of_the_inicio_tab_has_no_holyrics_so_its_arrows_stay_normal()
    {
        var clock = new FakeTimeProvider();
        using var vm = new QuickPanelViewModel(new FakeGateway(clock), new StatusHub(), clock: clock);

        Assert.False(vm.TryForwardArrow(next: true, isRepeat: false));
    }
}
