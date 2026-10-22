using AlertaIBT.App.Platform;
using AlertaIBT.Core.Input;
using AlertaIBT.Core.Screens;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.App.Tests.Platform;

/// <summary>A cursor that lives in memory: no automated test touches the real mouse, clip or keyboard.</summary>
internal sealed class FakeCursor : INativeCursor
{
    public static readonly ScreenRect Virtual = new(-2560, 0, 1920, 1080);

    public ScreenRect Primary { get; set; } = new(0, 0, 1920, 1080);

    public ScreenRect Clip { get; private set; } = Virtual;

    public bool ClipAccepted { get; set; } = true;

    public int X { get; set; } = 500;

    public int Y { get; set; } = 500;

    public int ClipCalls { get; private set; }

    public int UnclipCalls { get; private set; }

    public List<(int X, int Y)> Moves { get; } = [];

    public bool TryGetPrimaryRect(out ScreenRect rect)
    {
        rect = Primary;
        return true;
    }

    public bool TryGetClip(out ScreenRect rect)
    {
        rect = Clip;
        return true;
    }

    public bool TryClip(ScreenRect rect)
    {
        ClipCalls++;
        if (!ClipAccepted)
        {
            return false;
        }

        Clip = rect;
        return true;
    }

    public void Unclip()
    {
        UnclipCalls++;
        Clip = Virtual;
    }

    public bool TryGetPosition(out int x, out int y)
    {
        x = X;
        y = Y;
        return true;
    }

    public bool TrySetPosition(int x, int y)
    {
        Moves.Add((x, y));
        X = x;
        Y = y;
        return true;
    }
}

/// <summary>A keyboard hook that is never installed in the system: the test feeds it keys by hand.</summary>
internal sealed class FakeKeyboardHook : IKeyboardHook
{
    private KeyObserver? _observer;

    public bool Accept { get; set; } = true;

    public int Installs { get; private set; }

    public int Uninstalls { get; private set; }

    public bool IsInstalled => _observer is not null;

    public bool Install(KeyObserver observer)
    {
        Installs++;
        if (!Accept)
        {
            return false;
        }

        _observer = observer;
        return true;
    }

    public void Uninstall()
    {
        Uninstalls++;
        _observer = null;
    }

    public void Dispose() => Uninstall();

    // What Windows does for each key event while the hook is installed (nothing when it is not).
    public void Key(int virtualKey, bool down, uint timeMs, bool alt = false) => _observer?.Invoke(virtualKey, down, alt, timeMs);

    public void Tap(int virtualKey, uint timeMs)
    {
        Key(virtualKey, true, timeMs);
        Key(virtualKey, false, timeMs + 40);
    }
}

/// <summary>A hotkey registrar that registers nothing in the system.</summary>
internal sealed class FakeHotkeyRegistrar : IHotkeyRegistrar
{
    public bool Accept { get; set; } = true;

    public List<string> Registered { get; } = [];

    public bool Register(HotkeyGesture gesture)
    {
        Registered.Add(gesture.ToString());
        return Accept;
    }

    public void Unregister()
    {
    }
}

/// <summary>Collects what the app logs.</summary>
internal sealed class ListLogger : ILogger
{
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Add($"{logLevel}: {formatter(state, exception)}");
}
