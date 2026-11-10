using System.Globalization;

namespace AlertaIBT.App.Platform;

/// <summary>Counters of this process worth having in the log when the screen stops (Windows window-system quotas, memory, threads).</summary>
public static class ProcessCounters
{
    private const int GdiObjects = 0;
    private const int UserObjects = 1;

    /// <summary>For example <c>GDI 312, USER 140, handles 1204, threads 61, memoria 310 MB, GC 12/3/1</c>; never throws.</summary>
    public static string Describe()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var handle = process.Handle;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"GDI {GetGuiResources(handle, GdiObjects)}, USER {GetGuiResources(handle, UserObjects)}, handles {process.HandleCount}, threads {process.Threads.Count}, memoria {process.PrivateMemorySize64 / (1024 * 1024)} MB, GC {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
        }
        catch (Exception ex)
        {
            return "contadores indisponiveis (" + ex.GetType().Name + ")";
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, int flags);
}
