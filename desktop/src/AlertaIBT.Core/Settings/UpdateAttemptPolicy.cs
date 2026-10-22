namespace AlertaIBT.Core.Settings;

/// <summary>
/// The rule that keeps a broken update from restarting the app over and over: the same target version is attempted at most
/// <see cref="MaxAttempts"/> times in <see cref="Window"/>. The count lives in <see cref="SystemSettings"/>, so it survives the
/// restarts it is there to limit.
/// </summary>
public static class UpdateAttemptPolicy
{
    /// <summary>The most attempts for one version inside one window.</summary>
    public const int MaxAttempts = 2;

    /// <summary>The length of a window, counted from the first attempt in it.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>Asks to attempt <paramref name="version"/> now and, when that is allowed, returns the settings that remember it.</summary>
    /// <param name="system">The settings now.</param>
    /// <param name="version">The version about to be installed.</param>
    /// <param name="now">The current time.</param>
    /// <param name="next">The settings with the attempt recorded; unchanged when the attempt is refused.</param>
    /// <returns><c>false</c> when this version was already attempted <see cref="MaxAttempts"/> times in the current window.</returns>
    public static bool TryRecord(SystemSettings system, string version, DateTimeOffset now, out SystemSettings next)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var sameVersion = string.Equals(system.UpdateAttemptVersion, version, StringComparison.Ordinal);

        // A window that is over (or whose start is in the future: the clock was set back) starts again.
        var inWindow = sameVersion
            && system.UpdateAttemptSince is { } since
            && now >= since
            && now - since < Window;

        if (inWindow && system.UpdateAttempts >= MaxAttempts)
        {
            next = system;
            return false;
        }

        next = inWindow
            ? system with { UpdateAttempts = system.UpdateAttempts + 1 }
            : system with { UpdateAttemptVersion = version, UpdateAttemptSince = now, UpdateAttempts = 1 };
        return true;
    }
}
