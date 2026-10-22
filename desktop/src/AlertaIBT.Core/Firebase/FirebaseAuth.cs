using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlertaIBT.Core.Firebase;

/// <summary>Why a sign-in or a token refresh failed.</summary>
public enum AuthFailure
{
    /// <summary>The e-mail or the password is wrong, or the account does not exist.</summary>
    InvalidCredentials,

    /// <summary>The account was disabled in the console.</summary>
    Disabled,

    /// <summary>The API key is wrong, or e-mail/password sign-in is not enabled for the project.</summary>
    Configuration,

    /// <summary>Too many attempts: Firebase asks to wait.</summary>
    RateLimited,

    /// <summary>The server could not be reached, or answered with an error of its own.</summary>
    Network,

    /// <summary>Anything else.</summary>
    Other,
}

/// <summary>A sign-in or a token refresh failed. The message never holds a password, token or API key.</summary>
public sealed class FirebaseAuthException : Exception
{
    /// <param name="failure">Why it failed.</param>
    /// <param name="message">What to tell the user (pt-BR).</param>
    /// <param name="inner">The cause, when there is one.</param>
    public FirebaseAuthException(AuthFailure failure, string message, Exception? inner = null)
        : base(message, inner)
    {
        Failure = failure;
    }

    /// <summary>Why it failed.</summary>
    public AuthFailure Failure { get; }

    /// <summary>Whether trying again later without any change can help (network, rate limit); false for a wrong password or key.</summary>
    public bool IsTransient => Failure is AuthFailure.Network or AuthFailure.RateLimited or AuthFailure.Other;
}

/// <summary>The tokens of a signed-in account.</summary>
/// <param name="IdToken">The token that goes to the database (valid one hour).</param>
/// <param name="RefreshToken">The token that makes new ID tokens.</param>
/// <param name="Uid">The account's uid.</param>
/// <param name="ExpiresAt">When the ID token stops being accepted.</param>
public sealed record AuthSession(string IdToken, string RefreshToken, string Uid, DateTimeOffset ExpiresAt);

/// <summary>Gives the database client a valid ID token.</summary>
public interface ITokenSource
{
    /// <summary>The uid of the signed-in account, or <c>null</c> before the first sign-in.</summary>
    string? Uid { get; }

    /// <summary>How long until the current token should be renewed (never negative); an hour when there is none.</summary>
    TimeSpan TimeUntilRefresh { get; }

    /// <summary>A token that is valid now; <paramref name="forceRefresh"/> asks for a new one even if the current one has not expired (after a 401).</summary>
    Task<string> GetTokenAsync(bool forceRefresh, CancellationToken ct);
}

/// <summary>Where the two Firebase Auth REST services live; the defaults are the real ones, a test or the emulator passes its own.</summary>
/// <param name="IdentityBaseUrl">The Identity Toolkit root (<c>https://identitytoolkit.googleapis.com</c>; for the emulator <c>http://127.0.0.1:9099/identitytoolkit.googleapis.com</c>).</param>
/// <param name="SecureTokenBaseUrl">The Secure Token root (<c>https://securetoken.googleapis.com</c>; for the emulator <c>http://127.0.0.1:9099/securetoken.googleapis.com</c>).</param>
public sealed record FirebaseAuthEndpoints(
    string IdentityBaseUrl = "https://identitytoolkit.googleapis.com",
    string SecureTokenBaseUrl = "https://securetoken.googleapis.com")
{
    /// <summary>The endpoints of the Auth emulator on <paramref name="host"/>:<paramref name="port"/>.</summary>
    public static FirebaseAuthEndpoints Emulator(string host = "127.0.0.1", int port = 9099) =>
        new($"http://{host}:{port}/identitytoolkit.googleapis.com", $"http://{host}:{port}/securetoken.googleapis.com");
}

/// <summary>
/// Signs the PC's own account in through the Identity Toolkit REST API and keeps its ID token fresh through the Secure Token
/// service, renewing it about <see cref="RefreshMargin"/> before it expires. No SDK. Nothing secret (password, tokens, API key)
/// is ever logged. One call at a time.
/// </summary>
public sealed class FirebaseAuthClient : ITokenSource, IDisposable
{
    /// <summary>The ID token is renewed this long before it expires.</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly FirebaseAuthEndpoints _endpoints;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _email;
    private string? _password;
    private volatile AuthSession? _session;

    /// <param name="http">The HTTP client (its timeout is not used: each call has its own limit).</param>
    /// <param name="apiKey">The web API key of the Firebase project.</param>
    /// <param name="endpoints">Where the services are; the real ones by default.</param>
    /// <param name="clock">The source of time.</param>
    /// <param name="logger">Receives what happened, without secrets.</param>
    public FirebaseAuthClient(HttpClient http, string apiKey, FirebaseAuthEndpoints? endpoints = null, TimeProvider? clock = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        _http = http;
        _apiKey = apiKey;
        _endpoints = endpoints ?? new FirebaseAuthEndpoints();
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The current session, or <c>null</c> before the first sign-in.</summary>
    public AuthSession? Session => _session;

    /// <inheritdoc />
    public string? Uid => _session?.Uid;

    /// <inheritdoc />
    public TimeSpan TimeUntilRefresh
    {
        get
        {
            var session = _session;
            if (session is null)
            {
                return TimeSpan.FromHours(1);
            }

            var left = session.ExpiresAt - RefreshMargin - _clock.GetUtcNow();
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }
    }

    /// <summary>Signs in with the e-mail and the password, and remembers them (in memory only) to sign in again if the refresh token stops working.</summary>
    /// <exception cref="FirebaseAuthException">The sign-in failed.</exception>
    public async Task<AuthSession> SignInAsync(string email, string password, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrEmpty(password);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _email = email;
            _password = password;
            return await SignInLockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string> GetTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var session = _session;
            if (session is not null && !forceRefresh && _clock.GetUtcNow() < session.ExpiresAt - RefreshMargin)
            {
                return session.IdToken;
            }

            if (session is null)
            {
                if (_email is null)
                {
                    throw new FirebaseAuthException(AuthFailure.Other, "Ainda n\U000000E3o entrou no Firebase.");
                }

                return (await SignInLockedAsync(ct).ConfigureAwait(false)).IdToken;
            }

            try
            {
                return (await RefreshLockedAsync(session, ct).ConfigureAwait(false)).IdToken;
            }
            catch (FirebaseAuthException ex) when (!ex.IsTransient && _email is not null)
            {
                // The refresh token was revoked or the account was changed: the password is the way back in.
                _logger.LogInformation("Token de renovacao recusado ({Failure}); entrando de novo com a senha", ex.Failure);
                return (await SignInLockedAsync(ct).ConfigureAwait(false)).IdToken;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private async Task<AuthSession> SignInLockedAsync(CancellationToken ct)
    {
        var body = new JsonObject { ["email"] = _email, ["password"] = _password, ["returnSecureToken"] = true };
        var uri = $"{_endpoints.IdentityBaseUrl.TrimEnd('/')}/v1/accounts:signInWithPassword?key={Uri.EscapeDataString(_apiKey)}";
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        var json = await PostAsync(uri, content, ct).ConfigureAwait(false);
        var session = ReadSession(json, "idToken", "refreshToken", "localId", "expiresIn", null);
        _session = session;
        _logger.LogInformation("Entrou no Firebase como conta do PC");
        return session;
    }

    private async Task<AuthSession> RefreshLockedAsync(AuthSession old, CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
            new KeyValuePair<string, string>("refresh_token", old.RefreshToken),
        ]);
        var uri = $"{_endpoints.SecureTokenBaseUrl.TrimEnd('/')}/v1/token?key={Uri.EscapeDataString(_apiKey)}";
        var json = await PostAsync(uri, form, ct).ConfigureAwait(false);
        var session = ReadSession(json, "id_token", "refresh_token", "user_id", "expires_in", old);
        _session = session;
        _logger.LogInformation("Token do Firebase renovado");
        return session;
    }

    private AuthSession ReadSession(JsonObject json, string idKey, string refreshKey, string uidKey, string expiresKey, AuthSession? old)
    {
        var idToken = json[idKey]?.GetValue<string>();
        var refresh = json[refreshKey]?.GetValue<string>() ?? old?.RefreshToken;
        var uid = json[uidKey]?.GetValue<string>() ?? old?.Uid;
        if (string.IsNullOrEmpty(idToken) || string.IsNullOrEmpty(refresh) || string.IsNullOrEmpty(uid))
        {
            throw new FirebaseAuthException(AuthFailure.Other, "Resposta inesperada do login do Firebase.");
        }

        var seconds = 3600d;
        if (json[expiresKey] is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                seconds = parsed;
            }
            else if (value.TryGetValue<double>(out var number))
            {
                seconds = number;
            }
        }

        return new AuthSession(idToken, refresh, uid, _clock.GetUtcNow().AddSeconds(Math.Clamp(seconds, 60, 86400)));
    }

    private async Task<JsonObject> PostAsync(string uri, HttpContent content, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response = await _http.PostAsync(uri, content, timeout.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return JsonNode.Parse(text) as JsonObject
                    ?? throw new FirebaseAuthException(AuthFailure.Other, "Resposta inesperada do login do Firebase.");
            }

            throw Classify(response.StatusCode, text);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new FirebaseAuthException(AuthFailure.Network, "O login do Firebase demorou demais.");
        }
        catch (HttpRequestException ex)
        {
            // The message of the exception is not used: it could carry the address, which holds the API key.
            throw new FirebaseAuthException(AuthFailure.Network, "Sem conex\U000000E3o com o Firebase.", ex);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new FirebaseAuthException(AuthFailure.Network, "Resposta inesperada do login do Firebase.", ex);
        }
    }

    // The error code is the first word of the Google error message ("TOO_MANY_ATTEMPTS_TRY_LATER : Access to this account...").
    private static FirebaseAuthException Classify(HttpStatusCode status, string body)
    {
        var code = string.Empty;
        try
        {
            code = (JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? string.Empty).Split(' ', ':')[0].Trim();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Not Google's error format: the status decides.
        }

        if (code.StartsWith("TOO_MANY_ATTEMPTS", StringComparison.Ordinal) || status == HttpStatusCode.TooManyRequests)
        {
            return new(AuthFailure.RateLimited, "Muitas tentativas de login; esperando para tentar de novo.");
        }

        return code switch
        {
            "INVALID_LOGIN_CREDENTIALS" or "EMAIL_NOT_FOUND" or "INVALID_PASSWORD" or "INVALID_EMAIL" or "MISSING_PASSWORD"
                or "MISSING_EMAIL" or "INVALID_REFRESH_TOKEN" or "TOKEN_EXPIRED" or "USER_NOT_FOUND" or "INVALID_GRANT_TYPE"
                or "MISSING_REFRESH_TOKEN" =>
                new(AuthFailure.InvalidCredentials, "E-mail ou senha da conta do PC n\U000000E3o servem."),
            "USER_DISABLED" => new(AuthFailure.Disabled, "A conta do PC foi desativada no Firebase."),
            "INVALID_API_KEY" or "API_KEY_INVALID" or "OPERATION_NOT_ALLOWED" or "PROJECT_NOT_FOUND" or "API_KEY_HTTP_REFERRER_BLOCKED"
                or "CONFIGURATION_NOT_FOUND" =>
                new(AuthFailure.Configuration, "Chave da API errada ou login por e-mail e senha desligado no Firebase."),
            _ when (int)status >= 500 => new(AuthFailure.Network, "O Firebase est\U000000E1 com problema; tentando de novo."),
            _ when status == HttpStatusCode.BadRequest => new(AuthFailure.Other, "O Firebase recusou o login (" + (code.Length > 0 ? code : "400") + ")."),
            _ => new(AuthFailure.Other, "O Firebase respondeu " + (int)status + "."),
        };
    }
}
