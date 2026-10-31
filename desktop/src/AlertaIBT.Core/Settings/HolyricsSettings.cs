namespace AlertaIBT.Core.Settings;

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
