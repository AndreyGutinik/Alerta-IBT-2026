using System.Collections.Frozen;
using System.Globalization;
using System.Text.RegularExpressions;
using AlertaIBT.Core.Alerts;
using Microsoft.Extensions.Logging;

namespace AlertaIBT.Core.Correction;

/// <summary>
/// Corrects the text of an alert: Gemini first (when allowed), the offline corrector when Gemini cannot answer or is
/// not trusted, and the user's fixed rules last. The plate is never touched, and Gemini is never allowed to invent
/// words.
/// </summary>
/// <remarks>
/// <para>
/// The safety lock accepts Gemini's answer only when its plate is the plate found in the original text (both absent
/// counts) and every word it brings is a word of the original, a close spelling of one (see the edit distance rules in
/// <see cref="IsFaithful"/>) or a function word such as <c>de</c> or <c>em</c>. Otherwise the offline result is used.
/// </para>
/// <para>
/// A Gemini answer that passed the lock (or a refusal) is remembered for two minutes by the exact original text, at
/// most 50 of them, so the preview on the PC and the submit show the same thing and the free quota is spared.
/// </para>
/// <para>This class is thread-safe: Telegram, the site and the PC preview call it at the same time.</para>
/// </remarks>
public sealed partial class CorrectionPipeline
{
    /// <summary>
    /// How long Gemini may take in the app before the offline corrector takes over: 5 seconds (3 was too short for the first
    /// call after a quiet spell). <see cref="GeminiHttp.RequestTimeout"/> stays above it.
    /// </summary>
    public static readonly TimeSpan DefaultGeminiTimeout = TimeSpan.FromSeconds(5);

    private const int CacheCapacity = 50;

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(2);

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    // Folded (lower case, no accents): a, a-grave, ao, aos, as, as-grave, o, os, e, de, da, das, do, dos, em, na, nas,
    // no, nos, um, uma, para, pra, com, por.
    private static readonly FrozenSet<string> FunctionWords = new[]
    {
        "a", "ao", "aos", "as", "o", "os", "e", "de", "da", "das", "do", "dos", "em", "na", "nas", "no", "nos", "um",
        "uma", "para", "pra", "com", "por",
    }.ToFrozenSet(StringComparer.Ordinal);

    // Words that flip the meaning when they appear out of nowhere: they must be in the original, spelled the same.
    private static readonly FrozenSet<string> Negations = new[] { "nao", "nunca", "sem", "nem" }
        .ToFrozenSet(StringComparer.Ordinal);

    private readonly ICorrector _gemini;
    private readonly ICorrector _offline;
    private readonly Func<FixedRules> _rules;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _clock;
    private readonly GeminiLog _log;
    private readonly Func<string>? _modelName;

    private readonly Lock _cacheGate = new();
    private readonly OrderedDictionary<string, CacheEntry> _cache = new();

    private readonly Lock _healthGate = new();
    private bool? _health;

    /// <param name="gemini">The online corrector.</param>
    /// <param name="offline">The offline corrector, used when Gemini cannot answer or is not trusted.</param>
    /// <param name="rules">Gives the current fixed rules; read once per run.</param>
    /// <param name="timeout">How long Gemini may take (<see cref="DefaultGeminiTimeout"/> in the app) before the offline corrector takes over.</param>
    /// <param name="clock">Time source of the cache; the system clock when omitted.</param>
    /// <param name="logger">
    /// Receives one line when Gemini times out, throws, or its answer is turned down by the safety lock (the reason class
    /// and the model, never the text); the same reason at most once a minute. Nothing is logged when omitted.
    /// </param>
    /// <param name="model">Gives the Gemini model id for those lines; blank or omitted means <see cref="GeminiPrompt.DefaultModel"/>.</param>
    public CorrectionPipeline(
        ICorrector gemini,
        ICorrector offline,
        Func<FixedRules> rules,
        TimeSpan timeout,
        TimeProvider? clock = null,
        ILogger? logger = null,
        Func<string>? model = null)
    {
        ArgumentNullException.ThrowIfNull(gemini);
        ArgumentNullException.ThrowIfNull(offline);
        ArgumentNullException.ThrowIfNull(rules);
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be positive.");
        }

        _gemini = gemini;
        _offline = offline;
        _rules = rules;
        _timeout = timeout;
        _clock = clock ?? TimeProvider.System;
        _log = new GeminiLog(logger, _clock);
        _modelName = model;
    }

    /// <summary>
    /// Raised when Gemini goes online (<c>true</c>) or offline (<c>false</c>). The state starts unknown, so the first
    /// call to Gemini raises it either way. A rejection by the safety lock or a refusal is not a failure; a cache hit
    /// or a run without Gemini does not touch the state. Handlers run on the calling thread, one at a time; they
    /// should return quickly and not call back into the pipeline from another thread.
    /// </summary>
    public event Action<bool>? GeminiHealthChanged;

    /// <summary>
    /// Corrects <paramref name="raw"/>.
    /// </summary>
    /// <param name="raw">What the person typed.</param>
    /// <param name="useGemini">Whether Gemini may be asked.</param>
    /// <param name="ct">
    /// Cancels the run: <see cref="OperationCanceledException"/> is thrown and the offline corrector is not used.
    /// </param>
    public async Task<CorrectionResult> RunAsync(string raw, bool useGemini, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(raw))
        {
            return new CorrectionResult(AlertParser.Parse(raw), CorrectorKind.Offline, Changed: false, Refused: false);
        }

        var fixedRules = _rules();

        ParsedAlert alert;
        var by = CorrectorKind.Offline;
        if (useGemini && await AskGeminiAsync(raw, ct).ConfigureAwait(false) is { } answer)
        {
            if (answer.Refused)
            {
                return new CorrectionResult(answer.Alert, CorrectorKind.Gemini, Changed: false, Refused: true);
            }

            alert = answer.Alert;
            by = CorrectorKind.Gemini;
        }
        else
        {
            alert = await RunOfflineAsync(raw, ct).ConfigureAwait(false);
        }

        var final = alert with
        {
            Model = fixedRules.Apply(alert.Model),
            Color = fixedRules.Apply(alert.Color),
            Reason = fixedRules.Apply(alert.Reason),
            FreeText = fixedRules.Apply(alert.FreeText),
        };

        return new CorrectionResult(final, by, WordsDiffer(raw, final.FreeText), Refused: false);
    }

    // ---- Gemini ----

    // Null when Gemini did not give an answer to use (failed, or the safety lock refused it).
    private async Task<GeminiAnswer?> AskGeminiAsync(string raw, CancellationToken ct)
    {
        if (CacheLookup(raw) is { } cached)
        {
            return new GeminiAnswer(cached, IsRefusal(cached));
        }

        var answer = await CallGeminiAsync(raw, ct).ConfigureAwait(false);
        ReportHealth(answer is not null);
        if (answer is null)
        {
            return null;
        }

        // The refusal is looked at before the lock: the offensive text must never reach the offline path.
        if (IsRefusal(answer))
        {
            CacheStore(raw, answer);
            return new GeminiAnswer(answer, Refused: true);
        }

        if (Vet(raw, answer, out var why) is not { } trusted)
        {
            _log.Failure("lock-" + why, $"trava de seguranca recusou a resposta ({why})", ModelId());
            return null;
        }

        CacheStore(raw, trusted);
        return new GeminiAnswer(trusted, Refused: false);
    }

    // Gemini's alert, or null when it returned null, threw or took longer than the timeout.
    private async Task<ParsedAlert?> CallGeminiAsync(string raw, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(_timeout);
        try
        {
            Task<ParsedAlert?> call;
            try
            {
                call = _gemini.CorrectAsync(raw, linked.Token);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                LogThrown(ex);
                return null;
            }

            // A corrector that ignores its token must not hold the alert past the timeout.
            var expired = Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            if (await Task.WhenAny(call, expired).ConfigureAwait(false) != call)
            {
                _ = call.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                ct.ThrowIfCancellationRequested();
                _log.Failure("timeout", "tempo esgotado", ModelId());
                return null;
            }

            return await call.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogThrown(ex);
            return null;
        }
        finally
        {
            // Whatever is still running on the token (after a timeout) is told to stop.
            await linked.CancelAsync().ConfigureAwait(false);
        }
    }

    private string ModelId() =>
        _modelName?.Invoke() is { } name && !string.IsNullOrWhiteSpace(name) ? name.Trim() : GeminiPrompt.DefaultModel;

    // A corrector that throws instead of returning null (its own timeout is a corrector's business): the type, not the text.
    private void LogThrown(Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            _log.Failure("timeout", "tempo esgotado", ModelId());
        }
        else
        {
            _log.Failure("error-" + ex.GetType().Name, $"erro inesperado ({ex.GetType().Name})", ModelId());
        }
    }

    private static bool IsRefusal(ParsedAlert alert) =>
        string.IsNullOrWhiteSpace(alert.FreeText)
        && string.IsNullOrWhiteSpace(alert.Model)
        && string.IsNullOrWhiteSpace(alert.Color)
        && string.IsNullOrWhiteSpace(alert.Reason)
        && string.IsNullOrWhiteSpace(alert.Plate);

    private void ReportHealth(bool online)
    {
        lock (_healthGate)
        {
            if (_health == online)
            {
                return;
            }

            _health = online;
            GeminiHealthChanged?.Invoke(online);
        }
    }

    // ---- offline ----

    private async Task<ParsedAlert> RunOfflineAsync(string raw, CancellationToken ct)
    {
        try
        {
            return await _offline.CorrectAsync(raw, ct).ConfigureAwait(false) ?? AlertParser.Parse(raw);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return AlertParser.Parse(raw);
        }
    }

    // ---- cache ----

    private ParsedAlert? CacheLookup(string raw)
    {
        lock (_cacheGate)
        {
            if (!_cache.TryGetValue(raw, out var entry))
            {
                return null;
            }

            if (_clock.GetUtcNow() - entry.StoredAt >= CacheLifetime)
            {
                _cache.Remove(raw);
                return null;
            }

            return entry.Alert;
        }
    }

    private void CacheStore(string raw, ParsedAlert alert)
    {
        lock (_cacheGate)
        {
            _cache.Remove(raw); // a fresh answer goes to the end, as the newest
            _cache.Add(raw, new CacheEntry(alert, _clock.GetUtcNow()));
            while (_cache.Count > CacheCapacity)
            {
                _cache.RemoveAt(0);
            }
        }
    }

    private readonly record struct CacheEntry(ParsedAlert Alert, DateTimeOffset StoredAt);

    private readonly record struct GeminiAnswer(ParsedAlert Alert, bool Refused);

    // ---- safety lock ----

    // Gemini's alert with its plate in display form when it can be trusted, null when it cannot.
    // `why` is the class of the rejection (never the words themselves): it goes to the log.
    private static ParsedAlert? Vet(string raw, ParsedAlert ai, out string why)
    {
        why = string.Empty;
        var rawText = AlertFormatter.RemovePictographs(raw);
        var expected = PlateParser.TryExtract(rawText, out var rawPlate, out var rawRest) ? rawPlate : null;
        var plate = string.IsNullOrWhiteSpace(ai.Plate) ? null : PlateParser.Normalize(ai.Plate);
        if (plate != expected)
        {
            // A plate typed with a space ("abc 1234") is not found in the raw text; Gemini's reading of it is trusted
            // only when its seven characters really are in the text, one after the other.
            if (expected is not null || plate is null || !HasPlateContiguously(rawText, plate))
            {
                why = "placa diferente";
                return null;
            }

            expected = plate;
        }

        var rawWords = FoldedWords(rawRest).ToHashSet(StringComparer.Ordinal);
        var vehicleAnswer = plate is not null || !string.IsNullOrWhiteSpace(ai.Model);
        if (!ColorLock.Allows(ColorLock.ColorsIn(rawWords), ai.Color ?? string.Empty, vehicleAnswer))
        {
            why = "cor diferente";
            return null;
        }

        foreach (var field in new[] { ai.Model, ai.Color, ai.Reason, ai.FreeText })
        {
            if (string.IsNullOrWhiteSpace(field))
            {
                continue;
            }

            // A plate written inside any field must be the plate of the original, too.
            var found = PlateParser.TryExtract(AlertFormatter.RemovePictographs(field), out var fieldPlate, out var fieldRest);
            if (found && fieldPlate != expected)
            {
                why = "placa diferente";
                return null;
            }

            if (!FoldedWords(fieldRest).All(word => IsFaithful(word, rawWords)))
            {
                why = "palavra nova";
                return null;
            }
        }

        var freeText = string.IsNullOrWhiteSpace(ai.FreeText)
            ? string.Join(' ', new[] { ai.Model, ai.Color, plate, ai.Reason }.Where(part => !string.IsNullOrWhiteSpace(part)))
            : ai.FreeText;
        return ai with { Plate = plate, FreeText = freeText };
    }

    // Whether the text, without its white space and hyphens, has the plate's seven characters in a row (as
    // PlateParser.Normalize reads them: 0 1 5 8 stand for O I S B in the letter slots and the other way round). The seven
    // characters must start where a word of the text starts: "aceso 1234" does not hide the plate "ESO-1234".
    private static bool HasPlateContiguously(string text, string plate)
    {
        var compact = new System.Text.StringBuilder(text.Length);
        var wordStarts = new HashSet<int>();
        var atWordStart = true;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                atWordStart = true;
                continue;
            }

            if (c == '-')
            {
                continue;
            }

            if (atWordStart)
            {
                wordStarts.Add(compact.Length);
                atWordStart = false;
            }

            compact.Append(c);
        }

        var characters = compact.ToString();
        foreach (var start in wordStarts)
        {
            if (start + 7 <= characters.Length && PlateParser.Normalize(characters.Substring(start, 7)) == plate)
            {
                return true;
            }
        }

        return false;
    }

    // A word of Gemini's is faithful when the original already has it, when it is a function word, or when it is a
    // close spelling of a word of the original: up to 2 edits, up to 1 when either word has 3 letters or fewer
    // ("fox" is not a typo of "gol"). Words with digits ("hb20") and negations must match exactly.
    private static bool IsFaithful(string word, HashSet<string> rawWords)
    {
        if (FunctionWords.Contains(word) || rawWords.Contains(word))
        {
            return true;
        }

        if (Negations.Contains(word) || word.Any(char.IsDigit))
        {
            return false;
        }

        foreach (var original in rawWords)
        {
            if (original.Any(char.IsDigit))
            {
                continue;
            }

            var allowed = Math.Min(word.Length, original.Length) <= 3 ? 1 : 2;
            if (Math.Abs(word.Length - original.Length) <= allowed
                && EditDistance.Levenshtein(word, original) <= allowed)
            {
                return true;
            }
        }

        return false;
    }

    // ---- words ----

    // The words of a text: split at white space, | / ; , (but not a comma between digits) and a hyphen between
    // letters, without what is not a letter or a digit at their ends.
    [GeneratedRegex(@"[\s|/;]+|(?<!\p{Nd}),|,(?!\p{Nd})|(?<=\p{L})-(?=\p{L})")]
    private static partial Regex WordSeparator();

    private static IEnumerable<string> Tokens(string text)
    {
        foreach (var token in WordSeparator().Split(text))
        {
            var word = Vocabulary.TrimEdges(token);
            if (word.Length > 0)
            {
                yield return word;
            }
        }
    }

    // Lower case and no accents; the label "placa" is not a word of the message.
    private static IEnumerable<string> FoldedWords(string text) =>
        Tokens(text).Select(Vocabulary.Fold).Where(word => word.Length > 0 && word != "placa");

    // Lower case with the accents kept.
    private static IEnumerable<string> ExactWords(string text) =>
        Tokens(text)
            .Select(word => Vocabulary.TrimEdges(word.Normalize(System.Text.NormalizationForm.FormC).ToLower(PtBr)))
            .Where(word => word.Length > 0 && word != "placa");

    private static bool WordsDiffer(string raw, string text) =>
        !ExactWords(WithoutPlate(raw)).Order(StringComparer.Ordinal)
            .SequenceEqual(ExactWords(WithoutPlate(text)).Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static string WithoutPlate(string text)
    {
        PlateParser.TryExtract(AlertFormatter.RemovePictographs(text), out _, out var rest);
        return rest;
    }
}
