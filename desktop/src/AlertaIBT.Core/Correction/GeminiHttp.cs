namespace AlertaIBT.Core.Correction;

/// <summary>
/// The HTTP client of <see cref="GeminiCorrector"/>. The app keeps one for its whole life, and its connections are kept
/// warm on purpose: the first call after a quiet spell used to pay a new TLS handshake and ran into the time limit, and
/// the free quota forbids "warm-up" requests, so the pool does the job instead.
/// </summary>
public static class GeminiHttp
{
    /// <summary>
    /// The timeout of the client itself: longer than <see cref="CorrectionPipeline.DefaultGeminiTimeout"/>, so the pipeline
    /// is the one that gives up first and the offline corrector takes over.
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A pooled connection is replaced after this long, so a changed address is noticed.</summary>
    public static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(5);

    /// <summary>An idle connection is closed after this long: between messages in a busy hour it stays open.</summary>
    public static readonly TimeSpan ConnectionIdleTimeout = TimeSpan.FromMinutes(2);

    /// <summary>The handler with the lifetimes above.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        PooledConnectionLifetime = ConnectionLifetime,
        PooledConnectionIdleTimeout = ConnectionIdleTimeout,
    };

    /// <summary>The long-lived client for <see cref="GeminiCorrector"/>; the caller owns it.</summary>
    public static HttpClient CreateClient() => new(CreateHandler(), disposeHandler: true) { Timeout = RequestTimeout };
}
