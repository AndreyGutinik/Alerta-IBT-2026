using AlertaIBT.Core.Input;
using AlertaIBT.Core.Player;
using AlertaIBT.Core.Settings;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.App.Platform;

/// <summary>
/// Sends the keyboard's media keys (Play/Pause, Next, Previous, Stop) to the music player. It does not own a hook: the one passive keyboard
/// hook of <see cref="MouseLockController"/> feeds it, so there is never a second hook, and a key is never swallowed (the other programs
/// still hear it) nor recorded. It does only a lookup inside the hook; the command itself is posted to the UI dispatcher and then run on
/// the thread pool, because a command may open a file or a device.
/// </summary>
public sealed class MediaKeyRouter
{
    private readonly SettingsService _settings;
    private readonly IPlayerControl _player;
    private readonly Action<Action> _post;
    private readonly Action<Action> _run;
    private readonly ILogger? _logger;
    private readonly MediaKeyDetector _detector = new();

    /// <param name="settings">The settings ("Player ligado" and "Teclas de m\U000000EDdia controlam o player").</param>
    /// <param name="player">The player.</param>
    /// <param name="post">Runs an action on the UI thread.</param>
    /// <param name="run">Runs the command off the UI thread; the thread pool when null.</param>
    /// <param name="logger">Receives the command (never a key code).</param>
    public MediaKeyRouter(SettingsService settings, IPlayerControl player, Action<Action> post, Action<Action>? run = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(post);

        _settings = settings;
        _player = player;
        _post = post;
        _run = run ?? (action => _ = Task.Run(action));
        _logger = logger;
    }

    /// <summary>Whether the media keys should be listened to now (the player is on and the option is on): the hook is installed only then.</summary>
    public bool Wanted => _settings.Current.Player is { Enabled: true, MediaKeys: true };

    /// <summary>Forgets what is held (the hook was installed again).</summary>
    public void Reset() => _detector.Reset();

    /// <summary>Hears one key event from the hook. Does nothing for any other key and when the player or the option is off.</summary>
    public void Feed(int virtualKey, bool keyDown)
    {
        if (_detector.Feed(virtualKey, keyDown) is not { } key || !Wanted)
        {
            return;
        }

        _post(() => _run(() =>
        {
            try
            {
                var result = MediaKeyRouting.Execute(key, _player);
                _logger?.LogInformation("Tecla de midia: {Command} ({Result})", key, result.Success ? "ok" : result.Error);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "A tecla de midia {Command} falhou", key);
            }
        }));
    }
}
