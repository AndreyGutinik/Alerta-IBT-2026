namespace AlertaIBT.Core.Correction;

/// <summary>
/// Gives the current <see cref="FixedRules"/> from the rules text of the settings, parsing again only when the text changed:
/// the rules compile regular expressions, which must not happen for every alert or every keystroke of the preview.
/// </summary>
public sealed class FixedRulesProvider
{
    private readonly Func<string> _text;
    private readonly object _gate = new();
    private string? _lastText;
    private FixedRules _rules = FixedRules.Empty;

    /// <param name="text">Gives the rules text (one rule per line), read on every <see cref="Current"/>.</param>
    public FixedRulesProvider(Func<string> text)
    {
        ArgumentNullException.ThrowIfNull(text);

        _text = text;
    }

    /// <summary>The rules for the current text. Never throws for a bad rules text (bad lines are ignored by the parser).</summary>
    public FixedRules Current()
    {
        var text = _text() ?? string.Empty;
        lock (_gate)
        {
            if (_lastText is null || !string.Equals(_lastText, text, StringComparison.Ordinal))
            {
                _rules = FixedRules.Parse(text);
                _lastText = text;
            }

            return _rules;
        }
    }
}
