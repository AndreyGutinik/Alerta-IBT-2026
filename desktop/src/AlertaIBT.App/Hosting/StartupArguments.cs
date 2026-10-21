namespace AlertaIBT.App.Hosting;

/// <summary>The command-line switches the app understands. Anything else is ignored.</summary>
/// <param name="Tray">
/// <c>--tray</c>: start hidden in the notification area. The Windows Run entry passes it, so booting the PC never opens a window.
/// Without it (the user opened the app by hand) the main window is shown.
/// </param>
/// <param name="TestLed">
/// <c>--test-led</c>: treat the primary monitor as the LED, whatever its size, so the bar can be developed and checked
/// on a PC that has no 2560x768 screen.
/// </param>
public sealed record StartupArguments(bool Tray, bool TestLed)
{
    /// <summary>Reads the switches, ignoring case and unknown words.</summary>
    public static StartupArguments Parse(IEnumerable<string>? args)
    {
        var tray = false;
        var testLed = false;
        foreach (var arg in args ?? [])
        {
            if (string.Equals(arg, "--tray", StringComparison.OrdinalIgnoreCase))
            {
                tray = true;
            }
            else if (string.Equals(arg, "--test-led", StringComparison.OrdinalIgnoreCase))
            {
                testLed = true;
            }
        }

        return new StartupArguments(tray, testLed);
    }
}
