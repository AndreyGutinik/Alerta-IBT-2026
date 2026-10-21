using AlertaIBT.Core.Alerts;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Correction;

/// <summary>
/// A corrector built the first time it is needed, off the caller's thread. The offline dictionary takes about a second to
/// load, which must neither delay the start of the app nor freeze the panel the first time someone types. <see cref="Warm"/>
/// starts the load in the background so the first real alert finds it ready.
/// </summary>
/// <remarks>
/// When building fails (a missing dictionary file) every call throws the same error, which <see cref="CorrectionPipeline"/>
/// turns into the plain parser (the alerts keep working, only without the spelling help). The failure is logged once, as
/// one error line, and handed to <c>onLoadFailed</c> (the app shows it in the status); <see cref="LoadError"/> keeps it.
/// </remarks>
public sealed class LazyCorrector : ICorrector
{
    private readonly Lazy<ICorrector> _inner;
    private readonly ILogger? _logger;
    private readonly string? _source;
    private readonly Action<Exception>? _onLoadFailed;
    private Exception? _loadError;

    /// <param name="factory">Builds the corrector; runs at most once.</param>
    /// <param name="logger">Receives the one error line when building fails.</param>
    /// <param name="source">Where the dictionary is expected (a folder), for that line.</param>
    /// <param name="onLoadFailed">Told once when building fails; it must not throw (if it does, the exception is dropped).</param>
    public LazyCorrector(
        Func<ICorrector> factory,
        ILogger? logger = null,
        string? source = null,
        Action<Exception>? onLoadFailed = null)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _inner = new Lazy<ICorrector>(factory, LazyThreadSafetyMode.ExecutionAndPublication);
        _logger = logger;
        _source = source;
        _onLoadFailed = onLoadFailed;
    }

    /// <summary>The exception that stopped the corrector from being built, or <c>null</c>.</summary>
    public Exception? LoadError => Volatile.Read(ref _loadError);

    /// <summary>Starts building the corrector in the background. Never throws.</summary>
    public void Warm() => _ = Task.Run(() =>
    {
        try
        {
            Get();
        }
        catch (Exception)
        {
            // Recorded in LoadError; the real calls fall back to the plain parser.
        }
    });

    /// <inheritdoc />
    public async Task<ParsedAlert?> CorrectAsync(string raw, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var inner = await Task.Run(Get, ct).ConfigureAwait(false);
        return await inner.CorrectAsync(raw, ct).ConfigureAwait(false);
    }

    private ICorrector Get()
    {
        try
        {
            return _inner.Value;
        }
        catch (Exception ex)
        {
            if (Interlocked.CompareExchange(ref _loadError, ex, null) is null)
            {
                ReportLoadFailure(ex);
            }

            throw;
        }
    }

    // Once: a line for the log (the path, the type and the message of the exception) and the status.
    private void ReportLoadFailure(Exception ex)
    {
        var where = ex is FileNotFoundException { FileName: { Length: > 0 } file } ? file : _source ?? "(pasta desconhecida)";
        try
        {
            _logger?.LogError(
                "Nao foi possivel carregar o dicionario offline ({Where}): {ExceptionType}: {ExceptionMessage}. Os avisos continuam, so com a leitura basica, sem corrigir a ortografia",
                where,
                ex.GetType().Name,
                ex.Message);
            _onLoadFailed?.Invoke(ex);
        }
        catch (Exception)
        {
            // Reporting must never break the fallback.
        }
    }
}
