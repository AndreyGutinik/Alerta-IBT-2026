namespace AlertaIBT.Core.Firebase;

/// <summary>Where the connection to the site (the Firebase project) stands; it drives the "Site" status dot.</summary>
public enum SiteState
{
    /// <summary>The site is off or not filled in.</summary>
    NotConfigured,

    /// <summary>Signing in and opening the first connection.</summary>
    Connecting,

    /// <summary>Signed in and receiving data: notices sent from the phones arrive.</summary>
    Online,

    /// <summary>The connection was lost a moment ago; trying again.</summary>
    Reconnecting,

    /// <summary>No connection for more than a minute (no internet, or the service is down); keeps trying.</summary>
    Offline,

    /// <summary>The Firebase refuses the PC account (wrong e-mail, password or API key, or the account is not allowed to read).</summary>
    AuthError,
}
