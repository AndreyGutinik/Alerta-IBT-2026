using AlertaIBT.Core.Player;

namespace AlertaIBT.Core.Input;

/// <summary>A media key of the keyboard.</summary>
public enum MediaKey
{
    /// <summary>Play/Pause.</summary>
    PlayPause,

    /// <summary>Next track.</summary>
    Next,

    /// <summary>Previous track.</summary>
    Previous,

    /// <summary>Stop.</summary>
    Stop,
}

/// <summary>
/// Recognizes the media keys from a stream of key events, the same way <see cref="TripleCtrlDetector"/> does: it keeps no key, only which
/// of the four media keys is held (so auto-repeat of a held key is one press). Not thread-safe: feed it from the thread of the hook.
/// </summary>
public sealed class MediaKeyDetector
{
    /// <summary>VK_MEDIA_NEXT_TRACK.</summary>
    public const int VkNext = 0xB0;

    /// <summary>VK_MEDIA_PREV_TRACK.</summary>
    public const int VkPrevious = 0xB1;

    /// <summary>VK_MEDIA_STOP.</summary>
    public const int VkStop = 0xB2;

    /// <summary>VK_MEDIA_PLAY_PAUSE.</summary>
    public const int VkPlayPause = 0xB3;

    private int _held; // bit per media key

    /// <summary>The media key behind a virtual-key code, or <c>null</c> when it is not one.</summary>
    public static MediaKey? KeyOf(int virtualKey) => virtualKey switch
    {
        VkPlayPause => MediaKey.PlayPause,
        VkNext => MediaKey.Next,
        VkPrevious => MediaKey.Previous,
        VkStop => MediaKey.Stop,
        _ => null,
    };

    /// <summary>Forgets what is held (the hook was removed and put back, for instance).</summary>
    public void Reset() => _held = 0;

    /// <summary>Feeds one key event.</summary>
    /// <returns>The media key when this event is the press of one (not a repeat, not the release); otherwise <c>null</c>.</returns>
    public MediaKey? Feed(int virtualKey, bool keyDown)
    {
        if (KeyOf(virtualKey) is not { } key)
        {
            return null;
        }

        var bit = 1 << (int)key;
        if (!keyDown)
        {
            _held &= ~bit;
            return null;
        }

        if ((_held & bit) != 0)
        {
            return null; // auto-repeat of a key that is still down
        }

        _held |= bit;
        return key;
    }
}

/// <summary>What a media key does to the player.</summary>
public static class MediaKeyRouting
{
    /// <summary>
    /// Sends <paramref name="key"/> to the player. Play/Pause pauses while sound is coming out and plays otherwise; a pause that is still
    /// fading out counts as "not playing", so pressing again brings the music back.
    /// </summary>
    public static PlayerResult Execute(MediaKey key, IPlayerControl player)
    {
        ArgumentNullException.ThrowIfNull(player);

        switch (key)
        {
            case MediaKey.Next:
                return player.Next();
            case MediaKey.Previous:
                return player.Previous();
            case MediaKey.Stop:
                return player.Stop();
            default:
                var status = player.GetStatus();
                return status.State == PlayerState.Playing && !status.Fading ? player.Pause() : player.Play();
        }
    }
}
