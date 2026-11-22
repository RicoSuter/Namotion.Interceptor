using System.Net;
using HomeBlaze.Abstractions;
using Namotion.Devices.Hyundai.Client;
using Namotion.Devices.Hyundai.Tests.Testing;

namespace Namotion.Devices.Hyundai.Tests;

public class BluelinkAccountAuthenticationTests
{
    [Fact]
    public async Task WhenTokensRotate_ThenTheRotatedSetIsStored()
    {
        // Arrange
        var (account, _, _) = TestAccount.Create();

        // Act
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal(BluelinkResponses.TokenSet(1), BluelinkTokenSet.Parse(account.Tokens));
    }

    [Fact]
    public async Task WhenRefreshIsRejectedWithoutPassword_ThenReauthenticationIsRequiredAndPollingWaits()
    {
        // Arrange
        var (account, _, _) = TestAccount.Create(new FakeHttpMessageHandler()
            .Respond(BluelinkResponses.RefreshPath, HttpStatusCode.BadRequest, BluelinkResponses.CciError(4111)));

        // Act
        await account.PollOnceAsync(CancellationToken.None);
        var delay = await account.RunIterationAsync(CancellationToken.None);

        // Assert
        Assert.True(account.IsReauthenticationRequired);
        Assert.False(account.IsAuthenticated);
        Assert.Equal(ServiceStatus.Error, account.Status);
        Assert.StartsWith("Re-authentication required", account.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(Timeout.InfiniteTimeSpan, delay);
    }

    [Fact]
    public async Task WhenCredentialsAreReplacedAfterARejection_ThenPollingResumesWithTheNewTokens()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler()
            .Respond(BluelinkResponses.RefreshPath, HttpStatusCode.BadRequest, BluelinkResponses.CciError(4111));
        var (account, _, _) = TestAccount.Create(handler);
        await account.PollOnceAsync(CancellationToken.None);

        // Act
        account.ReplaceCredentials(new BluelinkConnection("user@example.invalid", "password-1", "", BluelinkResponses.TokenSet(7).Serialize(), "device-1", []));
        handler.Reset(BluelinkResponses.RefreshPath)
            .Respond(BluelinkResponses.RefreshPath, HttpStatusCode.OK, BluelinkResponses.Tokens(8))
            .Respond(BluelinkResponses.ExchangePath, HttpStatusCode.OK, BluelinkResponses.Ccs(8))
            .Respond(BluelinkResponses.VehiclesPath, HttpStatusCode.OK, BluelinkResponses.Success("""{"vehicles":[]}"""));
        await account.RunIterationAsync(CancellationToken.None);

        // Assert
        Assert.False(account.IsReauthenticationRequired);
        Assert.True(account.IsAuthenticated);
        Assert.Equal(BluelinkResponses.TokenSet(8), BluelinkTokenSet.Parse(account.Tokens));
    }

    [Fact]
    public async Task WhenStoredTokensAreNotValid_ThenReauthenticationIsRequired()
    {
        // Arrange
        var (account, handler, _) = TestAccount.Create();
        account.Tokens = "not json";

        // Act
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.True(account.IsReauthenticationRequired);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task WhenPasswordIsAddedAfterARejection_ThenPollingResumesAndSignsIn()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler()
            .Respond(BluelinkResponses.RefreshPath, HttpStatusCode.BadRequest, BluelinkResponses.CciError(4111));
        var (account, _, _) = TestAccount.Create(handler);
        await account.PollOnceAsync(CancellationToken.None);

        // Act
        account.Password = "password-1";
        TestAccount.RespondLogin(handler, 5)
            .Respond(BluelinkResponses.ExchangePath, HttpStatusCode.OK, BluelinkResponses.Ccs(5))
            .Respond(BluelinkResponses.VehiclesPath, HttpStatusCode.OK, BluelinkResponses.Success("""{"vehicles":[]}"""));
        var delay = await account.RunIterationAsync(CancellationToken.None);

        // Assert
        Assert.False(account.IsReauthenticationRequired);
        Assert.True(account.IsAuthenticated);
        Assert.Equal(ServiceStatus.Running, account.Status);
        Assert.Equal(1, handler.Count(BluelinkResponses.SignInPath));
        Assert.Equal(BluelinkResponses.TokenSet(5), BluelinkTokenSet.Parse(account.Tokens));
        Assert.Equal(TimeSpan.FromMinutes(30), delay);
    }

    [Fact]
    public async Task WhenPasswordIsClearedAndRefreshIsRejected_ThenReauthenticationIsRequiredWithoutSignIn()
    {
        // Arrange
        var handler = TestAccount.RespondLogin(new FakeHttpMessageHandler(), 5)
            .Respond(BluelinkResponses.RefreshPath, HttpStatusCode.BadRequest, BluelinkResponses.CciError(4111));
        var (account, _, _) = TestAccount.Create(handler, password: "password-1");

        // Act
        account.Password = string.Empty;
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.True(account.IsReauthenticationRequired);
        Assert.Contains("no password is stored", account.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(0, handler.Count(BluelinkResponses.AuthorizePath));
    }
}
