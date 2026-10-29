namespace AlertaIBT.Core.Ndi;

/// <summary>
/// Finds the NDI runtime DLL that NDI Tools (or the runtime redistributable) installed. The DLL is never shipped with this app: it is loaded
/// from the folder in <c>NDI_RUNTIME_DIR_V6</c> (then V5, V4, V3) or from the default NDI Tools folders.
/// </summary>
public static class NdiRuntimeLocator
{
    /// <summary>The file name of the 64-bit runtime.</summary>
    public const string DllName = "Processing.NDI.Lib.x64.dll";

    /// <summary>The environment variables that name the runtime folder, newest first.</summary>
    public static readonly IReadOnlyList<string> EnvironmentVariables =
        ["NDI_RUNTIME_DIR_V6", "NDI_RUNTIME_DIR_V5", "NDI_RUNTIME_DIR_V4", "NDI_RUNTIME_DIR_V3"];

    /// <summary>The folders searched after the variables.</summary>
    public static readonly IReadOnlyList<string> DefaultFolders =
    [
        @"C:\Program Files\NDI\NDI 6 Runtime\v6",
        @"C:\Program Files\NDI\NDI 6 Tools\Runtime",
        @"C:\Program Files\NDI\NDI 5 Runtime",
        @"C:\Program Files\NDI\NDI 5 Tools\Runtime",
    ];

    /// <summary>The folders to try, in order, without repeats.</summary>
    public static IEnumerable<string> Candidates(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in EnvironmentVariables)
        {
            var value = environment(name)?.Trim();
            if (!string.IsNullOrEmpty(value) && seen.Add(value))
            {
                yield return value;
            }
        }

        foreach (var folder in DefaultFolders)
        {
            if (seen.Add(folder))
            {
                yield return folder;
            }
        }
    }

    /// <summary>The full path of the first runtime DLL that exists, or <c>null</c>.</summary>
    public static string? FindDll(Func<string, string?> environment, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);

        foreach (var folder in Candidates(environment))
        {
            var path = Path.Combine(folder, DllName);
            if (fileExists(path))
            {
                return path;
            }
        }

        return null;
    }
}
