using AlertaIBT.Core.Input;
using AlertaIBT.Core.Screens;

namespace AlertaIBT.Core.Tests.Input;

/// <summary>A cursor that lives in memory: the tests never touch the real mouse.</summary>
internal sealed class FakeCursor : INativeCursor
{
    /// <summary>What Windows reports when nothing confines the cursor: the whole virtual screen (LED left of the primary monitor).</summary>
    public static readonly ScreenRect Virtual = new(-2560, 0, 1920, 1080);

    public ScreenRect Primary { get; set; } = new(0, 0, 1920, 1080);

    public ScreenRect Clip { get; set; } = Virtual;

    public bool PrimaryAvailable { get; set; } = true;

    public bool ClipReadable { get; set; } = true;

    public bool ClipAccepted { get; set; } = true;

    public int X { get; set; } = 500;

    public int Y { get; set; } = 500;

    public int ClipCalls { get; private set; }

    public int UnclipCalls { get; private set; }

    public int ReadClipCalls { get; private set; }

    public List<(int X, int Y)> Moves { get; } = [];

    public bool TryGetPrimaryRect(out ScreenRect rect)
    {
        rect = Primary;
        return PrimaryAvailable;
    }

    public bool TryGetClip(out ScreenRect rect)
    {
        ReadClipCalls++;
        rect = Clip;
        return ClipReadable;
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
