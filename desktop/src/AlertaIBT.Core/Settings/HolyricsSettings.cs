namespace AlertaIBT.Core.Settings;

/// <summary>What <see cref="HolyricsSettings.ParseHostEntry"/> made of the address box.</summary>
/// <param name="Valid">Whether the host can be used.</param>
/// <param name="Host">The clean host (an IP address or a name), or what was left of the text when not valid.</param>
/// <param name="Port">A port that came inside the text, or null.</param>
/// <param name="Hint">A short pt-BR note: what was fixed, or why the text is refused; null when the text was already clean.</param>
public readonly record struct HostEntry(bool Valid, string Host, int? Port, string? Hint);

/// <summary>
/// The connection to the HTTP API of Holyrics (the "API Server" of its settings): where it listens and the token that may run the app's few actions. Every field is
/// optional in <c>settings.json</c>. The token is only ever held protected (DPAPI, see <see cref="SettingsSecrets"/>). Not part of the backup.
/// </summary>
public sealed record HolyricsSettings
{
    /// <summary>The address Holyrics listens on by default: this PC.</summary>
    public const string DefaultHost = "127.0.0.1";

    /// <summary>The default port of the API Server of Holyrics.</summary>
    public const int DefaultPort = 8091;

    /// <summary>The PC that runs Holyrics (an IP address or a name); this PC by default.</summary>
    public string Host { get; init; } = DefaultHost;

    /// <summary>The port of the API Server (1 to 65535).</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>The API token, protected with DPAPI (base64); <c>null</c> when none. Never held in clear.</summary>
    public string? TokenProtected { get; init; }

    /// <summary>"Setas do painel rápido controlam o Holyrics": while the text box of the tray panel has the focus, Left and Right are slide back and next.</summary>
    public bool ArrowsEnabled { get; init; } = true;

    /// <summary>
    /// Reads what the owner typed in the address box and cleans it: spaces around it, a leading <c>http://</c> or <c>https://</c>, a path or trailing slash, a trailing dot,
    /// and a <c>:port</c> (which moves to the port box when it is clear). Anything ambiguous (spaces inside, two colons that are not an IPv6 address, a port out of range) is refused with the reason.
    /// </summary>
    /// <param name="text">What is in the address box.</param>
    public static HostEntry ParseHostEntry(string? text)
    {
        var t = (text ?? string.Empty).Trim();
        if (t.Length == 0)
        {
            return new HostEntry(false, string.Empty, null, "digite o endere\U000000E7o do Holyrics");
        }

        if (t.Any(char.IsWhiteSpace))
        {
            return new HostEntry(false, t, null, "o endere\U000000E7o n\U000000E3o pode ter espa\U000000E7os");
        }

        var changed = false;
        foreach (var scheme in new[] { "http://", "https://" })
        {
            if (t.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                t = t[scheme.Length..];
                changed = true;
                break;
            }
        }

        var cut = t.IndexOfAny(['/', '?', '#']);
        if (cut >= 0)
        {
            t = t[..cut];
            changed = true;
        }

        int? port = null;
        string host;
        if (t.StartsWith('['))
        {
            var close = t.IndexOf(']');
            if (close < 0)
            {
                return new HostEntry(false, t, null, "endere\U000000E7o IPv6 sem o ]");
            }

            host = t[1..close];
            var rest = t[(close + 1)..];
            if (rest.Length > 0 && (rest[0] != ':' || !TryPort(rest[1..], out port)))
            {
                return new HostEntry(false, t, null, "porta inv\U000000E1lida (use de 1 a 65535)");
            }

            changed = true;
        }
        else if (System.Net.IPAddress.TryParse(t, out _))
        {
            host = t;
        }
        else
        {
            var colon = t.IndexOf(':');
            if (colon >= 0 && colon != t.LastIndexOf(':'))
            {
                return new HostEntry(false, t, null, "endere\U000000E7o com v\U000000E1rios dois-pontos (IPv6 vai entre colchetes)");
            }

            if (colon >= 0)
            {
                host = t[..colon];
                if (!TryPort(t[(colon + 1)..], out port))
                {
                    return new HostEntry(false, t, null, "porta inv\U000000E1lida (use de 1 a 65535)");
                }
            }
            else
            {
                host = t;
            }
        }

        var trimmed = host.TrimEnd('.');
        changed |= trimmed.Length != host.Length;
        host = trimmed;
        if (!IsValidHost(host))
        {
            return new HostEntry(false, host, null, "endere\U000000E7o inv\U000000E1lido (use um IP como 127.0.0.1 ou um nome)");
        }

        var hint = port is not null ? "usei a porta " + port + " que veio no endere\U000000E7o" : changed ? "ajustei o endere\U000000E7o" : null;
        return new HostEntry(true, host, port, hint);
    }

    private static bool TryPort(string text, out int? port)
    {
        port = null;
        if (text.Length is 0 or > 5 || !text.All(char.IsAsciiDigit) || !int.TryParse(text, out var value) || value is < 1 or > 65535)
        {
            return false;
        }

        port = value;
        return true;
    }

    /// <summary>Whether <paramref name="host"/> is usable as the host of the URL: a name or an IP address, no scheme, port, path or spaces.</summary>
    public static bool IsValidHost(string? host)
    {
        var text = host?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 253)
        {
            return false;
        }

        if (System.Net.IPAddress.TryParse(text, out _))
        {
            return true;
        }

        return Uri.CheckHostName(text) == UriHostNameType.Dns && !text.Contains('/') && !text.Contains('@') && !text.Contains(':');
    }
}
