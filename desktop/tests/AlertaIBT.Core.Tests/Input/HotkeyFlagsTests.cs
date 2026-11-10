using AlertaIBT.Core.Input;

namespace AlertaIBT.Core.Tests.Input;

// Batch 42: every global shortcut is a one-shot command and is registered with MOD_NOREPEAT, so a key held down never repeats it.
public sealed class HotkeyFlagsTests
{
    [Fact]
    public void A_registration_always_carries_the_no_repeat_bit_and_keeps_the_modifiers()
    {
        Assert.Equal(0x4000u, HotkeyFlags.NoRepeat);
        Assert.Equal(0x4000u, HotkeyFlags.ForRegistration(HotkeyModifiers.None));
        Assert.Equal(0x4000u | (uint)HotkeyModifiers.Control, HotkeyFlags.ForRegistration(HotkeyModifiers.Control));
        Assert.Equal(0x4000u | (uint)(HotkeyModifiers.Control | HotkeyModifiers.Alt), HotkeyFlags.ForRegistration(HotkeyModifiers.Control | HotkeyModifiers.Alt));
    }

    // The lint of the rule: any RegisterHotKey call in the app goes through HotkeyFlags.ForRegistration (so nobody registers a shortcut that repeats by accident).
    [Fact]
    public void Every_RegisterHotKey_call_in_the_app_goes_through_HotkeyFlags()
    {
        var root = FindDesktopFolder();
        var calls = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "AlertaIBT.App"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var line in File.ReadAllLines(file))
            {
                var code = line.TrimStart();
                if (code.Contains("RegisterHotKey(", StringComparison.Ordinal)
                    && !code.StartsWith("///", StringComparison.Ordinal)
                    && !code.StartsWith("//", StringComparison.Ordinal)
                    && !code.Contains("static extern", StringComparison.Ordinal))
                {
                    calls.Add(Path.GetFileName(file) + ": " + code);
                }
            }
        }

        Assert.NotEmpty(calls); // the scan found the real registrations
        Assert.All(calls, call => Assert.Contains("HotkeyFlags.ForRegistration", call, StringComparison.Ordinal));
    }

    private static string FindDesktopFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AlertaIBT.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("The desktop folder (AlertaIBT.slnx) was not found above the test output.");
    }
}
