namespace AlertaIBT.Core.Audio;

/// <summary>
/// The Windows per-app output (Settings, System, Sound, Volume mixer: "Output device" of one program), as thin as it can be so the rules
/// live in <see cref="AudioController"/> and are tested against a fake. Never throws: a failure is <c>false</c> or <c>null</c>.
/// </summary>
public interface IAppOutputPolicy
{
    /// <summary>
    /// The output Windows keeps for the program of <paramref name="processId"/>: the endpoint id (<c>{0.0.0.00000000}.{guid}</c>), an empty
    /// string when the program follows the Windows default, or <c>null</c> when it could not be read (the interface is not there, the process is gone).
    /// </summary>
    string? GetPersistedEndpoint(int processId);

    /// <summary>Makes the program of <paramref name="processId"/> play on <paramref name="endpointId"/> (or follow the Windows default when <c>null</c>). Returns whether Windows accepted it.</summary>
    bool SetPersistedEndpoint(int processId, string? endpointId);
}

/// <summary>The device-id spelling the per-app output interface uses: <c>\\?\SWD#MMDEVAPI#{endpoint id}#{e6327cad-dcec-4949-ae8a-991e976a79d2}</c> for an output.</summary>
public static class MmdevapiDevicePath
{
    // Two backslashes, a question mark and a backslash: built from char codes so no tool can fold the repeated backslash.
    private static readonly string Prefix = new string((char)92, 2) + "?" + (char)92 + "SWD#MMDEVAPI#";
    private const string RenderSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    /// <summary>The per-app interface spelling of an output endpoint id.</summary>
    public static string ForRender(string endpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);

        return Prefix + endpointId.Trim() + RenderSuffix;
    }

    /// <summary>The endpoint id inside <paramref name="path"/>; the text itself when it is not in the per-app spelling (so an endpoint id passes through); empty for blank.</summary>
    public static string ToEndpointId(string? path)
    {
        var text = path?.Trim() ?? string.Empty;
        if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        var inner = text[Prefix.Length..];
        var hash = inner.LastIndexOf("#{", StringComparison.Ordinal);
        return hash > 0 ? inner[..hash] : inner;
    }
}
