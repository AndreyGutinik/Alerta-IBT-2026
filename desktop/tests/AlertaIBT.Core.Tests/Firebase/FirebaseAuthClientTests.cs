using System.Net;
using AlertaIBT.Core.Firebase;
using AlertaIBT.Core.Tests.Services;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Firebase;

public sealed class FirebaseAuthClientTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero));
    private readonly CapturingLogger _log = new();
    private readonly FakeFirebase _fake;
    private readonly FirebaseAuthClient _auth;

    public FirebaseAuthClientTests()
    {
        _fake = new FakeFirebase(_clock);
        _auth = new FirebaseAuthClient(new HttpClient(_fake), FakeFirebase.ApiKey, clock: _clock, logger: _log);
    }

    [Fact]
    public async Task Sign_in_gives_the_tokens_and_the_uid()
    {
        var session = await _auth.SignInAsync(FakeFirebase.Email, FakeFirebase.Password, Ct);

        Assert.StartsWith("tok-", session.IdToken, StringComparison.Ordinal);
        Assert.Equal(FakeFirebase.PcUid, session.Uid);
        Assert.Equal(FakeFirebase.PcUid, _auth.Uid);
        Assert.Equal(_clock.GetUtcNow().AddHours(1), session.ExpiresAt);
    }

    [Theory]
    [InlineData("INVALID_LOGIN_CREDENTIALS", AuthFailure.InvalidCredentials)]
    [InlineData("EMAIL_NOT_FOUND", AuthFailure.InvalidCredentials)]
    [InlineData("INVALID_PASSWORD", AuthFailure.InvalidCredentials)]
    [InlineData("USER_DISABLED", AuthFailure.Disabled)]
    [InlineData("TOO_MANY_ATTEMPTS_TRY_LATER", AuthFailure.RateLimited)]
    [InlineData("API_KEY_INVALID", AuthFailure.Configuration)]
    [InlineData("OPERATION_NOT_ALLOWED", AuthFailure.Configuration)]
    [InlineData("SOMETHING_NEW", AuthFailure.Other)]
    public async Task A_failed_sign_in_is_classified(string code, AuthFailure expected)
    {
        _fake.SignInFailure = (HttpStatusCode.BadRequest, code);

        var error = await Assert.ThrowsAsync<FirebaseAuthException>(() => _auth.SignInAsync(FakeFirebase.Email, FakeFirebase.Password, Ct));

        Assert.Equal(expected, error.Failure);
        Assert.Equal(expected is AuthFailure.RateLimited or AuthFailure.Other, error.IsTransient);
    }

    [Fact]
    public async Task Rate_limiting_by_status_is_rate_limited()
    {
        _fake.SignInFailure = (HttpStatusCode.TooManyRequests, "whatever");

        var error = await Assert.ThrowsAsync<FirebaseAuthException>(() => _auth.SignInAsync(FakeFirebase.Email, FakeFirebase.Password, Ct));

        Assert.Equal(AuthFailure.RateLimited, error.Failure);
    }

    [Fact]
    public async Task A_server_error_is_a_network_failure()
    {
        _fake.SignInFailure = (HttpStatusCode.ServiceUnavailable, "BACKEND_ERROR");

        var error = await Assert.ThrowsAsync<FirebaseAuthException>(() => _auth.SignInAsync(FakeFirebase.Email, FakeFirebase.Password, Ct));

        Assert.Equal(AuthFailure.Network, error.Failure);
        Assert.True(error.IsTransient);
    }

    [Fact]
    public async Task No_network_is_a_network_failure_and_the_message_hides_the_address()
    {
        _fake.NetworkDown = true;

        var error = await Assert.ThrowsAsync<FirebaseAuthException>(() => _auth.SignInAsync(FakeFirebase.Email, FakeFirebase.Password, Ct));

        Assert.Equal(AuthFailure.Network, error.Failure);
        Assert.DoesNotContain(FakeFirebase.ApiKey, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_valid_token_is_reused_until_five_minutes_before_it_expires()
    {
        await _auth.SignInAsync(FakeFirebase.Email, FakeFirebase.Password, Ct);
        var first = await _auth.GetTokenAsync(false, Ct);

        _clock.Advance(TimeSpan.FromMinutes(54));
        var later = await _auth.GetTokenAsync(false, Ct);

        Assert.Equal(first, later);
        Assert.Equal(0, _fake.Refreshes);
        Assert.Equal(TimeSpan.FromMinutes(1), _auth.TimeUntilRefresh);
    }

    [Fact]
    public async Task The_token_is_renewed_when_it_is_inside_the_five_minute_margin()
    {
        await _auth.SignInAsync(FakeFirebase.Email, FakeFirebase.Password, Ct);
        var first = await _auth.GetTokenAsync(false, Ct);

        _clock.Advance(TimeSpan.FromMinutes(55) + TimeSpan.FromSeconds(1));
        var renewed = await _auth.GetTokenAsync(false, Ct);

        Assert.NotEqual(first, renewed);
        Assert.Equal(1, _fake.Refreshes);
        Assert.Equal(_clock.GetUtcNow().AddHours(1), _auth.Session!.ExpiresAt);
        Assert.Equal(FakeFirebase.PcUid, _auth.Uid);
    }

    [Fact]
    public async Task A_forced_refresh_gets_a_new_token_at_once()
    {
        await _auth.SignInAsync(FakeFirebase.Email, FakeFirebase.Password, Ct);
        var first = await _auth.GetTokenAsync(false, Ct);

        var second = await _auth.GetTokenAsync(true, Ct);

        Assert.NotEqual(first, second);
        Assert.Equal(1, _fake.Refreshes);
    }

    [Fact]
    public async Task When_the_refresh_token_is_refused_the_password_is_used_again()
    {
        await _auth.SignInAsync(FakeFirebase.Email, FakeFirebase.Password, Ct);
        _fake.RefreshFailure = (HttpStatusCode.BadRequest, "TOKEN_EXPIRED");

        var token = await _auth.GetTokenAsync(true, Ct);

        Assert.StartsWith("tok-", token, StringComparison.Ordinal);
        Assert.Equal(2, _fake.SignIns);
    }

    [Fact]
    public async Task A_network_failure_during_refresh_is_thrown_and_the_old_session_stays()
    {
        await _auth.SignInAsync(FakeFirebase.Email, FakeFirebase.Password, Ct);
        _clock.Advance(TimeSpan.FromMinutes(58));
        _fake.NetworkDown = true;

        var error = await Assert.ThrowsAsync<FirebaseAuthException>(() => _auth.GetTokenAsync(false, Ct));

        Assert.Equal(AuthFailure.Network, error.Failure);
        Assert.NotNull(_auth.Session);
        Assert.Equal(TimeSpan.Zero, _auth.TimeUntilRefresh);
    }

    [Fact]
    public async Task Asking_for_a_token_before_signing_in_fails_cleanly()
    {
        var error = await Assert.ThrowsAsync<FirebaseAuthException>(() => _auth.GetTokenAsync(false, Ct));

        Assert.Equal(AuthFailure.Other, error.Failure);
    }

    [Fact]
    public async Task Secrets_never_reach_the_log()
    {
        await _auth.SignInAsync(FakeFirebase.Email, FakeFirebase.Password, Ct);
        var token = await _auth.GetTokenAsync(false, Ct);
        _clock.Advance(TimeSpan.FromMinutes(56));
        var renewed = await _auth.GetTokenAsync(false, Ct);
        _fake.SignInFailure = (HttpStatusCode.BadRequest, "INVALID_LOGIN_CREDENTIALS");
        _ = await Assert.ThrowsAsync<FirebaseAuthException>(() => _auth.SignInAsync(FakeFirebase.Email, "another", Ct));

        var text = string.Join('\n', _log.Entries.Select(e => e.Message));
        Assert.NotEmpty(_log.Entries);
        foreach (var secret in new[] { FakeFirebase.Password, FakeFirebase.ApiKey, token, renewed, "another", "refresh-" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_emulator_endpoints_are_the_local_ones()
    {
        var endpoints = FirebaseAuthEndpoints.Emulator();

        Assert.Equal("http://127.0.0.1:9099/identitytoolkit.googleapis.com", endpoints.IdentityBaseUrl);
        Assert.Equal("http://127.0.0.1:9099/securetoken.googleapis.com", endpoints.SecureTokenBaseUrl);
    }

    [Fact]
    public void A_loopback_database_uses_the_emulator_auth_and_a_real_one_uses_google()
    {
        var emulator = new SiteConfig("http://127.0.0.1:9000?ns=demo-alertaibt", "k", "e", "p").ResolveEndpoints();
        var real = new SiteConfig("https://x-default-rtdb.firebaseio.com", "k", "e", "p").ResolveEndpoints();

        Assert.Equal("http://127.0.0.1:9099/identitytoolkit.googleapis.com", emulator.IdentityBaseUrl);
        Assert.Equal("https://identitytoolkit.googleapis.com", real.IdentityBaseUrl);
        Assert.Equal("https://securetoken.googleapis.com", real.SecureTokenBaseUrl);
    }

    [Fact]
    public void The_config_never_prints_the_password()
    {
        var config = new SiteConfig("https://x.firebaseio.com", "key", "pc@x.com", "SuperSecret1");

        Assert.DoesNotContain("SuperSecret1", config.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("SuperSecret1", $"{config}", StringComparison.Ordinal);
    }
}
