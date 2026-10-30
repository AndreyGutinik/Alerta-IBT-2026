using System.Globalization;

namespace AlertaIBT.Core.Settings;

/// <summary>One playlist: a folder of music (scanned recursively).</summary>
/// <param name="Name">The name on the screen; unique, case does not matter.</param>
/// <param name="Folder">The folder path.</param>
public sealed record PlaylistEntry(string Name, string Folder);

/// <summary>
/// One alarm ("despertador"). <see cref="Start"/> and <see cref="Latest"/> are local wall-clock times <c>HH:mm</c>; <see cref="Days"/> is a
/// bit mask where bit <c>(int)DayOfWeek</c> is a selected weekday (Sunday = bit 0).
/// </summary>
/// <param name="Id">Identifies the alarm (stable across edits).</param>
/// <param name="Enabled">Whether it can fire.</param>
/// <param name="Start">When it starts the music, <c>HH:mm</c>.</param>
/// <param name="Latest">"Nao iniciar depois de": a late start (PC opened late) still happens until this time, <c>HH:mm</c>.</param>
/// <param name="Days">The selected weekdays (bit mask).</param>
/// <param name="Playlist">The playlist it plays.</param>
/// <param name="LastFiredOn">The local date (<c>yyyy-MM-dd</c>) it last fired, or <c>null</c>: kept so a restart does not fire it twice in a day.</param>
public sealed record AlarmEntry(string Id, bool Enabled, string Start, string Latest, int Days, string Playlist, string? LastFiredOn = null)
{
    /// <summary>All seven days.</summary>
    public const int AllDays = 0x7F;

    /// <summary>Sunday only, the default of a new alarm.</summary>
    public const int SundayOnly = 1;

    /// <summary>The start time.</summary>
    public TimeOnly StartTime => PlayerSettings.ParseTime(Start) ?? PlayerSettings.DefaultStart;

    /// <summary>The latest start time.</summary>
    public TimeOnly LatestTime => PlayerSettings.ParseTime(Latest) ?? PlayerSettings.DefaultLatest;

    /// <summary>Whether <paramref name="day"/> is selected.</summary>
    public bool HasDay(DayOfWeek day) => (Days & (1 << (int)day)) != 0;

    /// <summary>A copy with <paramref name="day"/> selected or not.</summary>
    public AlarmEntry WithDay(DayOfWeek day, bool selected) =>
        this with { Days = selected ? Days | (1 << (int)day) : Days & ~(1 << (int)day) };

    /// <summary>A new alarm with the defaults of the owner: 18:00, not after 18:25, Sunday only.</summary>
    public static AlarmEntry CreateDefault(string id, string playlist) =>
        new(
            id,
            true,
            PlayerSettings.DefaultStart.ToString("HH:mm", CultureInfo.InvariantCulture),
            PlayerSettings.DefaultLatest.ToString("HH:mm", CultureInfo.InvariantCulture),
            SundayOnly,
            playlist);
}

/// <summary>
/// The music player ("Som" tab). Every field is optional in <c>settings.json</c>: an older file loads with the defaults. The folder of the
/// default playlist is the church's music folder of the owner; on another PC it simply finds no files until it is changed.
/// </summary>
public sealed record PlayerSettings
{
    /// <summary>The playlist that exists out of the box.</summary>
    public const string DefaultPlaylistName = "Igreja";

    /// <summary>The folder of the default playlist.</summary>
    public const string DefaultPlaylistFolder = @"C:\Users\gutni\Music\Mornye\Igreja";

    /// <summary>The words of the default output rule: "WING" and "3-4" (the church's "WING-USB (Out 3-4)").</summary>
    public static readonly IReadOnlyList<string> DefaultOutputMatch = ["WING", "3-4"];

    /// <summary>The default alarm start (18:00).</summary>
    public static readonly TimeOnly DefaultStart = new(18, 0);

    /// <summary>The default "do not start after" (18:25).</summary>
    public static readonly TimeOnly DefaultLatest = new(18, 25);

    /// <summary>The fade lengths in seconds the screen offers: 0 (dry), 0.5, 1, 2 and so on up to 10.</summary>
    public static readonly IReadOnlyList<double> FadeChoices = [0, 0.5, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

    /// <summary>The crossfade lengths in seconds the screen offers: 0 (none, gapless), 0.5, 1, 2 and so on up to 12.</summary>
    public static readonly IReadOnlyList<double> CrossfadeChoices = [0, 0.5, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

    /// <summary>The value of <paramref name="choices"/> nearest to <paramref name="seconds"/> (the lower one on a tie); the first one when it is not a number.</summary>
    public static double Nearest(IReadOnlyList<double> choices, double seconds)
    {
        ArgumentNullException.ThrowIfNull(choices);

        if (!double.IsFinite(seconds))
        {
            return choices[0];
        }

        var best = choices[0];
        foreach (var choice in choices)
        {
            if (Math.Abs(choice - seconds) < Math.Abs(best - seconds))
            {
                best = choice;
            }
        }

        return best;
    }

    /// <summary>Master switch "Player ligado": off means nothing plays, no alarm fires, the API and the media keys are ignored. On by default.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The endpoint id of the output, when known (filled in when the output is found).</summary>
    public string? OutputDeviceId { get; init; }

    /// <summary>The friendly name of the output, for the screen (filled in when the output is found).</summary>
    public string? OutputDeviceName { get; init; }

    /// <summary>The words that find the output by name when the id is not active (all must be in the name). Default WING + 3-4.</summary>
    public IReadOnlyList<string> OutputMatch { get; init; } = DefaultOutputMatch;

    /// <summary>The player's own volume, 0 to 100 (applied to the samples), 100 by default.</summary>
    public int Volume { get; init; } = 100;

    /// <summary>Whether the player is muted (the volume row's "mudo"). Off by default.</summary>
    public bool StartMuted { get; init; }

    /// <summary>Whether the next track is drawn at random. On by default.</summary>
    public bool Shuffle { get; init; } = true;

    /// <summary>What happens at the end of the list: <c>off</c>, <c>all</c> or <c>one</c>. <c>all</c> by default.</summary>
    public string Repeat { get; init; } = "all";

    /// <summary>"Teclas de midia controlam o player": the keyboard's Play/Pause, Next, Previous and Stop keys drive the player. On by default.</summary>
    public bool MediaKeys { get; init; } = true;

    /// <summary>The fade-in, in seconds, of play and of an alarm start (one of <see cref="FadeChoices"/>; 0 is dry). 0 (dry) by default. Older files kept the alarm's value here.</summary>
    public double FadeInSeconds { get; init; }

    /// <summary>The fade-out, in seconds, of pause and stop (one of <see cref="FadeChoices"/>; 0 is dry). 5 by default.</summary>
    public double FadeOutSeconds { get; init; } = 5;

    /// <summary>The crossfade ("mescla"), in seconds, at the automatic end of a track (one of <see cref="CrossfadeChoices"/>; 0 is none: the next track follows with no gap). 0 by default.</summary>
    public double CrossfadeSeconds { get; init; }

    /// <summary>The shape of the crossfade; read at the start of each one. <see cref="CrossfadeCurve.Linear"/> by default (also for files with no value).</summary>
    public CrossfadeCurve CrossfadeCurve { get; init; } = CrossfadeCurve.Linear;

    /// <summary>The name of the selected playlist.</summary>
    public string CurrentPlaylist { get; init; } = DefaultPlaylistName;

    /// <summary>The playlists.</summary>
    public IReadOnlyList<PlaylistEntry> Playlists { get; init; } = DefaultPlaylists();

    /// <summary>Master switch "Despertadores ligados". On by default.</summary>
    public bool AlarmsEnabled { get; init; } = true;

    /// <summary>The alarms.</summary>
    public IReadOnlyList<AlarmEntry> Alarms { get; init; } = DefaultAlarms();

    /// <summary>The rule built from <see cref="OutputMatch"/>.</summary>
    public Audio.DeviceMatchRule OutputRule => new(OutputMatch);

    /// <summary>The playlist that exists out of the box.</summary>
    public static IReadOnlyList<PlaylistEntry> DefaultPlaylists() => [new PlaylistEntry(DefaultPlaylistName, DefaultPlaylistFolder)];

    /// <summary>The alarm that exists out of the box: Sunday 18:00 (not after 18:25) on the default playlist.</summary>
    public static IReadOnlyList<AlarmEntry> DefaultAlarms() => [AlarmEntry.CreateDefault("domingo", DefaultPlaylistName)];

    /// <summary>The playlist called <paramref name="name"/> (case does not matter), or <c>null</c>.</summary>
    public PlaylistEntry? FindPlaylist(string? name) =>
        Playlists.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads <c>HH:mm</c> (also <c>H:mm</c>); <c>null</c> when it is not a time of day.</summary>
    public static TimeOnly? ParseTime(string? text) =>
        TimeOnly.TryParseExact(text?.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;

    /// <summary>Value equality, with the lists compared one by one (a list is otherwise compared by reference).</summary>
    public bool Equals(PlayerSettings? other) =>
        other is not null
        && Enabled == other.Enabled
        && OutputDeviceId == other.OutputDeviceId
        && OutputDeviceName == other.OutputDeviceName
        && OutputMatch.SequenceEqual(other.OutputMatch)
        && Volume == other.Volume
        && StartMuted == other.StartMuted
        && Shuffle == other.Shuffle
        && Repeat == other.Repeat
        && MediaKeys == other.MediaKeys
        && FadeInSeconds == other.FadeInSeconds
        && FadeOutSeconds == other.FadeOutSeconds
        && CrossfadeSeconds == other.CrossfadeSeconds
        && CrossfadeCurve == other.CrossfadeCurve
        && CurrentPlaylist == other.CurrentPlaylist
        && Playlists.SequenceEqual(other.Playlists)
        && AlarmsEnabled == other.AlarmsEnabled
        && Alarms.SequenceEqual(other.Alarms);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Enabled, Volume, Shuffle, Repeat, CurrentPlaylist, Playlists.Count, Alarms.Count);
}
