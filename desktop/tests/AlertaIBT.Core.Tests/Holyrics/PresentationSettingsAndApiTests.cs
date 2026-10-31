using AlertaIBT.Core.Control;
using AlertaIBT.Core.Holyrics;
using AlertaIBT.Core.Http;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Tests.Control;
using AlertaIBT.Core.Tests.Settings;

namespace AlertaIBT.Core.Tests.Holyrics;

// The keys of the presentation mode in settings.json (migration safe), the /holyrics/presentation routes and the optional MA2 action.
public sealed class PresentationSettingsTests
{
    [Fact]
    public void A_file_from_before_has_the_default_keys()
    {
        var (settings, corrupt) = SettingsJson.Load("{\"holyrics\":{\"host\":\"127.0.0.1\",\"port\":8091}}");

        Assert.False(corrupt);
        Assert.Equal([0x22, 0x27], settings.Holyrics.PresentationNext);
        Assert.Equal([0x21, 0x25], settings.Holyrics.PresentationPrevious);
    }

    [Fact]
    public void Chosen_keys_survive_a_save_and_a_load()
    {
        var settings = new AppSettings { Holyrics = new HolyricsSettings { PresentationNext = [0x42, 0x22], PresentationPrevious = [0x43] } };

        var (loaded, _) = SettingsJson.Load(SettingsJson.Save(settings));

        Assert.Equal([0x42, 0x22], loaded.Holyrics.PresentationNext);
        Assert.Equal([0x43], loaded.Holyrics.PresentationPrevious);
        Assert.Equal(settings.Holyrics, loaded.Holyrics);
    }

    [Fact]
    public void An_emptied_set_stays_empty_and_a_missing_one_gets_the_default()
    {
        var (loaded, _) = SettingsJson.Load("{\"holyrics\":{\"presentationNext\":[]}}");

        Assert.Empty(loaded.Holyrics.PresentationNext);
        Assert.Equal([0x21, 0x25], loaded.Holyrics.PresentationPrevious);
    }

    [Fact]
    public void Unusable_keys_and_duplicates_between_the_sets_are_cleaned()
    {
        var (loaded, _) = SettingsJson.Load("{\"holyrics\":{\"presentationNext\":[34,17,34,999,\"x\",39],\"presentationPrevious\":[39,33]}}");

        Assert.Equal([34, 39], loaded.Holyrics.PresentationNext);
        Assert.Equal([33], loaded.Holyrics.PresentationPrevious); // 39 is "next": it wins
    }

    [Fact]
    public void The_mode_itself_is_never_written_to_the_file()
    {
        var json = SettingsJson.Save(new AppSettings());

        Assert.DoesNotContain("presentationOn", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("presentationEnabled", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("presentationNext", json);
    }
}

internal sealed class FakePresentation : IPresentationControl
{
    public bool IsOn { get; private set; }

    public string? Refuse { get; set; }

    public List<bool> Asked { get; } = [];

    public PresentationResult SetOn(bool on)
    {
        Asked.Add(on);
        if (on && Refuse is not null)
        {
            return PresentationResult.Fail(Refuse);
        }

        IsOn = on;
        return PresentationResult.Ok;
    }
}

public sealed class PresentationApiTests
{
    [Theory]
    [InlineData("GET", "/holyrics/presentation/on", PresentationIntentKind.On)]
    [InlineData("POST", "/holyrics/presentation/off", PresentationIntentKind.Off)]
    [InlineData("get", "/HOLYRICS/Presentation/Status/", PresentationIntentKind.Status)]
    [InlineData("GET", "/holyrics/presentation/on?x=1", PresentationIntentKind.On)]
    [InlineData("GET", "/holyrics/presentation", PresentationIntentKind.NotFound)]
    [InlineData("GET", "/holyrics/other", PresentationIntentKind.NotFound)]
    [InlineData("DELETE", "/holyrics/presentation/on", PresentationIntentKind.NotFound)]
    public void The_routes_are_exact(string method, string url, PresentationIntentKind expected)
    {
        Assert.True(PresentationIntent.IsHolyricsPath(url));
        Assert.Equal(expected, PresentationIntent.Parse(method, url));
    }

    [Fact]
    public void Other_paths_are_not_holyrics_paths()
    {
        Assert.False(PresentationIntent.IsHolyricsPath("/ndi/on"));
        Assert.False(PresentationIntent.IsHolyricsPath("/holyricsx"));
    }

    [Fact]
    public void On_and_off_drive_the_mode_and_report_it()
    {
        var control = new FakePresentation();

        var on = PresentationIntent.Execute(PresentationIntentKind.On, control);
        Assert.Equal(true, on["success"]);
        Assert.Equal(true, on["presentationOn"]);

        var off = PresentationIntent.Execute(PresentationIntentKind.Off, control);
        Assert.Equal(false, off["presentationOn"]);

        PresentationIntent.Execute(PresentationIntentKind.Status, control);
        Assert.Equal([true, false], control.Asked); // status asks nothing
    }

    [Fact]
    public void A_refusal_is_success_false_with_the_reason()
    {
        var control = new FakePresentation { Refuse = "Holyrics sem token" };

        var body = PresentationIntent.Execute(PresentationIntentKind.On, control);

        Assert.Equal(false, body["success"]);
        Assert.Equal("Holyrics sem token", body["error"]);
        Assert.Equal(false, body["presentationOn"]);
    }
}

public sealed class PresentationControlActionTests
{
    private static ControlTargets Targets(FakePresentation? presentation) =>
        new(new FakeControlNdi(), new FakePlayerControl(), new FakeControlVolumes(), null, presentation);

    private static ControlCommand Command(ControlCommandKind kind, bool on = false) => new(1, ControlAction.PresentationMode, kind, 0, on, 0);

    [Fact]
    public void The_action_is_in_the_catalog_but_not_in_the_default_map()
    {
        Assert.Contains(ControlCatalog.All, item => item.Action == ControlAction.PresentationMode && item.Available);
        Assert.DoesNotContain(ControlSettings.DefaultRows(), row => row.Action == ControlAction.PresentationMode);
        Assert.Equal(ControlAction.PresentationMode, ControlCatalog.ParseAction("presentationMode"));
    }

    [Fact]
    public void A_button_flips_the_mode()
    {
        var presentation = new FakePresentation();
        var targets = Targets(presentation);

        Assert.True(targets.Run(Command(ControlCommandKind.Trigger)).Success);
        Assert.True(presentation.IsOn);
        Assert.True(targets.Run(Command(ControlCommandKind.Trigger)).Success);
        Assert.False(presentation.IsOn);
    }

    [Fact]
    public void A_state_row_follows_the_channel()
    {
        var presentation = new FakePresentation();
        var targets = Targets(presentation);

        targets.Run(Command(ControlCommandKind.SetState, on: true));
        targets.Run(Command(ControlCommandKind.SetState, on: true));
        Assert.True(presentation.IsOn);
        targets.Run(Command(ControlCommandKind.SetState, on: false));
        Assert.False(presentation.IsOn);
    }

    [Fact]
    public void A_refusal_and_a_missing_mode_fail_with_a_reason()
    {
        var refused = Targets(new FakePresentation { Refuse = "Holyrics sem token (aba Controle)" }).Run(Command(ControlCommandKind.Trigger));
        var missing = Targets(null).Run(Command(ControlCommandKind.Trigger));

        Assert.False(refused.Success);
        Assert.Contains("token", refused.Message);
        Assert.False(missing.Success);
    }
}
