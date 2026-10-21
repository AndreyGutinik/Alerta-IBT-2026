using AlertaIBT.App.Tray;
using AlertaIBT.App.Tests.ViewModels;
using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.App.Tests.Tray;

public sealed class QuickPanelControllerTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new();
    private readonly QuickPanelViewModel _vm;
    private readonly FakePanelWindow _window = new();
    private readonly QuickPanelController _controller;
    private int _created;

    public QuickPanelControllerTests()
    {
        _vm = new QuickPanelViewModel(new FakeGateway(_clock), new StatusHub(), clock: _clock);
        _controller = new QuickPanelController(
            _vm,
            () =>
            {
                _created++;
                return _window;
            },
            NullLogger<QuickPanelController>.Instance,
            _clock);
    }

    public void Dispose() => _vm.Dispose();

    private sealed class FakePanelWindow : IQuickPanelWindow
    {
        public bool IsVisible { get; private set; }

        public int Shown { get; private set; }

        public bool ThrowOnShow { get; set; }

        public event Action? Hidden;

        public void ShowPanel()
        {
            if (ThrowOnShow)
            {
                throw new InvalidOperationException("boom");
            }

            IsVisible = true;
            Shown++;
        }

        public void HidePanel() => Lose();

        // What Deactivated does when the user clicks elsewhere.
        public void Lose()
        {
            if (!IsVisible)
            {
                return;
            }

            IsVisible = false;
            Hidden?.Invoke();
        }
    }

    [Fact]
    public void The_window_is_made_once_on_first_use_and_shown_with_the_focus_request()
    {
        var focus = 0;
        _vm.FocusInputRequested += () => focus++;
        Assert.Equal(0, _created);

        _controller.Toggle();

        Assert.Equal(1, _created);
        Assert.True(_controller.IsOpen);
        Assert.Equal(1, focus);

        _window.Lose();
        _clock.Advance(QuickPanelController.ReopenGuard);
        _controller.Toggle();
        Assert.Equal(1, _created);
        Assert.Equal(2, _window.Shown);
    }

    [Fact]
    public void A_second_request_while_open_closes_the_panel()
    {
        _controller.Toggle();

        _controller.Toggle();

        Assert.False(_controller.IsOpen);
    }

    [Fact]
    public void The_tray_click_that_closed_the_panel_by_taking_its_focus_does_not_open_it_again()
    {
        _controller.Toggle();
        _window.Lose(); // the click on the tray icon deactivates the panel first...

        _clock.Advance(TimeSpan.FromMilliseconds(50));
        _controller.Toggle(); // ...and then arrives as a click

        Assert.False(_controller.IsOpen);
    }

    [Fact]
    public void A_later_request_opens_it_again()
    {
        _controller.Toggle();
        _window.Lose();

        _clock.Advance(QuickPanelController.ReopenGuard + TimeSpan.FromMilliseconds(1));
        _controller.Toggle();

        Assert.True(_controller.IsOpen);
    }

    [Fact]
    public void Open_when_already_open_does_not_show_twice()
    {
        _controller.Open();
        _controller.Open();

        Assert.Equal(1, _window.Shown);
    }

    [Fact]
    public void Esc_on_an_empty_field_closes_the_panel_through_the_view_model()
    {
        var other = new QuickPanelController(_vm, () => _window, NullLogger<QuickPanelController>.Instance, _clock);
        _vm.CloseRequested += other.Close;
        other.Open();

        _vm.Escape();

        Assert.False(other.IsOpen);
    }

    [Fact]
    public void A_window_that_fails_to_show_is_logged_and_never_thrown()
    {
        _window.ThrowOnShow = true;

        var exception = Record.Exception(_controller.Toggle);

        Assert.Null(exception);
        Assert.False(_controller.IsOpen);
    }

    [Fact]
    public void Closing_before_it_was_ever_opened_is_harmless()
    {
        _controller.Close();

        Assert.Equal(0, _created);
    }
}
