using System.Collections.ObjectModel;
using AlertaIBT.App.Platform;
using AlertaIBT.Core.Holyrics;
using AlertaIBT.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AlertaIBT.App.ViewModels;

/// <summary>One key of a set of the presentation mode, as a chip: a short label, a tooltip with the full name, and the way to take it out.</summary>
public sealed class PresentationKeyChip
{
    /// <param name="virtualKey">The virtual-key code.</param>
    /// <param name="remove">Takes the key out of its set.</param>
    public PresentationKeyChip(int virtualKey, Action<int> remove)
    {
        VirtualKey = virtualKey;
        Label = PresentationKeys.Label(virtualKey);
        Tip = PresentationKeys.Name(virtualKey) + ": clique para tirar";
        RemoveCommand = new RelayCommand(() => remove(virtualKey));
    }

    /// <summary>The virtual-key code.</summary>
    public int VirtualKey { get; }

    /// <summary>The short text on the chip.</summary>
    public string Label { get; }

    /// <summary>The tooltip.</summary>
    public string Tip { get; }

    /// <summary>Takes the key out.</summary>
    public IRelayCommand RemoveCommand { get; }
}

/// <summary>
/// The "Modo apresenta\U000000E7\U000000E3o" part of the Holyrics card: the switch (saved: it comes back as it was), the Ctrl+Alt+P shortcut, and the two sets of keys
/// (next, previous) that the pastor's remote sends, as removable chips with "adicionar tecla" (press-to-record) and "restaurar padr\U000000E3o". The keys are saved in
/// <see cref="HolyricsSettings"/>; the mode itself lives in <see cref="PresentationModeController"/>.
/// </summary>
public sealed partial class ControlViewModel
{
    private PresentationModeController? _presentation;

    [ObservableProperty]
    private string _presentationKeyHint = string.Empty;

    /// <summary>Whether the presentation mode exists here (false only in tests of the rest of the tab).</summary>
    public bool HasPresentation => _presentation is not null;

    /// <summary>The keys of "Pr\U000000F3ximo".</summary>
    public ObservableCollection<PresentationKeyChip> PresentationNextKeys { get; } = [];

    /// <summary>The keys of "Anterior".</summary>
    public ObservableCollection<PresentationKeyChip> PresentationPreviousKeys { get; } = [];

    /// <summary>"Adicionar tecla" in "Pr\U000000F3ximo".</summary>
    public IRelayCommand AddNextKeyCommand { get; private set; } = null!;

    /// <summary>"Adicionar tecla" in "Anterior".</summary>
    public IRelayCommand AddPreviousKeyCommand { get; private set; } = null!;

    /// <summary>"Restaurar padr\U000000E3o": the keys go back to Page Down and the right arrow (next), Page Up and the left arrow (previous).</summary>
    public IRelayCommand RestorePresentationKeysCommand { get; private set; } = null!;

    /// <summary>
    /// Asks the owner to press a key: given the name of the set ("Pr\U000000F3ximo", "Anterior"), returns the virtual-key code pressed, or <c>null</c> when cancelled. The page sets it
    /// (it opens the small dialog); without one "adicionar tecla" does nothing.
    /// </summary>
    public Func<string, int?>? KeyRecorder { get; set; }

    /// <summary>The switch "Modo apresenta\U000000E7\U000000E3o". Turning it on can be refused (no token, hook refused): the switch then goes back off and the reason shows.</summary>
    public bool PresentationOn
    {
        get => _presentation?.IsOn ?? false;
        set
        {
            if (_syncing || _presentation is null || value == _presentation.IsOn)
            {
                return;
            }

            _presentation.SetOn(value);

            // Whatever happened (refused, or done), the switch shows the truth: handed over to the dispatcher so it also works while the binding is still setting the value.
            _post(NotifyPresentation);
        }
    }

    /// <summary>The switch can be used: Holyrics has a token (or the mode is on and must be able to turn off).</summary>
    public bool PresentationSwitchEnabled => _presentation is { } p && (p.CanTurnOn || p.IsOn);

    /// <summary>The tooltip of the switch.</summary>
    public string PresentationTip => _presentation switch
    {
        null => string.Empty,
        { IsOn: true } => "Modo apresenta\U000000E7\U000000E3o ligado: as teclas do controle do pastor passam o slide no Holyrics de qualquer janela. Atalho: Ctrl+Alt+P.",
        { CanTurnOn: false } => "Para ligar, cole antes o token do Holyrics (acima).",
        _ => "Ligar o modo apresenta\U000000E7\U000000E3o: as teclas abaixo (o controle do pastor) passam o slide no Holyrics de qualquer janela e n\U000000E3o chegam ao programa da frente. Atalho: Ctrl+Alt+P. Ao abrir o app, volta como estava.",
    };

    /// <summary>Why the mode is off or does not work (a refusal, a hook that was lost, the shortcut in use by another program); empty when there is nothing to say.</summary>
    public string PresentationReasonText
    {
        get
        {
            if (_presentation is null)
            {
                return string.Empty;
            }

            if (_presentation.Reason is { Length: > 0 } reason)
            {
                return "Modo apresenta\U000000E7\U000000E3o desligado: " + reason + ".";
            }

            return _presentation.HotkeyStatus is { Registered: false, Message: { Length: > 0 } message } ? message : string.Empty;
        }
    }

    /// <summary>Whether a line about the mode shows under the card.</summary>
    public bool HasPresentationReason => PresentationReasonText.Length > 0;

    /// <summary>Whether a note about the last key (refused, repeated) shows.</summary>
    public bool HasPresentationKeyHint => PresentationKeyHint.Length > 0;

    /// <summary>Adds <paramref name="virtualKey"/> to "Pr\U000000F3ximo" or "Anterior". False, with a note, when it cannot be used.</summary>
    /// <param name="next">True for "Pr\U000000F3ximo", false for "Anterior".</param>
    /// <param name="virtualKey">The key pressed.</param>
    public bool AddPresentationKey(bool next, int virtualKey)
    {
        var holyrics = _settings.Current.Holyrics;
        var mine = (next ? holyrics.PresentationNext : holyrics.PresentationPrevious).ToList();
        var other = next ? holyrics.PresentationPrevious : holyrics.PresentationNext;
        var name = PresentationKeys.Name(virtualKey);
        string? refusal = null;
        if (!PresentationKeys.IsAllowed(virtualKey))
        {
            refusal = $"{name} n\U000000E3o pode ser usada (Ctrl, Alt, Shift, Windows, Esc e as teclas de m\U000000FAsica ficam de fora)";
        }
        else if (mine.Contains(virtualKey))
        {
            refusal = $"{name} j\U000000E1 est\U000000E1 em {(next ? "Pr\U000000F3ximo" : "Anterior")}";
        }
        else if (other.Contains(virtualKey))
        {
            refusal = $"{name} j\U000000E1 \U000000E9 usada em {(next ? "Anterior" : "Pr\U000000F3ximo")}: tire-a de l\U000000E1 primeiro";
        }
        else if (mine.Count >= PresentationKeys.MaxPerSet)
        {
            refusal = $"no m\U000000E1ximo {PresentationKeys.MaxPerSet} teclas";
        }

        if (refusal is not null)
        {
            PresentationKeyHint = refusal;
            return false;
        }

        mine.Add(virtualKey);
        PresentationKeyHint = string.Empty;
        UpdateHolyrics(h => next ? h with { PresentationNext = mine } : h with { PresentationPrevious = mine });
        return true;
    }

    /// <summary>Takes a key out of both sets.</summary>
    /// <param name="virtualKey">The key.</param>
    public void RemovePresentationKey(int virtualKey)
    {
        PresentationKeyHint = string.Empty;
        UpdateHolyrics(h => h with
        {
            PresentationNext = h.PresentationNext.Where(k => k != virtualKey).ToList(),
            PresentationPrevious = h.PresentationPrevious.Where(k => k != virtualKey).ToList(),
        });
    }

    private void CreatePresentation(PresentationModeController? presentation)
    {
        _presentation = presentation;
        AddNextKeyCommand = new RelayCommand(() => AddRecordedKey(next: true));
        AddPreviousKeyCommand = new RelayCommand(() => AddRecordedKey(next: false));
        RestorePresentationKeysCommand = new RelayCommand(() =>
        {
            PresentationKeyHint = string.Empty;
            UpdateHolyrics(h => h with { PresentationNext = PresentationKeys.DefaultNext, PresentationPrevious = PresentationKeys.DefaultPrevious });
        });
        if (presentation is not null)
        {
            presentation.StateChanged += OnPresentationChanged;
        }
    }

    private void AddRecordedKey(bool next)
    {
        if (KeyRecorder?.Invoke(next ? "Pr\U000000F3ximo" : "Anterior") is { } key)
        {
            AddPresentationKey(next, key);
        }
    }

    private void OnPresentationChanged() => _post(() =>
    {
        if (!_disposed)
        {
            NotifyPresentation();
        }
    });

    partial void OnPresentationKeyHintChanged(string value) => OnPropertyChanged(nameof(HasPresentationKeyHint));

    private void NotifyPresentation()
    {
        OnPropertyChanged(nameof(PresentationOn));
        OnPropertyChanged(nameof(PresentationSwitchEnabled));
        OnPropertyChanged(nameof(PresentationTip));
        OnPropertyChanged(nameof(PresentationReasonText));
        OnPropertyChanged(nameof(HasPresentationReason));
    }

    // Reads the saved keys into the chips (called by LoadHolyrics).
    private void LoadPresentation()
    {
        var holyrics = _settings.Current.Holyrics;
        Rebuild(PresentationNextKeys, holyrics.PresentationNext);
        Rebuild(PresentationPreviousKeys, holyrics.PresentationPrevious);
        OnPropertyChanged(nameof(HasPresentation));
        NotifyPresentation();

        void Rebuild(ObservableCollection<PresentationKeyChip> chips, IReadOnlyList<int> keys)
        {
            if (chips.Select(c => c.VirtualKey).SequenceEqual(keys))
            {
                return;
            }

            chips.Clear();
            foreach (var key in keys)
            {
                chips.Add(new PresentationKeyChip(key, RemovePresentationKey));
            }
        }
    }
}
