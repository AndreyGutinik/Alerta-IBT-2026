using AlertaIBT.Core.Screens;
using AlertaIBT.Core.Temple;

namespace AlertaIBT.Core.Settings;

/// <summary>
/// "Templo menor": a mode turned on once, on the PC of the smaller temple. It switches off the functions listed in <see cref="Disabled"/> (they really stop and
/// disappear from the interface) and moves the target screen from the normal LED size to the temple size. Saved like every switch (it comes back as it was and is
/// applied before the services start). Nothing else in the settings file is touched by it: leaving the mode brings everything back exactly as it was.
/// </summary>
public sealed record TempleSettings
{
    /// <summary>The smallest accepted screen side, in pixels.</summary>
    public const int MinSide = 320;

    /// <summary>The largest accepted screen side, in pixels.</summary>
    public const int MaxSide = 16384;

    /// <summary>Whether the mode is on. Off by default and in an older file.</summary>
    public bool Enabled { get; init; }

    /// <summary>The functions turned off while the mode is on (names of <see cref="TempleFeatures.Key"/>). Empty on the first time: the owner picks.</summary>
    public IReadOnlyList<string> Disabled { get; init; } = [];

    /// <summary>The normal LED width (2560).</summary>
    public int NormalWidth { get; init; } = TargetSize.DefaultNormal.Width;

    /// <summary>The normal LED height (768).</summary>
    public int NormalHeight { get; init; } = TargetSize.DefaultNormal.Height;

    /// <summary>The screen width used in Templo menor (1920).</summary>
    public int TempleWidth { get; init; } = TargetSize.DefaultTemple.Width;

    /// <summary>The screen height used in Templo menor (1080).</summary>
    public int TempleHeight { get; init; } = TargetSize.DefaultTemple.Height;

    /// <summary>The normal size.</summary>
    public TargetSize NormalSize => new(NormalWidth, NormalHeight);

    /// <summary>The size of Templo menor.</summary>
    public TargetSize TempleSize => new(TempleWidth, TempleHeight);

    /// <summary>The size in force now: the temple one while the mode is on, else the normal one.</summary>
    public TargetSize Target => Enabled ? TempleSize : NormalSize;

    /// <summary>Whether <paramref name="feature"/> is on the list of functions to turn off (whatever the mode says).</summary>
    public bool Lists(TempleFeature feature) => Disabled.Contains(TempleFeatures.Key(feature), StringComparer.OrdinalIgnoreCase);

    /// <summary>The one rule of the whole feature: a function is active unless the mode is on and the function is on the list.</summary>
    public bool IsActive(TempleFeature feature) => !(Enabled && Lists(feature));

    /// <summary>The disabled list as features, in the order of <see cref="TempleFeatures.All"/>, unknown names dropped.</summary>
    public IReadOnlyList<TempleFeature> DisabledFeatures => TempleFeatures.All.Where(Lists).ToList();

    /// <summary>Value equality with the list compared as a set.</summary>
    public bool Equals(TempleSettings? other) =>
        other is not null
        && Enabled == other.Enabled
        && NormalWidth == other.NormalWidth
        && NormalHeight == other.NormalHeight
        && TempleWidth == other.TempleWidth
        && TempleHeight == other.TempleHeight
        && DisabledFeatures.SequenceEqual(other.DisabledFeatures);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Enabled, NormalWidth, NormalHeight, TempleWidth, TempleHeight, Disabled.Count);
}
