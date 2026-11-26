using System.Net;
using HomeBlaze.Abstractions;
using Namotion.Devices.Hyundai.Client;
using Namotion.Devices.Hyundai.Tests.Testing;
using Namotion.Interceptor.Testing;

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
        LoginFixture.Respond(handler, tokenGeneration: 5)
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
        var handler = LoginFixture.Respond(new FakeHttpMessageHandler(), tokenGeneration: 5)
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

    [Fact]
    public async Task WhenCredentialsAreReplacedWhileTheRefreshIsRejected_ThenTheNextIterationUsesTheReplacement()
    {
        // Arrange
        var refreshResponse = TestAccount.CreateHeldResponse();
        var handler = new FakeHttpMessageHandler()
            .RespondAfter(BluelinkResponses.RefreshPath, refreshResponse.Task)
            .Respond(BluelinkResponses.RefreshPath, HttpStatusCode.OK, BluelinkResponses.Tokens(8))
            .Respond(BluelinkResponses.ExchangePath, HttpStatusCode.OK, BluelinkResponses.Ccs(8))
            .Respond(BluelinkResponses.VehiclesPath, HttpStatusCode.OK, BluelinkResponses.Success("""{"vehicles":[]}"""));
        var (account, _, _) = TestAccount.Create(handler, password: "password-1");
        var poll = account.PollOnceAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => handler.Count(BluelinkResponses.RefreshPath) == 1);
        account.ReplaceCredentials(new BluelinkConnection("user@example.invalid", "password-2", "", BluelinkResponses.TokenSet(7).Serialize(), "device-1", []));
        refreshResponse.SetResult(TestAccount.CreateResponse(HttpStatusCode.BadRequest, BluelinkResponses.CciError(4111)));
        await poll;
        var isReauthenticationRequiredAfterRejection = account.IsReauthenticationRequired;

        // Act
        var delay = await account.RunIterationAsync(CancellationToken.None);

        // Assert
        Assert.False(isReauthenticationRequiredAfterRejection);
        Assert.Equal(0, handler.Count(BluelinkResponses.AuthorizePath));
        Assert.True(account.IsAuthenticated);
        Assert.Equal(BluelinkResponses.TokenSet(8), BluelinkTokenSet.Parse(account.Tokens));
        Assert.Contains("refresh-7", handler.Requests.Last(request => request.Uri.AbsolutePath == BluelinkResponses.RefreshPath).Body, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromMinutes(30), delay);
    }

    [Fact]
    public async Task WhenTokensRotate_ThenTheRotatedSetIsWrittenBeforeTheFirstVehicleCall()
    {
        // Arrange
        var (account, handler, _) = TestAccount.Create();
        var writer = TestAccount.AttachWriter(account, handler);

        // Act
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        var write = Assert.Single(writer.Writes);
        Assert.Equal(BluelinkResponses.TokenSet(1), BluelinkTokenSet.Parse(write.Tokens));
        Assert.Equal([BluelinkResponses.RefreshPath, BluelinkResponses.ExchangePath], handler.Requests.Take(write.RequestCount).Select(request => request.Uri.AbsolutePath));
        Assert.Equal(ServiceStatus.Running, account.Status);
    }

    [Fact]
    public async Task WhenWritingTheRotatedTokensFails_ThenThePollFailsAndOnlyTheWriteIsRetried()
    {
        // Arrange
        var (account, handler, _) = TestAccount.Create();
        var writer = TestAccount.AttachWriter(account, handler);
        writer.ShouldFail = true;
        await account.PollOnceAsync(CancellationToken.None);
        var statusAfterFailure = account.Status;
        var vehicleListRequestsAfterFailure = handler.Count(BluelinkResponses.VehiclesPath);

        // Act
        writer.ShouldFail = false;
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Error, statusAfterFailure);
        Assert.Equal(0, vehicleListRequestsAfterFailure);
        Assert.Equal(1, handler.Count(BluelinkResponses.RefreshPath));
        Assert.Equal(2, writer.Writes.Count);
        Assert.All(writer.Writes, write => Assert.Equal(BluelinkResponses.TokenSet(1), BluelinkTokenSet.Parse(write.Tokens)));
        Assert.Equal(ServiceStatus.Running, account.Status);
    }

    [Fact]
    public async Task WhenTheWriterDoesNotStoreTheRotatedTokens_ThenTheWriteIsRetriedWithTheNextPoll()
    {
        // Arrange
        var (account, handler, time) = TestAccount.Create();
        var writer = TestAccount.AttachWriter(account, handler);
        writer.IsWriteAccepted = false;
        await account.PollOnceAsync(CancellationToken.None);
        var statusAfterRejectedWrite = account.Status;

        // Act
        writer.IsWriteAccepted = true;
        time.Advance(TimeSpan.FromMinutes(15));
        await account.PollOnceAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(15));
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Running, statusAfterRejectedWrite);
        Assert.Equal(2, writer.Writes.Count);
        Assert.Equal(1, handler.Count(BluelinkResponses.RefreshPath));
    }

    [Fact]
    public async Task WhenCredentialsAreReplacedDuringARotation_ThenTheRotationDoesNotOverwriteThem()
    {
        // Arrange
        var refreshResponse = TestAccount.CreateHeldResponse();
        var handler = TestAccount.CreateHandler()
            .Reset(BluelinkResponses.RefreshPath)
            .RespondAfter(BluelinkResponses.RefreshPath, refreshResponse.Task)
            .Respond(BluelinkResponses.RefreshPath, HttpStatusCode.OK, BluelinkResponses.Tokens(8));
        var (account, _, _) = TestAccount.Create(handler);
        var writer = TestAccount.AttachWriter(account, handler);
        var poll = account.PollOnceAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => handler.Count(BluelinkResponses.RefreshPath) == 1);

        // Act
        account.ReplaceCredentials(new BluelinkConnection("user@example.invalid", "", "", BluelinkResponses.TokenSet(7).Serialize(), "device-1", []));
        refreshResponse.SetResult(TestAccount.CreateResponse(HttpStatusCode.OK, BluelinkResponses.Tokens(1)));
        await poll;
        var tokensAfterRotation = account.Tokens;
        var writesAfterRotation = writer.Writes.Count;
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal(BluelinkResponses.TokenSet(7), BluelinkTokenSet.Parse(tokensAfterRotation));
        Assert.Equal(0, writesAfterRotation);
        Assert.Contains("refresh-7", handler.Requests.Last(request => request.Uri.AbsolutePath == BluelinkResponses.RefreshPath).Body, StringComparison.Ordinal);
        Assert.Equal(BluelinkResponses.TokenSet(8), BluelinkTokenSet.Parse(Assert.Single(writer.Writes).Tokens));
        Assert.Equal(2, handler.Count(BluelinkResponses.VehiclesPath));
    }
}
