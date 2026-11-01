using AlertaIBT.Core.Holyrics;
using AlertaIBT.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AlertaIBT.App.ViewModels;

/// <summary>
/// The "Parar a m\usica quando o cron\ometro chegar a N s" block of the Holyrics card: the switch, the seconds (1 to 10) and a live chip with the countdown Holyrics shows
/// (only while one is shown, so the owner sees the watch works). Both settings are saved at once as the last position; the watching itself is <see cref="CountdownWatcher"/>.
/// </summary>
public sealed partial class ControlViewModel
{
    private CountdownWatcher? _countdown;

    [ObservableProperty]
    private string _countdownLiveText = string.Empty;

    [ObservableProperty]
    private string _countdownProblem = string.Empty;

    /// <summary>The seconds the owner can choose (1 to 10).</summary>
    public IReadOnlyList<int> CountdownChoices { get; } = Enumerable.Range(HolyricsSettings.MinCountdownStopAt, HolyricsSettings.MaxCountdownStopAt).ToArray();

    /// <summary>"Parar a m\usica quando o cron\ometro chegar a N s" (on by default).</summary>
    public bool CountdownStopEnabled
    {
        get => _settings.Current.Holyrics.CountdownStopEnabled;
        set
        {
            if (!_syncing && value != CountdownStopEnabled)
            {
                UpdateHolyrics(h => h with { CountdownStopEnabled = value });
            }
        }
    }

    /// <summary>The seconds left on the Holyrics countdown at which the music stops.</summary>
    public int CountdownStopAt
    {
        get => _settings.Current.Holyrics.CountdownStopAt;
        set
        {
            if (!_syncing && value != CountdownStopAt && value is >= HolyricsSettings.MinCountdownStopAt and <= HolyricsSettings.MaxCountdownStopAt)
            {
                UpdateHolyrics(h => h with { CountdownStopAt = value });
            }
        }
    }

    /// <summary>Whether the live chip shows (a Holyrics countdown is on display and the watch is on).</summary>
    public bool HasCountdownLive => CountdownLiveText.Length > 0;

    /// <summary>Whether the token problem line shows.</summary>
    public bool HasCountdownProblem => CountdownProblem.Length > 0;

    /// <summary>The tooltip of the block and of its controls.</summary>
    public string CountdownTip =>
        "Parar a m\U000000FAsica quando o cron\U000000F4metro do Holyrics chegar a " + CountdownStopAt + " s: o app olha o cron\U000000F4metro (o do painel de comunica\U000000E7\U000000E3o e o da apresenta\U000000E7\U000000E3o de contagem regressiva) "
        + "e, quando qualquer um chega a esse tempo, a m\U000000FAsica do player abaixa (com o seu fade-out) e fica pausada. Uma vez por cron\U000000F4metro; se o player n\U000000E3o est\U000000E1 tocando, nada acontece. "
        + "O cron\U000000F4metro progressivo (que sobe) \U000000E9 ignorado. Precisa do token com permiss\U000000E3o para GetCPInfo e GetCurrentPresentation.";

    private void AttachCountdown(CountdownWatcher? watcher)
    {
        _countdown = watcher;
        if (watcher is not null)
        {
            watcher.Changed += OnCountdownChanged;
        }
    }

    private void DetachCountdown()
    {
        if (_countdown is not null)
        {
            _countdown.Changed -= OnCountdownChanged;
        }
    }

    // The watcher saw something new (from its background task): the chip follows on the UI thread.
    private void OnCountdownChanged() => _post(() =>
    {
        if (_disposed || _countdown is null)
        {
            return;
        }

        var now = _countdown.Current;
        CountdownLiveText = now.Seconds is { } seconds && CountdownStopEnabled ? CountdownText.Format(seconds) : string.Empty;
        CountdownProblem = now.Problem ?? string.Empty;
    });

    partial void OnCountdownLiveTextChanged(string value) => OnPropertyChanged(nameof(HasCountdownLive));

    partial void OnCountdownProblemChanged(string value) => OnPropertyChanged(nameof(HasCountdownProblem));

    private void LoadCountdown()
    {
        OnPropertyChanged(nameof(CountdownStopEnabled));
        OnPropertyChanged(nameof(CountdownStopAt));
        OnPropertyChanged(nameof(CountdownTip));
        if (!CountdownStopEnabled)
        {
            CountdownLiveText = string.Empty;
            CountdownProblem = string.Empty;
        }
    }
}
