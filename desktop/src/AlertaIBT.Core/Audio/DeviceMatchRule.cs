namespace AlertaIBT.Core.Audio;

/// <summary>
/// A name rule for an output device that works while the device is absent: every word must appear in the device name (case does not
/// matter). <c>WING</c> + <c>3-4</c> matches "WING-USB (Out 3-4)". The player uses <c>WING + 3-4</c>; the Holyrics output uses <c>WING + 1-2</c>.
/// An empty rule matches nothing.
/// </summary>
/// <param name="AllOf">The words that must all be contained in the name.</param>
public sealed record DeviceMatchRule(IReadOnlyList<string> AllOf)
{
    /// <summary>The rule that matches nothing.</summary>
    public static DeviceMatchRule None { get; } = new([]);

    /// <summary>Whether the rule has no words (it matches nothing).</summary>
    public bool IsEmpty => AllOf.Count == 0;

    /// <summary>The rule as one line for the screen, such as <c>WING + 3-4</c>.</summary>
    public string Text => string.Join(" + ", AllOf);

    /// <summary>Reads the words of a line such as <c>WING + 3-4</c> (split on <c>+</c>; blanks dropped).</summary>
    public static DeviceMatchRule Parse(string? text) =>
        new((text ?? string.Empty).Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Whether <paramref name="deviceName"/> contains every word (case does not matter). The "2- " that Windows puts in front of a second
    /// device of the same kind is ignored too, so the name of a device still matches when it is plugged into another USB port.
    /// </summary>
    public bool Matches(string? deviceName)
    {
        if (IsEmpty || string.IsNullOrWhiteSpace(deviceName))
        {
            return false;
        }

        var plain = AudioNames.NormalizeDevice(deviceName);
        return AllOf.All(word =>
            deviceName.Contains(word, StringComparison.OrdinalIgnoreCase)
            || plain.Contains(AudioNames.NormalizeDevice(word), StringComparison.Ordinal));
    }

    /// <summary>Value equality, with the words compared one by one.</summary>
    public bool Equals(DeviceMatchRule? other) => other is not null && AllOf.SequenceEqual(other.AllOf, StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(AllOf.Count, AllOf.FirstOrDefault());
}

/// <summary>How an output was found.</summary>
public enum OutputChoiceKind
{
    /// <summary>The saved endpoint id is active.</summary>
    ById,

    /// <summary>The id is not active (or was never known) and an active device matched the name rule.</summary>
    ByRule,

    /// <summary>Neither matched: the Windows default output is used.</summary>
    Default,

    /// <summary>There is no active output at all.</summary>
    None,
}

/// <summary>The output picked by <see cref="OutputResolver"/>.</summary>
/// <param name="Kind">How it was found.</param>
/// <param name="Device">The device; for <see cref="OutputChoiceKind.Default"/> it is the default device (or <c>null</c> when Windows reports none, and the backend then opens the default itself).</param>
public sealed record OutputChoice(OutputChoiceKind Kind, AudioDevice? Device)
{
    /// <summary>Whether the wished device is not the one in use (the default output stands in for it).</summary>
    public bool IsFallback => Kind is OutputChoiceKind.Default or OutputChoiceKind.None;
}

/// <summary>The order in which an output is chosen: exact endpoint id, else the first active device matching the name rule, else the Windows default.</summary>
public static class OutputResolver
{
    /// <summary>
    /// The device a rule and a saved id point at, without any stand-in: the saved id when it is active, else the first active device
    /// matching the rule, else none (`Device` is <c>null</c>, kind <see cref="OutputChoiceKind.None"/>). Used where a missing device means
    /// "leave things alone" (the lock of the default output, the Holyrics output).
    /// </summary>
    public static OutputChoice Find(IReadOnlyList<AudioDevice> active, string? savedId, DeviceMatchRule rule)
    {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(rule);

        if (!string.IsNullOrWhiteSpace(savedId)
            && active.FirstOrDefault(d => string.Equals(d.Id, savedId.Trim(), StringComparison.OrdinalIgnoreCase)) is { } byId)
        {
            return new OutputChoice(OutputChoiceKind.ById, byId);
        }

        return active.FirstOrDefault(d => rule.Matches(d.Name)) is { } byRule
            ? new OutputChoice(OutputChoiceKind.ByRule, byRule)
            : new OutputChoice(OutputChoiceKind.None, null);
    }

    /// <summary>Picks the output.</summary>
    /// <param name="active">The active outputs.</param>
    /// <param name="defaultId">The id of the Windows default output (multimedia role), or <c>null</c>.</param>
    /// <param name="savedId">The saved endpoint id, or <c>null</c> when none is known.</param>
    /// <param name="rule">The name rule.</param>
    public static OutputChoice Resolve(IReadOnlyList<AudioDevice> active, string? defaultId, string? savedId, DeviceMatchRule rule)
    {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(rule);

        if (!string.IsNullOrWhiteSpace(savedId)
            && active.FirstOrDefault(d => string.Equals(d.Id, savedId.Trim(), StringComparison.OrdinalIgnoreCase)) is { } byId)
        {
            return new OutputChoice(OutputChoiceKind.ById, byId);
        }

        if (active.FirstOrDefault(d => rule.Matches(d.Name)) is { } byRule)
        {
            return new OutputChoice(OutputChoiceKind.ByRule, byRule);
        }

        if (active.Count == 0)
        {
            return new OutputChoice(OutputChoiceKind.None, null);
        }

        var standIn = string.IsNullOrWhiteSpace(defaultId)
            ? null
            : active.FirstOrDefault(d => string.Equals(d.Id, defaultId.Trim(), StringComparison.OrdinalIgnoreCase));
        return new OutputChoice(OutputChoiceKind.Default, standIn);
    }
}
