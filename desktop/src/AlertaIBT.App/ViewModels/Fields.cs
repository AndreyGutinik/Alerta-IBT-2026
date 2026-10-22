using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AlertaIBT.App.ViewModels;

/// <summary>
/// A box that takes a whole number (the separate font size, the margin, the slide time). Whatever is typed, only something
/// usable is ever handed on: letters are ignored with a hint, a number out of range is held to the range (with a hint that says
/// to which value) and only that held value is saved. When the box loses the focus it shows the value that was kept.
/// </summary>
public sealed partial class IntegerField : ObservableObject
{
    private readonly int _min;
    private readonly int _max;
    private readonly bool _allowBlank;
    private readonly Action<int?> _commit;
    private readonly Action _cancel;
    private bool _quiet;
    private int? _accepted;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private string? _hint;

    /// <param name="min">The lowest value.</param>
    /// <param name="max">The highest value.</param>
    /// <param name="allowBlank">Whether an empty box means "automatic" (null) instead of a mistake.</param>
    /// <param name="commit">Receives the usable value (or null for blank) after every usable edit.</param>
    /// <param name="cancel">Called when the box holds something unusable, so a waiting save of an older value is dropped.</param>
    public IntegerField(int min, int max, bool allowBlank, Action<int?> commit, Action cancel)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(cancel);

        _min = min;
        _max = max;
        _allowBlank = allowBlank;
        _commit = commit;
        _cancel = cancel;
    }

    /// <summary>The value last kept (what is saved), or null for blank/automatic.</summary>
    public int? Accepted => _accepted;

    /// <summary>Whether the hint is visible.</summary>
    public bool HasHint => Hint is not null;

    /// <summary>Puts a value into the box without handing it on (the settings were loaded or changed elsewhere).</summary>
    public void Set(int? value)
    {
        _quiet = true;
        try
        {
            _accepted = value;
            Text = value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            Hint = null;
        }
        finally
        {
            _quiet = false;
        }
    }

    /// <summary>The box lost the focus: show the value that was kept, never what could not be used.</summary>
    public void Normalize()
    {
        if (Hint is null && string.Equals(Text.Trim(), _accepted?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, StringComparison.Ordinal))
        {
            return;
        }

        Set(_accepted);
    }

    partial void OnHintChanged(string? value) => OnPropertyChanged(nameof(HasHint));

    partial void OnTextChanged(string value)
    {
        if (_quiet)
        {
            return;
        }

        var typed = value.Trim();
        if (typed.Length == 0)
        {
            if (_allowBlank)
            {
                Hint = null;
                Keep(null);
            }
            else
            {
                Hint = "Digite um n\U000000FAmero.";
                _cancel();
            }

            return;
        }

        if (!TryRead(typed, out var number))
        {
            Hint = "Digite s\U000000F3 n\U000000FAmeros inteiros.";
            _cancel();
            return;
        }

        var held = Math.Clamp(number, _min, _max);
        Hint = held == number ? null : $"Ajustado para {held} (vale de {_min} a {_max}).";
        Keep(held);
    }

    private void Keep(int? value)
    {
        _accepted = value;
        _commit(value);
    }

    // A whole number, however large: one that does not fit in a long still counts (it is held to the range afterwards).
    private static bool TryRead(string typed, out int number)
    {
        if (long.TryParse(typed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var wide))
        {
            number = (int)Math.Clamp(wide, int.MinValue, int.MaxValue);
            return true;
        }

        if (Regex.IsMatch(typed, "^[+-]?[0-9]+$"))
        {
            number = typed.StartsWith('-') ? int.MinValue : int.MaxValue;
            return true;
        }

        number = 0;
        return false;
    }
}

/// <summary>
/// A box that takes a line of text that must pass a check (the site address, the API key, an e-mail). Whatever is typed, only a
/// usable value is ever handed on, trimmed; blank is usable and clears the value. Anything else is ignored with a hint and never
/// saved. When the box loses the focus it shows the value that was kept.
/// </summary>
public sealed partial class TextField : ObservableObject
{
    private readonly Func<string, string?> _validate;
    private readonly Action<string> _commit;
    private readonly Action _cancel;
    private bool _quiet;
    private string _accepted = string.Empty;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private string? _hint;

    /// <param name="validate">Says what is wrong with a typed value (already trimmed, not blank), or null when it can be used.</param>
    /// <param name="commit">Receives the usable value (blank for none) after every usable edit.</param>
    /// <param name="cancel">Called when the box holds something unusable, so a waiting save of an older value is dropped.</param>
    public TextField(Func<string, string?> validate, Action<string> commit, Action cancel)
    {
        ArgumentNullException.ThrowIfNull(validate);
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(cancel);

        _validate = validate;
        _commit = commit;
        _cancel = cancel;
    }

    /// <summary>The value last kept (what is saved).</summary>
    public string Accepted => _accepted;

    /// <summary>Whether the hint is visible.</summary>
    public bool HasHint => Hint is not null;

    /// <summary>Puts a value into the box without handing it on.</summary>
    public void Set(string value)
    {
        _quiet = true;
        try
        {
            _accepted = value;
            Text = value;
            Hint = null;
        }
        finally
        {
            _quiet = false;
        }
    }

    /// <summary>The box lost the focus: show the value that was kept, never what could not be used.</summary>
    public void Normalize()
    {
        if (Hint is null && string.Equals(Text.Trim(), _accepted, StringComparison.Ordinal))
        {
            return;
        }

        Set(_accepted);
    }

    partial void OnHintChanged(string? value) => OnPropertyChanged(nameof(HasHint));

    partial void OnTextChanged(string value)
    {
        if (_quiet)
        {
            return;
        }

        var typed = value.Trim();
        var problem = typed.Length == 0 ? null : _validate(typed);
        Hint = problem;
        if (problem is not null)
        {
            _cancel();
            return;
        }

        _accepted = typed;
        _commit(typed);
    }
}

/// <summary>A box that takes a color as <c>#RRGGBB</c>. Anything else is ignored with a hint and never saved.</summary>
public sealed partial class ColorField : ObservableObject
{
    private static readonly Regex Hex = new("^#?([0-9A-Fa-f]{6})$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private readonly Action<string> _commit;
    private readonly Action _cancel;
    private bool _quiet;
    private string _accepted = string.Empty;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private string? _hint;

    /// <param name="commit">Receives <c>#RRGGBB</c> (upper case) after every usable edit.</param>
    /// <param name="cancel">Called when the box holds something unusable.</param>
    public ColorField(Action<string> commit, Action cancel)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(cancel);

        _commit = commit;
        _cancel = cancel;
    }

    /// <summary>The color last kept (<c>#RRGGBB</c>).</summary>
    public string Accepted => _accepted;

    /// <summary>Whether the hint is visible.</summary>
    public bool HasHint => Hint is not null;

    /// <summary>Puts a color into the box without handing it on.</summary>
    public void Set(string color)
    {
        _quiet = true;
        try
        {
            _accepted = color;
            Text = color;
            Hint = null;
        }
        finally
        {
            _quiet = false;
        }
    }

    /// <summary>The box lost the focus: show the color that was kept.</summary>
    public void Normalize()
    {
        if (Hint is null && string.Equals(Text, _accepted, StringComparison.Ordinal))
        {
            return;
        }

        Set(_accepted);
    }

    partial void OnHintChanged(string? value) => OnPropertyChanged(nameof(HasHint));

    partial void OnTextChanged(string value)
    {
        if (_quiet)
        {
            return;
        }

        var match = Hex.Match(value.Trim());
        if (!match.Success)
        {
            Hint = "Use o formato #RRGGBB, por exemplo #FFF7E8.";
            _cancel();
            return;
        }

        Hint = null;
        _accepted = "#" + match.Groups[1].Value.ToUpperInvariant();
        _commit(_accepted);
    }
}

/// <summary>
/// A field for a secret (the Telegram token, the Gemini key). Once a secret is stored the field is locked and shows only dots, never
/// a letter of it; "Trocar" unlocks it for a new value. A new value is checked, handed on once, and then dropped from memory at
/// once: the field goes back to dots. The value is never logged or shown again.
/// </summary>
public sealed partial class SecretFieldViewModel : ObservableObject
{
    /// <summary>What the locked field shows: fixed length, so even the length of the secret stays private.</summary>
    public const string Mask = "\U00002022\U00002022\U00002022\U00002022\U00002022\U00002022\U00002022\U00002022\U00002022\U00002022\U00002022\U00002022\U00002022\U00002022\U00002022\U00002022";

    private readonly Func<bool> _hasStoredSource;
    private readonly Func<string, string?> _validate;
    private readonly Action<string> _commit;
    private readonly Action _cancel;

    [ObservableProperty]
    private string _entry = string.Empty;

    [ObservableProperty]
    private bool _isLocked;

    [ObservableProperty]
    private bool _hasStored;

    [ObservableProperty]
    private string? _hint;

    /// <param name="hasStored">Whether a secret is stored in the settings now.</param>
    /// <param name="validate">Says what is wrong with a typed secret, or null when it can be used.</param>
    /// <param name="commit">Receives a usable new secret (once).</param>
    /// <param name="cancel">Called when the entry holds something unusable or was emptied, so a waiting save is dropped.</param>
    public SecretFieldViewModel(Func<bool> hasStored, Func<string, string?> validate, Action<string> commit, Action cancel)
    {
        ArgumentNullException.ThrowIfNull(hasStored);
        ArgumentNullException.ThrowIfNull(validate);
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(cancel);

        _hasStoredSource = hasStored;
        _validate = validate;
        _commit = commit;
        _cancel = cancel;
        _hasStored = hasStored();
        _isLocked = _hasStored;
        ToggleLockCommand = new RelayCommand(ToggleLock);
    }

    /// <summary>The lock button ("Trocar" / "Bloquear").</summary>
    public IRelayCommand ToggleLockCommand { get; }

    /// <summary>Whether the hint is visible.</summary>
    public bool HasHint => Hint is not null;

    /// <summary>Whether the real entry box is shown (a new secret can be typed).</summary>
    public bool IsEditing => !IsLocked;

    /// <summary>"Trocar" (locked) or "Bloquear" (open with a secret stored); nothing to lock when none is stored.</summary>
    public string LockButtonText => IsLocked ? "Trocar" : "Bloquear";

    /// <summary>The Lucide icon of the lock button.</summary>
    public string LockIconKey => IsLocked ? "LucideLock" : "LucideLockOpen";

    /// <summary>Whether the lock button is shown (only when there is a secret to protect).</summary>
    public bool CanLock => HasStored;

    /// <summary>"Trocar": opens the field for a new value. Nothing typed yet.</summary>
    public void Unlock()
    {
        Hint = null;
        Entry = string.Empty;
        IsLocked = false;
    }

    /// <summary>"Bloquear": drops what was typed and hides the field again (only when a secret is stored).</summary>
    public void Lock()
    {
        if (!HasStored)
        {
            return;
        }

        _cancel();
        Hint = null;
        IsLocked = true;
        Entry = string.Empty;
    }

    /// <summary>The lock button.</summary>
    public void ToggleLock()
    {
        if (IsLocked)
        {
            Unlock();
        }
        else
        {
            Lock();
        }
    }

    /// <summary>The new secret went into the settings: forget it here and show the dots.</summary>
    public void Committed()
    {
        HasStored = _hasStoredSource();
        Hint = null;
        IsLocked = HasStored;
        Entry = string.Empty;
    }

    /// <summary>The settings changed elsewhere (or were reloaded): re-read whether a secret is stored.</summary>
    public void Refresh()
    {
        HasStored = _hasStoredSource();
        if (!HasStored)
        {
            IsLocked = false;
        }
    }

    partial void OnHintChanged(string? value) => OnPropertyChanged(nameof(HasHint));

    partial void OnHasStoredChanged(bool value) => OnPropertyChanged(nameof(CanLock));

    partial void OnIsLockedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(LockButtonText));
        OnPropertyChanged(nameof(LockIconKey));
    }

    partial void OnEntryChanged(string value)
    {
        if (IsLocked)
        {
            return;
        }

        var typed = value.Trim();
        if (typed.Length == 0)
        {
            Hint = null;
            _cancel();
            return;
        }

        var problem = _validate(typed);
        Hint = problem;
        if (problem is not null)
        {
            _cancel();
            return;
        }

        _commit(typed);
    }
}
