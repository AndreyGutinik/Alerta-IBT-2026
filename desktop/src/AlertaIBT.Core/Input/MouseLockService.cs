using AlertaIBT.Core.Screens;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Input;

/// <summary>
/// What the mouse lock needs from the system, in physical pixels. The real one (<c>WinCursor</c> in the app) calls
/// <c>ClipCursor</c>, <c>GetClipCursor</c>, <c>GetCursorPos</c>, <c>SetCursorPos</c> and <c>GetMonitorInfo</c>; tests use a fake, so
/// no automated test ever grabs or moves the real mouse.
/// </summary>
public interface INativeCursor
{
    /// <summary>The bounds of the Windows primary monitor right now. False when Windows cannot say.</summary>
    bool TryGetPrimaryRect(out ScreenRect rect);

    /// <summary>The rectangle the cursor is confined to now (the whole virtual screen when nothing confines it). False when it cannot be read (secure desktop).</summary>
    bool TryGetClip(out ScreenRect rect);

    /// <summary>Confines the cursor to <paramref name="rect"/>. False when Windows refuses (secure desktop, lock screen).</summary>
    bool TryClip(ScreenRect rect);

    /// <summary>Lets the cursor go anywhere again.</summary>
    void Unclip();

    /// <summary>Where the cursor is.</summary>
    bool TryGetPosition(out int x, out int y);

    /// <summary>Moves the cursor.</summary>
    bool TrySetPosition(int x, int y);
}

/// <summary>
/// Confines the mouse cursor to the primary monitor (so it never ends up on the LED wall or the return screen) and brings it to the
/// middle of that monitor on demand. Windows and some programs reset the confinement (display changes, a lock screen, remote
/// desktop, a game), so <see cref="Verify"/> puts it back; the app calls that on display and session changes and from a 1 s
/// watchdog. Not thread-safe: use it from the UI thread. When the process dies, Windows releases the confinement by itself.
/// </summary>
public sealed class MouseLockService : IDisposable
{
    private readonly INativeCursor _cursor;
    private readonly ILogger? _logger;
    private ScreenRect _applied;
    private bool _outOfStep;
    private bool _applyFailing;

    /// <param name="cursor">The system calls.</param>
    /// <param name="logger">Receives the lock, the release and a lost confinement (never one line per second).</param>
    public MouseLockService(INativeCursor cursor, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        _cursor = cursor;
        _logger = logger;
    }

    /// <summary>Tells (when set) which program had the focus when the confinement was found undone: it goes in the log line, to find out who releases it. Must not throw.</summary>
    public Func<string?>? OutOfStepContext { get; set; }

    /// <summary>Whether the lock is on (the app asked for it and Windows accepted it the last time).</summary>
    public bool IsLocked { get; private set; }

    /// <summary>The rectangle the cursor was last confined to; meaningful only while <see cref="IsLocked"/>.</summary>
    public ScreenRect Applied => _applied;

    /// <summary>Raised when <see cref="IsLocked"/> changed.</summary>
    public event Action<bool>? LockedChanged;

    /// <summary>
    /// Confines the cursor to the primary monitor. A cursor on another monitor is pulled to the nearest point of the primary one.
    /// Does nothing when it is on already. False (and the lock stays off) when Windows gives no primary monitor or refuses.
    /// </summary>
    public bool Lock()
    {
        if (IsLocked)
        {
            return true;
        }

        if (!_cursor.TryGetPrimaryRect(out var primary) || primary.IsEmpty)
        {
            _logger?.LogWarning("Travar o mouse: o Windows nao informou o monitor principal");
            return false;
        }

        if (!_cursor.TryClip(primary))
        {
            _logger?.LogWarning("Travar o mouse: o Windows recusou travar o cursor");
            return false;
        }

        _applied = primary;
        PullIn(primary);
        IsLocked = true;
        _outOfStep = false;
        _applyFailing = false;
        _logger?.LogInformation("Mouse travado no monitor principal ({Width}x{Height})", primary.Width, primary.Height);
        LockedChanged?.Invoke(true);
        return true;
    }

    /// <summary>Lets the cursor go anywhere again. Does nothing when the lock is off.</summary>
    public void Unlock()
    {
        if (!IsLocked)
        {
            return;
        }

        IsLocked = false;
        try
        {
            _cursor.Unclip();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Destravar o mouse falhou");
        }

        _logger?.LogInformation("Mouse destravado");
        LockedChanged?.Invoke(false);
    }

    /// <summary>Turns the lock on or off. Returns whether it is on afterwards.</summary>
    public bool Toggle()
    {
        if (IsLocked)
        {
            Unlock();
        }
        else
        {
            Lock();
        }

        return IsLocked;
    }

    /// <summary>
    /// Puts the confinement back when something undid it or the primary monitor changed: it compares what Windows holds with the
    /// wanted rectangle and acts only when they differ. Cheap and allocation-free while everything is as wanted; call it on display
    /// and session changes and every second. Does nothing when the lock is off or when Windows cannot be asked (secure desktop).
    /// </summary>
    public void Verify()
    {
        if (!IsLocked || !_cursor.TryGetPrimaryRect(out var wanted) || wanted.IsEmpty || !_cursor.TryGetClip(out var current))
        {
            return;
        }

        if (current == wanted)
        {
            if (_outOfStep)
            {
                _outOfStep = false;
                _applyFailing = false;
            }

            return;
        }

        if (!_cursor.TryClip(wanted))
        {
            if (!_applyFailing)
            {
                _applyFailing = true;
                _logger?.LogWarning("Mouse destravado pelo sistema e o Windows recusou travar de novo (tentando a cada segundo)");
            }

            return;
        }

        _applied = wanted;
        PullIn(wanted);
        if (!_outOfStep)
        {
            _outOfStep = true; // one line when it is lost, not one per second while a program keeps resetting it
            string? context = null;
            try
            {
                context = OutOfStepContext?.Invoke();
            }
            catch (Exception)
            {
                // Only a clue for the log.
            }

            _logger?.LogInformation("Travamento do mouse refeito (o sistema ou um programa o tinha soltado; programa em primeiro plano: {Foreground})", context ?? "desconhecido");
        }

        _applyFailing = false;
    }

    /// <summary>
    /// Moves the cursor to the center of the primary monitor, from wherever it is, with the lock on or off. With the lock on the
    /// confinement is checked first, so a primary monitor that just changed cannot refuse the move. False when Windows gave no
    /// primary monitor or refused the move.
    /// </summary>
    public bool Recenter()
    {
        if (!_cursor.TryGetPrimaryRect(out var primary) || primary.IsEmpty)
        {
            return false;
        }

        Verify();
        return _cursor.TrySetPosition(primary.CenterX, primary.CenterY);
    }

    /// <summary>Gives the cursor back (see <see cref="Unlock"/>).</summary>
    public void Dispose() => Unlock();

    // ClipCursor already moves a cursor that is outside; doing it here too keeps the promise whatever Windows does.
    private void PullIn(ScreenRect rect)
    {
        if (_cursor.TryGetPosition(out var x, out var y) && !rect.Contains(x, y))
        {
            var (nx, ny) = rect.Clamp(x, y);
            _cursor.TrySetPosition(nx, ny);
        }
    }
}
