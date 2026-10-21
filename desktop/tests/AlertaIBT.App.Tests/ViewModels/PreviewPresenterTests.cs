using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Correction;
using AlertaIBT.Core.Services;

namespace AlertaIBT.App.Tests.ViewModels;

public sealed class PreviewPresenterTests
{
    private static PreviewResult Result(
        RuleKind kind = RuleKind.Vehicle,
        CorrectorKind by = CorrectorKind.Gemini,
        bool duplicate = false,
        int? minutes = null,
        string text = "GOL BRANCO | PLACA: ABC-1234",
        IReadOnlyList<string>? missing = null,
        bool refused = false,
        bool tooLong = false) =>
        new(text, kind, missing ?? [], duplicate, by, minutes, false, refused, tooLong);

    [Fact]
    public void A_vehicle_alert_corrected_online_is_green_with_the_plain_heading()
    {
        var view = PreviewPresenter.Present(Result());

        Assert.Equal(PreviewTone.Ok, view.Tone);
        Assert.Equal("Vai aparecer assim:", view.Label);
        Assert.Equal("GOL BRANCO | PLACA: ABC-1234", view.Text);
        Assert.Equal("LucideWandSparkles", view.IconKey);
        Assert.Equal(string.Empty, view.Note);
    }

    [Fact]
    public void An_offline_correction_is_blue_with_the_wifi_off_icon()
    {
        var view = PreviewPresenter.Present(Result(by: CorrectorKind.Offline));

        Assert.Equal(PreviewTone.Info, view.Tone);
        Assert.Equal("Vai aparecer assim (offline):", view.Label);
        Assert.Equal("LucideWifiOff", view.IconKey);
    }

    [Fact]
    public void An_announcement_without_plate_is_blue_and_says_aviso_livre()
    {
        var view = PreviewPresenter.Present(Result(kind: RuleKind.Free, text: "PAIS DO PEDRO NA RECEPCAO"));

        Assert.Equal(PreviewTone.Info, view.Tone);
        Assert.Equal("Vai aparecer assim (aviso livre):", view.Label);
        Assert.Equal("LucideWandSparkles", view.IconKey);
    }

    [Fact]
    public void A_free_announcement_corrected_offline_says_both()
    {
        var view = PreviewPresenter.Present(Result(kind: RuleKind.Free, by: CorrectorKind.Offline));

        Assert.Equal("Vai aparecer assim (aviso livre, offline):", view.Label);
        Assert.Equal(PreviewTone.Info, view.Tone);
    }

    [Fact]
    public void A_plate_shown_minutes_ago_warns_but_keeps_the_preview()
    {
        var view = PreviewPresenter.Present(Result(duplicate: true, minutes: 4));

        Assert.Equal(PreviewTone.Ok, view.Tone);
        Assert.Equal("Esse carro j\U000000E1 passou h\U000000E1 4 min. Pode enviar mesmo assim.", view.Note);
    }

    [Fact]
    public void A_plate_shown_less_than_a_minute_ago_says_one_minute()
    {
        var view = PreviewPresenter.Present(Result(duplicate: true, minutes: 0));

        Assert.Contains("h\U000000E1 1 min", view.Note);
    }

    [Fact]
    public void A_plate_already_in_the_queue_warns()
    {
        var view = PreviewPresenter.Present(Result(duplicate: true));

        Assert.Equal("Esse carro j\U000000E1 est\U000000E1 na fila. Pode enviar mesmo assim.", view.Note);
    }

    [Fact]
    public void A_refused_text_is_red_and_says_improper()
    {
        var view = PreviewPresenter.Present(Result(kind: RuleKind.Reject, refused: true, text: string.Empty));

        Assert.Equal(PreviewTone.Error, view.Tone);
        Assert.Equal("N\U000000E3o vai para o tel\U000000E3o:", view.Label);
        Assert.Equal("conte\U000000FAdo impr\U000000F3prio", view.Text);
        Assert.Equal("LucideCircleAlert", view.IconKey);
    }

    [Fact]
    public void Missing_fields_are_listed_in_red()
    {
        var view = PreviewPresenter.Present(Result(kind: RuleKind.Reject, missing: ["modelo", "placa"]));

        Assert.Equal(PreviewTone.Error, view.Tone);
        Assert.Equal("falta modelo, placa", view.Text);
    }

    [Fact]
    public void Nothing_to_show_is_red()
    {
        var view = PreviewPresenter.Present(Result(kind: RuleKind.Reject, text: string.Empty));

        Assert.Equal(PreviewTone.Error, view.Tone);
        Assert.Equal("nada para mostrar", view.Text);
    }

    [Fact]
    public void A_text_too_long_for_a_member_is_red_and_names_the_limit()
    {
        var view = PreviewPresenter.Present(Result(kind: RuleKind.Reject, tooLong: true));

        Assert.Equal(PreviewTone.Error, view.Tone);
        Assert.Equal("texto grande demais (m\U000000E1ximo 200 letras)", view.Text);
    }

    [Fact]
    public void The_empty_preview_has_no_tone()
    {
        Assert.True(PreviewView.Empty.IsEmpty);
        Assert.False(PreviewPresenter.Present(Result()).IsEmpty);
    }
}
