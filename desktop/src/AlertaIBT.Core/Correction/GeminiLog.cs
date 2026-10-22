using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Correction;

/// <summary>
/// The lines Gemini leaves in the log (Configuracoes &gt; Diagnostico). One concise line per problem: why it failed and
/// the model, never the API key, the request address or what the person typed. The same reason is written at most once
/// per minute, so an internet that is down does not flood the file.
/// </summary>
internal sealed class GeminiLog
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private const int MaxKinds = 64;

    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _last = new(StringComparer.Ordinal);

    public GeminiLog(ILogger? logger, TimeProvider? clock)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>A failure (Warning): <c>Gemini falhou: {reason} (modelo {model})</c>.</summary>
    /// <param name="kind">What makes two failures "the same reason" for the throttle (for example <c>http-403</c>).</param>
    /// <param name="reason">The text after "Gemini falhou:", in pt-BR, free of secrets.</param>
    /// <param name="model">The model id that was asked.</param>
    public void Failure(string kind, string reason, string model) =>
        Write(LogLevel.Warning, kind, "Gemini falhou: {Reason} (modelo {Model})", reason, model);

    /// <summary>The health probe worked (Information): <c>Gemini: conectado (modelo {model}, {ms} ms)</c>.</summary>
    public void Connected(string model, long milliseconds) =>
        Write(LogLevel.Information, "connected", "Gemini: conectado (modelo {Model}, {Ms} ms)", model, milliseconds);

    /// <summary>Gemini answered but declined, or its answer was not used (Information): <c>Gemini: {what} (modelo {model})</c>.</summary>
    public void Note(string kind, string what, string model) =>
        Write(LogLevel.Information, kind, "Gemini: {What} (modelo {Model})", what, model);

    private void Write(LogLevel level, string kind, string template, params object[] args)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (_last.TryGetValue(kind, out var before) && now - before < Window)
            {
                return;
            }

            if (_last.Count >= MaxKinds)
            {
                foreach (var old in _last.Where(entry => now - entry.Value >= Window).Select(entry => entry.Key).ToList())
                {
                    _last.Remove(old);
                }
            }

            _last[kind] = now;
        }

        try
        {
            _logger.Log(level, template, args);
        }
        catch (Exception)
        {
            // Logging must never break a correction.
        }
    }

    /// <summary>One line of text, at most <paramref name="max"/> letters, with the secret (when known) hidden.</summary>
    public static string Clean(string text, string? secret, int max = 160)
    {
        if (!string.IsNullOrEmpty(secret))
        {
            text = text.Replace(secret, "***", StringComparison.Ordinal);
        }

        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= max ? line : line[..max] + "...";
    }
}
