using System.Text.RegularExpressions;
using AlertaIBT.Core.Correction;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AlertaIBT.App.ViewModels;

/// <summary>
/// The logic of the correction (spec 7.2; a folded section of the Início tab): the on/off switch of the automatic correction, Gemini or "Só offline", the Gemini key
/// (locked, stored with DPAPI, never shown again), the model (a card of Configurações) and the fixed rules, one per line. Every control saves
/// 600 ms after its last change through the <see cref="Autosaver"/>; nothing is explained in text blocks (the hints are tooltips).
/// </summary>
/// <remarks>
/// The fixed rules are looked at once per saved change, not per keystroke: the note under the box ("2 regras · 1 linha ignorada")
/// is worked out right after the rules went into the settings. A repeated rule counts once.
/// </remarks>
public sealed partial class CorrectionViewModel : ObservableObject, IDisposable
{
    private static readonly Regex ModelId = new("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private readonly SettingsService _settings;
    private readonly ISecretProtector _protector;
    private readonly StatusHub _hub;
    private readonly Autosaver _saver;
    private readonly Action<Action> _post;
    private readonly Func<string, FixedRulesReport> _inspect;
    private bool _quiet;
    private bool _disposed;

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private bool _useGemini;

    [ObservableProperty]
    private string _model = string.Empty;

    [ObservableProperty]
    private string? _modelHint;

    [ObservableProperty]
    private string _rulesText = string.Empty;

    [ObservableProperty]
    private string _rulesNote = string.Empty;

    [ObservableProperty]
    private bool _rulesNoteIsWarning;

    [ObservableProperty]
    private StatusLight _geminiLight = StatusLight.Gray;

    [ObservableProperty]
    private string _geminiText = string.Empty;

    [ObservableProperty]
    private string _geminiTip = string.Empty;

    /// <param name="settings">The settings.</param>
    /// <param name="protector">Protects the Gemini key (DPAPI in the app).</param>
    /// <param name="hub">Where the Gemini dot comes from.</param>
    /// <param name="saver">The autosave shared with the Configurações tab.</param>
    /// <param name="post">Runs an action on the UI thread; runs it at once when null (tests).</param>
    /// <param name="inspect">Reads the rules text; <see cref="FixedRules.Inspect"/> in the app, a counting fake in tests.</param>
    public CorrectionViewModel(
        SettingsService settings,
        ISecretProtector protector,
        StatusHub hub,
        Autosaver saver,
        Action<Action>? post = null,
        Func<string, FixedRulesReport>? inspect = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(saver);

        _settings = settings;
        _protector = protector;
        _hub = hub;
        _saver = saver;
        _post = post ?? (action => action());
        _inspect = inspect ?? FixedRules.Inspect;

        GeminiKey = new SecretFieldViewModel(
            () => _settings.Current.Correction.GeminiKeyProtected is not null,
            ValidateGeminiKey,
            key => _saver.Queue(
                "correction.key",
                s => s.WithGeminiKey(key, _protector),
                () => GeminiKey!.Committed()),
            () => _saver.Cancel("correction.key"));
        SelectModeCommand = new RelayCommand<string>(mode => UseGemini = !string.Equals(mode, "offline", StringComparison.Ordinal));
        ResetModelCommand = new RelayCommand(() => Model = GeminiPrompt.DefaultModel);

        var correction = _settings.Current.Correction;
        _quiet = true;
        Enabled = correction.Enabled;
        UseGemini = !string.Equals(correction.Mode, "offline", StringComparison.Ordinal);
        Model = correction.GeminiModel;
        RulesText = correction.FixedRules;
        _quiet = false;
        ShowRulesNote(correction.FixedRules);

        _hub.PropertyChanged += OnHubChanged;
        RefreshGemini();
    }

    /// <summary>The Gemini key field: locked with dots once stored, "Trocar" to type a new one.</summary>
    public SecretFieldViewModel GeminiKey { get; }

    /// <summary>The Gemini / Só offline choice; the parameter is <c>gemini</c> or <c>offline</c>.</summary>
    public IRelayCommand<string> SelectModeCommand { get; }

    /// <summary>Puts the default model back (<see cref="GeminiPrompt.DefaultModel"/>).</summary>
    public IRelayCommand ResetModelCommand { get; }

    /// <summary>The autosave stamp of this tab.</summary>
    public Autosaver Saver => _saver;

    /// <summary>The model the box shows when it is empty: the default one.</summary>
    public string DefaultModel => GeminiPrompt.DefaultModel;

    /// <summary>Whether "Só offline" is chosen (the icon toggle next to the Gemini key; turning it on turns Gemini off).</summary>
    public bool UseOffline
    {
        get => !UseGemini;
        set => UseGemini = !value;
    }

    /// <summary>Whether the note under the rules box is shown.</summary>
    public bool HasRulesNote => RulesNote.Length > 0;

    /// <summary>Whether the model hint is shown.</summary>
    public bool HasModelHint => ModelHint is not null;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hub.PropertyChanged -= OnHubChanged;
    }

    partial void OnEnabledChanged(bool value)
    {
        if (!_quiet)
        {
            _saver.Queue("correction.enabled", s => s with { Correction = s.Correction with { Enabled = value } });
        }
    }

    partial void OnUseGeminiChanged(bool value)
    {
        OnPropertyChanged(nameof(UseOffline));
        if (!_quiet)
        {
            var mode = value ? "gemini" : "offline";
            _saver.Queue("correction.mode", s => s with { Correction = s.Correction with { Mode = mode } });
        }
    }

    partial void OnModelHintChanged(string? value) => OnPropertyChanged(nameof(HasModelHint));

    partial void OnModelChanged(string value)
    {
        if (_quiet)
        {
            return;
        }

        var typed = value.Trim();
        if (typed.Length > 0 && !ModelId.IsMatch(typed))
        {
            ModelHint = "Use s\U000000F3 letras, n\U000000FAmeros, ponto, h\U000000EDfen ou sublinhado.";
            _saver.Cancel("correction.model");
            return;
        }

        ModelHint = null;

        // Blank means the default model (the settings hold it too).
        var model = typed.Length == 0 ? GeminiPrompt.DefaultModel : typed;
        _saver.Queue("correction.model", s => s with { Correction = s.Correction with { GeminiModel = model } });
    }

    partial void OnRulesTextChanged(string value)
    {
        if (_quiet)
        {
            return;
        }

        // The text goes to the settings 600 ms after the last key; the rules are read once, right after that.
        _saver.Queue(
            "correction.rules",
            s => s with { Correction = s.Correction with { FixedRules = value } },
            () => ShowRulesNote(value));
    }

    partial void OnRulesNoteChanged(string value) => OnPropertyChanged(nameof(HasRulesNote));

    private static string? ValidateGeminiKey(string key)
    {
        if (key.Any(char.IsWhiteSpace) || key.Length < 16)
        {
            return "A chave parece incompleta. Cole a chave inteira, sem espa\U000000E7os.";
        }

        return null;
    }

    private void ShowRulesNote(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            RulesNote = string.Empty;
            RulesNoteIsWarning = false;
            return;
        }

        var report = _inspect(text);
        List<string> parts = [];
        parts.Add(report.ValidCount switch
        {
            0 => "Nenhuma regra v\U000000E1lida",
            1 => "1 regra",
            _ => $"{report.ValidCount} regras",
        });
        if (report.IgnoredLines.Count > 0)
        {
            parts.Add(
                report.IgnoredLines.Count == 1
                    ? $"1 linha ignorada (linha {report.IgnoredLines[0]})"
                    : $"{report.IgnoredLines.Count} linhas ignoradas ({LineList(report.IgnoredLines)})");
        }

        if (report.RepeatedLines.Count > 0)
        {
            parts.Add(
                report.RepeatedLines.Count == 1
                    ? $"1 repetida (linha {report.RepeatedLines[0]}) conta uma vez"
                    : $"{report.RepeatedLines.Count} repetidas ({LineList(report.RepeatedLines)}) contam uma vez");
        }

        RulesNote = string.Join(" \U000000B7 ", parts);
        RulesNoteIsWarning = report.IgnoredLines.Count > 0 || report.ValidCount == 0;
    }

    private static string LineList(IReadOnlyList<int> lines)
    {
        const int Shown = 5;
        var head = string.Join(", ", lines.Take(Shown));
        return lines.Count > Shown ? $"linhas {head}..." : $"linhas {head}";
    }

    private void OnHubChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StatusHub.Gemini) or nameof(StatusHub.GeminiState))
        {
            _post(RefreshGemini);
        }
    }

    private void RefreshGemini()
    {
        if (_disposed)
        {
            return;
        }

        var gemini = _hub.Gemini;
        GeminiLight = gemini.Light;
        GeminiText = gemini.Text;
        GeminiTip = gemini.Detail.Length > 0 ? $"Gemini: {gemini.Text}\n{gemini.Detail}" : $"Gemini: {gemini.Text}";
    }
}
