using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Namotion.Devices.Tesla.Client;
using Namotion.Devices.Tesla.Tests.Testing;

namespace Namotion.Devices.Tesla.Tests;

public class TeslaAuthenticationTests
{
    private const string TokenPath = "/oauth2/v3/token";

    private static readonly DateTimeOffset Start = new(2026, 11, 21, 20, 0, 0, TimeSpan.Zero);

    private static string TokenJson(string accessToken, string refreshToken, int expiresIn = 28800) =>
        $$"""{"access_token":"{{accessToken}}","refresh_token":"{{refreshToken}}","id_token":"{{IdToken("owner@example.com")}}","expires_in":{{expiresIn}},"token_type":"Bearer"}""";

    private static string IdToken(string email)
    {
        static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Encode("{\"alg\":\"none\"}") + "." + Encode($$"""{"email":"{{email}}"}""") + ".";
    }

    /// <summary>Returns the access token that an API call receives.</summary>
    private static Task<string> GetAccessTokenAsync(TeslaTokenManager manager, CancellationToken cancellationToken) =>
        manager.CallAsync(Task.FromResult, cancellationToken);

    [Fact]
    public async Task WhenAccessTokenIsRequested_ThenRefreshTokenIsRotatedAndPersistedBeforeUse()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler().Respond(TokenPath, HttpStatusCode.OK, TokenJson("access-1", "refresh-2"));
        using var httpClient = new HttpClient(handler);
        var time = new FakeTimeProvider(Start);
        var persisted = new List<string>();
        var manager = new TeslaTokenManager(new TeslaAuthClient(httpClient), "refresh-1", time,
            token => { persisted.Add(token); return Task.CompletedTask; });

        // Act
        var accessToken = await GetAccessTokenAsync(manager, CancellationToken.None);

        // Assert
        Assert.Equal("access-1", accessToken);
        Assert.Equal(["refresh-2"], persisted);
        Assert.Equal("owner@example.com", manager.AuthenticatedUser);
        Assert.Equal(Start, manager.AuthenticatedAt);
        var body = handler.Requests.Single().Body!;
        Assert.Contains("grant_type=refresh_token", body);
        Assert.Contains("client_id=ownerapi", body);
        Assert.Contains("refresh_token=refresh-1", body);
    }

    [Fact]
    public async Task WhenPersistingTheRotatedTokenFails_ThenOnlyThePersistingIsRetriedBeforeTheAccessTokenIsHandedOut()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler()
            .Respond(TokenPath, HttpStatusCode.OK, TokenJson("access-1", "refresh-2"))
            .Respond(TokenPath, HttpStatusCode.OK, TokenJson("access-2", "refresh-3"));
        using var httpClient = new HttpClient(handler);
        var persisted = new List<string>();
        var shouldFail = true;
        var manager = new TeslaTokenManager(new TeslaAuthClient(httpClient), "refresh-1", new FakeTimeProvider(Start), token =>
        {
            persisted.Add(token);
            return shouldFail ? throw new IOException("disk full") : Task.CompletedTask;
        });

        // Act
        await Assert.ThrowsAsync<IOException>(() => GetAccessTokenAsync(manager, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => GetAccessTokenAsync(manager, CancellationToken.None));
        shouldFail = false;
        var accessToken = await GetAccessTokenAsync(manager, CancellationToken.None);
        var accessTokenAfterPersist = await GetAccessTokenAsync(manager, CancellationToken.None);

        // Assert
        Assert.Equal("access-1", accessToken);
        Assert.Equal("access-1", accessTokenAfterPersist);
        Assert.Equal(["refresh-2", "refresh-2", "refresh-2"], persisted);
        Assert.Single(handler.Requests);
        Assert.Equal(Start, manager.AuthenticatedAt);
    }

    [Fact]
    public async Task WhenAPendingAccessTokenIsDue_ThenTheRotatedTokenIsRefreshed()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler()
            .Respond(TokenPath, HttpStatusCode.OK, TokenJson("access-1", "refresh-2"))
            .Respond(TokenPath, HttpStatusCode.OK, TokenJson("access-2", "refresh-3"));
        using var httpClient = new HttpClient(handler);
        var time = new FakeTimeProvider(Start);
        var shouldFail = true;
        var manager = new TeslaTokenManager(new TeslaAuthClient(httpClient), "refresh-1", time,
            _ => shouldFail ? throw new IOException("disk full") : Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(() => GetAccessTokenAsync(manager, CancellationToken.None));

        // Act
        time.Advance(TimeSpan.FromHours(7.5));
        shouldFail = false;
        var accessToken = await GetAccessTokenAsync(manager, CancellationToken.None);

        // Assert
        Assert.Equal("access-2", accessToken);
        Assert.Contains("refresh_token=refresh-2", handler.Requests[1].Body);
    }

    [Fact]
    public async Task WhenTheAccessTokenLivesShort_ThenItIsRefreshedAtHalfItsLifetime()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler()
            .Respond(TokenPath, HttpStatusCode.OK, TokenJson("access-1", "refresh-2", expiresIn: 600))
            .Respond(TokenPath, HttpStatusCode.OK, TokenJson("access-2", "refresh-3", expiresIn: 600));
        using var httpClient = new HttpClient(handler);
        var time = new FakeTimeProvider(Start);
        var manager = new TeslaTokenManager(new TeslaAuthClient(httpClient), "refresh-1", time, _ => Task.CompletedTask);
        await GetAccessTokenAsync(manager, CancellationToken.None);

        // Act
        time.Advance(TimeSpan.FromMinutes(4));
        var beforeHalf = await GetAccessTokenAsync(manager, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(2));
        var afterHalf = await GetAccessTokenAsync(manager, CancellationToken.None);

        // Assert
        Assert.Equal("access-1", beforeHalf);
        Assert.Equal("access-2", afterHalf);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task WhenExpiresInIsNotPositive_ThenEightHoursAreAssumed(int expiresIn)
    {
        // Arrange
        var handler = new FakeHttpMessageHandler().Respond(TokenPath, HttpStatusCode.OK, TokenJson("access-1", "refresh-2", expiresIn));
        using var httpClient = new HttpClient(handler);
        var client = new TeslaAuthClient(httpClient);

        // Act
        var tokens = await client.RefreshAsync("refresh-1", new FakeTimeProvider(Start), CancellationToken.None);

        // Assert
        Assert.Equal(Start.AddHours(8), tokens.ExpiresAt);
    }

    [Fact]
    public async Task WhenAuthServerLimitsRequests_ThenRetryAfterIsReported()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler().Respond(TokenPath, HttpStatusCode.TooManyRequests, "{}", ("Retry-After", "120"));
        using var httpClient = new HttpClient(handler);
        var client = new TeslaAuthClient(httpClient);

        // Act
        var exception = await Assert.ThrowsAsync<TeslaApiException>(() => client.RefreshAsync("refresh-1", new FakeTimeProvider(Start), CancellationToken.None));

        // Assert
        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(120), exception.RetryAfter);
    }

    [Fact]
    public async Task WhenTokenIsStillValid_ThenNoRefreshHappens()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler().Respond(TokenPath, HttpStatusCode.OK, TokenJson("access-1", "refresh-2"));
        using var httpClient = new HttpClient(handler);
        var time = new FakeTimeProvider(Start);
        var manager = new TeslaTokenManager(new TeslaAuthClient(httpClient), "refresh-1", time, _ => Task.CompletedTask);
        await GetAccessTokenAsync(manager, CancellationToken.None);

        // Act
        time.Advance(TimeSpan.FromHours(6)); // 2 h left of 8 h
        await GetAccessTokenAsync(manager, CancellationToken.None);

        // Assert
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task WhenLessThanOneHourIsLeft_ThenTokenIsRefreshed()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler()
            .Respond(TokenPath, HttpStatusCode.OK, TokenJson("access-1", "refresh-2"))
            .Respond(TokenPath, HttpStatusCode.OK, TokenJson("access-2", "refresh-3"));
        using var httpClient = new HttpClient(handler);
        var time = new FakeTimeProvider(Start);
        var persisted = new List<string>();
        var manager = new TeslaTokenManager(new TeslaAuthClient(httpClient), "refresh-1", time,
            token => { persisted.Add(token); return Task.CompletedTask; });
        await GetAccessTokenAsync(manager, CancellationToken.None);

        // Act
        time.Advance(TimeSpan.FromHours(7.5));
        var accessToken = await GetAccessTokenAsync(manager, CancellationToken.None);

        // Assert
        Assert.Equal("access-2", accessToken);
        Assert.Equal(["refresh-2", "refresh-3"], persisted);
    }

    [Fact]
    public async Task WhenCallersRequestATokenConcurrently_ThenOnlyOneRefreshRuns()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler().Respond(TokenPath, HttpStatusCode.OK, TokenJson("access-1", "refresh-2"));
        using var httpClient = new HttpClient(handler);
        var persistEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persistGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new TeslaTokenManager(new TeslaAuthClient(httpClient), "refresh-1", new FakeTimeProvider(Start),
            async _ => { persistEntered.TrySetResult(); await persistGate.Task; });

        // Act
        var first = GetAccessTokenAsync(manager, CancellationToken.None);
        await persistEntered.Task;
        var second = GetAccessTokenAsync(manager, CancellationToken.None);
        var secondCompletedBeforePersist = second.IsCompleted;
        persistGate.SetResult();
        var accessTokens = await Task.WhenAll(first, second);

        // Assert
        Assert.False(secondCompletedBeforePersist);
        Assert.Equal(["access-1", "access-1"], accessTokens);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task WhenAWaitingCallerIsCanceled_ThenTheRefreshStillCompletes()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler().Respond(TokenPath, HttpStatusCode.OK, TokenJson("access-1", "refresh-2"));
        using var httpClient = new HttpClient(handler);
        var persisted = new List<string>();
        var persistEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persistGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new TeslaTokenManager(new TeslaAuthClient(httpClient), "refresh-1", new FakeTimeProvider(Start),
            async token => { persistEntered.TrySetResult(); await persistGate.Task; persisted.Add(token); });
        using var cancellation = new CancellationTokenSource();

        // Act
        var canceled = GetAccessTokenAsync(manager, cancellation.Token);
        await persistEntered.Task;
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        persistGate.SetResult();
        var accessToken = await GetAccessTokenAsync(manager, CancellationToken.None);

        // Assert
        Assert.Equal("access-1", accessToken);
        Assert.Equal(["refresh-2"], persisted);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task WhenRefreshIsRejected_ThenReauthenticationIsRequired()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler().Respond(TokenPath, HttpStatusCode.Unauthorized, """{"error":"login_required"}""");
        using var httpClient = new HttpClient(handler);
        var manager = new TeslaTokenManager(new TeslaAuthClient(httpClient), "refresh-1", TimeProvider.System, _ => Task.CompletedTask);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<TeslaReauthenticationRequiredException>(() => GetAccessTokenAsync(manager, CancellationToken.None));
        Assert.DoesNotContain("refresh-1", exception.Message);
    }

    [Fact]
    public async Task WhenGrantIsInvalid_ThenReauthenticationIsRequired()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler().Respond(TokenPath, HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        using var httpClient = new HttpClient(handler);
        var manager = new TeslaTokenManager(new TeslaAuthClient(httpClient), "refresh-1", TimeProvider.System, _ => Task.CompletedTask);

        // Act & Assert
        await Assert.ThrowsAsync<TeslaReauthenticationRequiredException>(() => GetAccessTokenAsync(manager, CancellationToken.None));
    }

    [Fact]
    public async Task WhenAuthServerFailsTemporarily_ThenAnApiExceptionWithoutTheBodyIsThrown()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler().Respond(TokenPath, HttpStatusCode.BadGateway, "<html>refresh-1 upstream error</html>");
        using var httpClient = new HttpClient(handler);
        var manager = new TeslaTokenManager(new TeslaAuthClient(httpClient), "refresh-1", TimeProvider.System, _ => Task.CompletedTask);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<TeslaApiException>(() => GetAccessTokenAsync(manager, CancellationToken.None));
        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.DoesNotContain("refresh-1", exception.Message);
    }

    [Fact]
    public async Task WhenAnApiCallIsUnauthorized_ThenTheTokenIsRefreshedOnceAndTheCallRetried()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler()
            .Respond(TokenPath, HttpStatusCode.OK, TokenJson("access-1", "refresh-2"))
            .Respond(TokenPath, HttpStatusCode.OK, TokenJson("access-2", "refresh-3"));
        using var httpClient = new HttpClient(handler);
        var persisted = new List<string>();
        var manager = new TeslaTokenManager(new TeslaAuthClient(httpClient), "refresh-1", new FakeTimeProvider(Start),
            token => { persisted.Add(token); return Task.CompletedTask; });
        var usedTokens = new List<string>();

        // Act
        var result = await manager.CallAsync(accessToken =>
        {
            usedTokens.Add(accessToken);
            return accessToken == "access-1"
                ? throw new TeslaApiException(HttpStatusCode.Unauthorized, "unauthorized")
                : Task.FromResult(42);
        }, CancellationToken.None);

        // Assert
        Assert.Equal(42, result);
        Assert.Equal(["access-1", "access-2"], usedTokens);
        Assert.Equal(["refresh-2", "refresh-3"], persisted);
    }

    [Fact]
    public async Task WhenTheRefreshedTokenIsUnauthorizedAgain_ThenReauthenticationIsRequired()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler()
            .Respond(TokenPath, HttpStatusCode.OK, TokenJson("access-1", "refresh-2"))
            .Respond(TokenPath, HttpStatusCode.OK, TokenJson("access-2", "refresh-3"));
        using var httpClient = new HttpClient(handler);
        var manager = new TeslaTokenManager(new TeslaAuthClient(httpClient), "refresh-1", new FakeTimeProvider(Start), _ => Task.CompletedTask);

        // Act & Assert
        await Assert.ThrowsAsync<TeslaReauthenticationRequiredException>(() => manager.CallAsync<int>(
            _ => throw new TeslaApiException(HttpStatusCode.Unauthorized, "unauthorized"), CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void WhenTokensAreFormatted_ThenSecretsAreNotIncluded()
    {
        // Arrange
        var tokens = new TeslaTokens("access-secret", "refresh-secret", Start, "owner@example.com");
        var login = new TeslaLogin("https://auth.tesla.com/oauth2/v3/authorize", "state", "verifier-secret");

        // Act
        var text = tokens + " " + login;

        // Assert
        Assert.DoesNotContain("secret", text);
        Assert.Contains("owner@example.com", text);
    }

    [Fact]
    public void WhenHttpClientIsCreated_ThenItRequiresHttp2()
    {
        // Act
        using var httpClient = TeslaHttp.CreateClient();

        // Assert
        Assert.Equal(HttpVersion.Version20, httpClient.DefaultRequestVersion);
        Assert.Equal(HttpVersionPolicy.RequestVersionExact, httpClient.DefaultVersionPolicy);
    }

    [Fact]
    public void WhenLoginIsStarted_ThenAuthorizeUrlHasPkce()
    {
        // Act
        var login = TeslaAuthClient.CreateLogin();

        // Assert
        Assert.StartsWith("https://auth.tesla.com/oauth2/v3/authorize?", login.Url);
        Assert.Contains("client_id=ownerapi", login.Url);
        Assert.Contains("code_challenge_method=S256", login.Url);
        Assert.Contains("redirect_uri=tesla%3A%2F%2Fauth%2Fcallback", login.Url);
        Assert.Contains("state=" + login.State, login.Url);
        Assert.True(login.CodeVerifier.Length >= 43);
    }

    [Fact]
    public async Task WhenCodeIsExchanged_ThenVerifierAndRedirectAreSent()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler().Respond(TokenPath, HttpStatusCode.OK, TokenJson("access-1", "refresh-1"));
        using var httpClient = new HttpClient(handler);
        var client = new TeslaAuthClient(httpClient);

        // Act
        var tokens = await client.ExchangeCodeAsync("abc", "verifier", new FakeTimeProvider(Start), CancellationToken.None);

        // Assert
        Assert.Equal("refresh-1", tokens.RefreshToken);
        Assert.Equal(Start.AddHours(8), tokens.ExpiresAt);
        var body = handler.Requests.Single().Body!;
        Assert.Contains("grant_type=authorization_code", body);
        Assert.Contains("code=abc", body);
        Assert.Contains("code_verifier=verifier", body);
        Assert.Contains("redirect_uri=tesla%3A%2F%2Fauth%2Fcallback", body);
    }

    [Theory]
    [InlineData("tesla://auth/callback?code=abc&state=xyz&issuer=https%3A%2F%2Fauth.tesla.com%2Foauth2%2Fv3")]
    [InlineData("code=abc&state=xyz")]
    [InlineData("?code=abc&state=xyz")]
    public void WhenCallbackIsPasted_ThenCodeIsExtracted(string pasted)
    {
        // Act
        var code = TeslaAuthClient.ParseCallbackCode(pasted, "xyz");

        // Assert
        Assert.Equal("abc", code);
    }

    [Fact]
    public void WhenCallbackHasAFragment_ThenItIsIgnored()
    {
        // Act
        var code = TeslaAuthClient.ParseCallbackCode("tesla://auth/callback?code=abc&state=xyz#_=_", "xyz");

        // Assert
        Assert.Equal("abc", code);
    }

    [Fact]
    public void WhenCallbackHasAnError_ThenTheErrorIsReported()
    {
        // Act & Assert
        var exception = Assert.Throws<FormatException>(() => TeslaAuthClient.ParseCallbackCode(
            "tesla://auth/callback?error=access_denied&error_description=User%20canceled&state=xyz", "xyz"));
        Assert.Contains("access_denied", exception.Message);
        Assert.Contains("User canceled", exception.Message);
    }

    [Fact]
    public void WhenCallbackStateDiffers_ThenItIsRejected()
    {
        // Act & Assert
        Assert.Throws<FormatException>(() => TeslaAuthClient.ParseCallbackCode("code=abc&state=other", "xyz"));
    }
}
