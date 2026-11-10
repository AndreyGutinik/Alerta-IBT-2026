using AlertaIBT.Core.Input;
using AlertaIBT.Core.Ndi;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Ndi;

// Batch 42: a shortcut tapped again (the owner's log: F1 seven times in two seconds) is a cheap no-op: no reconnection, no trace.
public sealed class NdiPreviewNoOpTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new();
    private readonly List<FakeCameraReceiver> _receivers = [];
    private readonly NdiSettings _settings = NdiTestSettings.With();
    private readonly NdiSwitcher _switcher;

    public NdiPreviewNoOpTests()
    {
        _switcher = new NdiSwitcher(
            _ =>
            {
                var receiver = new FakeCameraReceiver(_clock);
                _receivers.Add(receiver);
                return receiver;
            },
            () => _settings,
            _clock);
    }

    public void Dispose() => _switcher.Dispose();

    [Fact]
    public void Nothing_selected_yet_means_the_first_selection_is_not_a_no_op()
    {
        Assert.False(_switcher.IsPreviewNoOp(0));
        Assert.False(_switcher.IsPreviewNoOp(1));
    }

    [Fact]
    public void The_camera_already_in_preview_is_a_no_op_and_another_camera_is_not()
    {
        _switcher.SelectPreview(1);

        Assert.True(_switcher.IsPreviewNoOp(1));
        Assert.False(_switcher.IsPreviewNoOp(2));
    }

    [Fact]
    public void The_camera_on_air_is_a_no_op_for_a_preview_selection()
    {
        _switcher.SelectPreview(0);
        _switcher.Take();
        _receivers[1].Push(); // the first frame arrives: the cut happens and camera 1 is on air

        Assert.True(_switcher.IsPreviewNoOp(0));
    }

    [Fact]
    public void A_slot_outside_the_range_is_not_a_no_op_so_the_real_error_still_comes()
    {
        Assert.False(_switcher.IsPreviewNoOp(-1));
        Assert.False(_switcher.IsPreviewNoOp(NdiSettings.SlotCount));
    }

    [Fact]
    public void Tapping_the_same_preview_key_many_times_connects_the_receiver_once()
    {
        for (var i = 0; i < 30; i++)
        {
            if (!_switcher.IsPreviewNoOp(1))
            {
                _switcher.SelectPreview(1);
            }
        }

        Assert.Single(_receivers.Single(r => r.Connects.Count > 0).Connects);
    }

    [Fact]
    public void The_hotkey_controller_asks_the_control_each_time_and_the_control_decides_it_is_a_no_op()
    {
        var control = new CountingControl();
        var registry = new FakeRegistry();
        using var hotkeys = new NdiHotkeyController(registry, control);
        hotkeys.Apply(true);

        for (var i = 0; i < 7; i++)
        {
            hotkeys.OnHotkey(NdiHotkeyController.FirstSelectId); // F1 seven times
        }

        Assert.Equal(7, control.Selected.Count); // every press reaches the control: the no-op decision lives there
        Assert.All(control.Selected, slot => Assert.Equal(0, slot));
    }

    private sealed class FakeRegistry : INdiHotkeyRegistry
    {
        public bool Register(int id, HotkeyModifiers modifiers, int virtualKey) => true;

        public void Unregister(int id)
        {
        }
    }

    private sealed class CountingControl : INdiControl
    {
        public List<int> Selected { get; } = [];

        public NdiResult SelectPreview(int slot)
        {
            Selected.Add(slot);
            return NdiResult.Ok;
        }

        public NdiResult Take() => NdiResult.Ok;

        public NdiResult SetLayer(bool on) => NdiResult.Ok;

        public NdiStatus GetStatus() => throw new NotSupportedException();
    }
}
