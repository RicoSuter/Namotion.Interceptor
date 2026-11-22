using System.Net;
using Microsoft.Extensions.Time.Testing;
using Namotion.Devices.Hyundai.Client;
using Namotion.Devices.Hyundai.Tests.Testing;

namespace Namotion.Devices.Hyundai.Tests;

public class BluelinkTokenManagerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1700000000);

    private sealed class Harness
    {
        public FakeHttpMessageHandler Handler { get; } = new();
        public FakeTimeProvider Time { get; } = new(Now);
        public List<BluelinkTokenSet> Persisted { get; } = [];
        public int LoginCount { get; set; }
        public Exception? PersistFailure { get; set; }
        public Func<CancellationToken, Task<BluelinkCciTokens>>? Login { get; set; }

        public BluelinkTokenManager Create()
        {
            var authClient = new BluelinkAuthClient(new HttpClient(Handler), Time);
            return new BluelinkTokenManager(authClient, BluelinkResponses.TokenSet(0), "device-1", Time, tokens =>
            {
                if (PersistFailure is { } failure)
                {
                    PersistFailure = null;
                    throw failure;
                }

                Persisted.Add(tokens);
                return Task.CompletedTask;
            }, Login);
        }
    }

    private static Task<string> Echo(BluelinkTokenManager manager) => manager.CallAsync(accessToken => Task.FromResult(accessToken), CancellationToken.None);

    [Fact]
    public async Task WhenFirstCallIsMade_ThenTokensAreRefreshedPersistedAndExchanged()
    {
        // Arrange
        var harness = new Harness();
        harness.Handler.Respond(BluelinkResponses.RefreshPath, HttpStatusCode.OK, BluelinkResponses.Tokens(1))
            .Respond(BluelinkResponses.ExchangePath, HttpStatusCode.OK, BluelinkResponses.Ccs(1));
        var manager = harness.Create();

        // Act
        var accessToken = await Echo(manager);

        // Assert
        Assert.Equal("ccs-1", accessToken);
        Assert.Equal([BluelinkResponses.TokenSet(1)], harness.Persisted);
        Assert.Equal(Now, manager.AuthenticatedAt);
    }

    [Fact]
    public async Task WhenAccessTokenIsFresh_ThenNoRefreshHappens()
    {
        // Arrange
        var harness = new Harness();
        harness.Handler.Respond(BluelinkResponses.RefreshPath, HttpStatusCode.OK, BluelinkResponses.Tokens(1))
            .Respond(BluelinkResponses.ExchangePath, HttpStatusCode.OK, BluelinkResponses.Ccs(1));
        var manager = harness.Create();
        await Echo(manager);

        // Act
        harness.Time.Advance(TimeSpan.FromMinutes(50));
        await Echo(manager);

        // Assert
        Assert.Equal(1, harness.Handler.Count(BluelinkResponses.RefreshPath));
    }

    [Fact]
    public async Task WhenAccessTokenIsWithinSixMinutesOfExpiry_ThenItRefreshes()
    {
        // Arrange
        var harness = new Harness();
        harness.Handler.Respond(BluelinkResponses.RefreshPath, HttpStatusCode.OK, BluelinkResponses.Tokens(1))
            .Respond(BluelinkResponses.RefreshPath, HttpStatusCode.OK, BluelinkResponses.Tokens(2))
            .Respond(BluelinkResponses.ExchangePath, HttpStatusCode.OK, BluelinkResponses.Ccs(1));
        var manager = harness.Create();
        await Echo(manager);

        // Act
        harness.Time.Advance(TimeSpan.FromSeconds(3599 - 360 + 1));
        await Echo(manager);

        // Assert
        Assert.Equal(2, harness.Handler.Count(BluelinkResponses.RefreshPath));
        Assert.Equal(BluelinkResponses.TokenSet(2), harness.Persisted[^1]);
    }

    [Fact]
    public async Task WhenPersistingFails_ThenTheNextCallPersistsWithoutRefreshingAgain()
    {
        // Arrange
        var harness = new Harness { PersistFailure = new IOException("disk full") };
        harness.Handler.Respond(BluelinkResponses.RefreshPath, HttpStatusCode.OK, BluelinkResponses.Tokens(1))
            .Respond(BluelinkResponses.ExchangePath, HttpStatusCode.OK, BluelinkResponses.Ccs(1));
        var manager = harness.Create();
        await Assert.ThrowsAsync<IOException>(() => Echo(manager));

        // Act
        var accessToken = await Echo(manager);

        // Assert
        Assert.Equal("ccs-1", accessToken);
        Assert.Equal(1, harness.Handler.Count(BluelinkResponses.RefreshPath));
        Assert.Equal([BluelinkResponses.TokenSet(1)], harness.Persisted);
    }

    [Fact]
    public async Task WhenRefreshIsRejected_ThenItLogsInOnce()
    {
        // Arrange
        var harness = new Harness();
        harness.Login = _ =>
        {
            harness.LoginCount++;
            return Task.FromResult(new BluelinkCciTokens(BluelinkResponses.TokenSet(5), TimeSpan.FromSeconds(3599)));
        };
        harness.Handler.Respond(BluelinkResponses.RefreshPath, HttpStatusCode.BadRequest, BluelinkResponses.CciError(4111))
            .Respond(BluelinkResponses.ExchangePath, HttpStatusCode.OK, BluelinkResponses.Ccs(5));
        var manager = harness.Create();

        // Act
        var accessToken = await Echo(manager);

        // Assert
        Assert.Equal("ccs-5", accessToken);
        Assert.Equal(1, harness.LoginCount);
        Assert.Equal([BluelinkResponses.TokenSet(5)], harness.Persisted);
    }

    [Fact]
    public async Task WhenRefreshIsRejectedWithoutPassword_ThenReauthenticationIsRequired()
    {
        // Arrange
        var harness = new Harness();
        harness.Handler.Respond(BluelinkResponses.RefreshPath, HttpStatusCode.BadRequest, BluelinkResponses.CciError(4111));
        var manager = harness.Create();

        // Act & Assert
        await Assert.ThrowsAsync<BluelinkReauthenticationRequiredException>(() => Echo(manager));
    }

    [Fact]
    public async Task WhenFingerprintIsRejected_ThenNoLoginIsAttempted()
    {
        // Arrange
        var harness = new Harness();
        harness.Login = _ =>
        {
            harness.LoginCount++;
            return Task.FromResult(new BluelinkCciTokens(BluelinkResponses.TokenSet(5), TimeSpan.FromSeconds(3599)));
        };
        harness.Handler.Respond(BluelinkResponses.RefreshPath, HttpStatusCode.BadRequest, BluelinkResponses.CciError(9036));
        var manager = harness.Create();

        // Act
        var exception = await Assert.ThrowsAsync<BluelinkApiException>(() => Echo(manager));

        // Assert
        Assert.True(exception.IsFingerprintRejected);
        Assert.Equal(0, harness.LoginCount);
    }

    [Fact]
    public async Task WhenCallReportsAnExpiredToken_ThenItRefreshesAndRepeatsOnce()
    {
        // Arrange
        var harness = new Harness();
        harness.Handler.Respond(BluelinkResponses.RefreshPath, HttpStatusCode.OK, BluelinkResponses.Tokens(1))
            .Respond(BluelinkResponses.RefreshPath, HttpStatusCode.OK, BluelinkResponses.Tokens(2))
            .Respond(BluelinkResponses.ExchangePath, HttpStatusCode.OK, BluelinkResponses.Ccs(1))
            .Respond(BluelinkResponses.ExchangePath, HttpStatusCode.OK, BluelinkResponses.Ccs(2));
        var manager = harness.Create();
        var calls = new List<string>();

        // Act
        var result = await manager.CallAsync(accessToken =>
        {
            calls.Add(accessToken);
            return calls.Count == 1
                ? throw new BluelinkApiException(HttpStatusCode.OK, "7501", "token expired")
                : Task.FromResult(accessToken);
        }, CancellationToken.None);

        // Assert
        Assert.Equal(["ccs-1", "ccs-2"], calls);
        Assert.Equal("ccs-2", result);
    }

    [Fact]
    public async Task WhenRefreshedTokenIsAlsoRejected_ThenReauthenticationIsRequired()
    {
        // Arrange
        var harness = new Harness();
        harness.Handler.Respond(BluelinkResponses.RefreshPath, HttpStatusCode.OK, BluelinkResponses.Tokens(1))
            .Respond(BluelinkResponses.ExchangePath, HttpStatusCode.OK, BluelinkResponses.Ccs(1));
        var manager = harness.Create();

        // Act & Assert
        await Assert.ThrowsAsync<BluelinkReauthenticationRequiredException>(() => manager.CallAsync<string>(
            _ => throw new BluelinkApiException(HttpStatusCode.OK, "7501", null), CancellationToken.None));
    }
}
