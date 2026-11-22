using System.Net;
using HomeBlaze.Abstractions;
using Namotion.Devices.Hyundai.Tests.Testing;
using Namotion.Interceptor.Testing;

namespace Namotion.Devices.Hyundai.Tests;

public class BluelinkAccountPollingTests
{
    [Fact]
    public async Task WhenAccountPolls_ThenElectricVehiclesAreDiscoveredByVin()
    {
        // Arrange
        var (account, _, _) = TestAccount.Create();

        // Act
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Running, account.Status);
        Assert.True(account.IsAuthenticated);
        Assert.Equal("user@example.invalid", account.AuthenticatedUser);
        Assert.Equal(["TESTVIN0000000001", "TESTVIN0000000002"], account.Vehicles.Keys.Order());
        var kona = account.Vehicles["TESTVIN0000000001"];
        Assert.Equal(BluelinkProtocol.Legacy, kona.Protocol);
        Assert.Equal(0.465m, kona.Charge.BatteryLevel);
        Assert.Equal(1.234567, kona.Location!.Value.Latitude, 6);
    }

    [Fact]
    public async Task WhenAccountPolls_ThenOnlyStoredStatusEndpointsAreCalled()
    {
        // Arrange
        var (account, handler, _) = TestAccount.Create();

        // Act
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, handler.Count(BluelinkResponses.LegacyStatusRequestPath("VEHICLE-1")));
        Assert.Equal(0, handler.Count(BluelinkResponses.Ccs2StatusRequestPath("VEHICLE-2")));
        Assert.Equal(5, account.RequestsToday);
    }

    [Fact]
    public async Task WhenLocalMidnightPasses_ThenRequestsTodayCountsOnlyTheNewDay()
    {
        // Arrange
        var (account, _, time) = TestAccount.Create();
        await account.RunIterationAsync(CancellationToken.None);

        // Act
        time.Advance(TimeSpan.FromHours(14));
        await account.RunIterationAsync(CancellationToken.None);

        // Assert (the vehicle list is not read again within 24 hours)
        Assert.Equal(4, account.RequestsToday);
    }

    [Fact]
    public async Task WhenPolledAgainWithinADay_ThenVehicleListIsNotReadAgainAndVehiclesStay()
    {
        // Arrange
        var (account, handler, time) = TestAccount.Create();
        await account.PollOnceAsync(CancellationToken.None);
        var kona = account.Vehicles["TESTVIN0000000001"];

        // Act
        time.Advance(TimeSpan.FromMinutes(30));
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, handler.Count(BluelinkResponses.VehiclesPath));
        Assert.Same(kona, account.Vehicles["TESTVIN0000000001"]);
    }

    [Fact]
    public async Task WhenOneVehicleFails_ThenTheOtherIsStillRead()
    {
        // Arrange
        var handler = TestAccount.CreateHandler();
        handler.Reset(BluelinkResponses.StatusPath("VEHICLE-2")).Respond(BluelinkResponses.StatusPath("VEHICLE-2"), HttpStatusCode.OK, BluelinkResponses.Failure("5031", "unavailable"));
        var (account, _, _) = TestAccount.Create(handler);

        // Act
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Running, account.Status);
        Assert.Contains("5031", account.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(0.465m, account.Vehicles["TESTVIN0000000001"].Charge.BatteryLevel);
    }

    [Fact]
    public async Task WhenHyundaiLimitsRequests_ThenPollingPausesForTwoHours()
    {
        // Arrange
        var handler = TestAccount.CreateHandler();
        handler.Reset(BluelinkResponses.VehiclesPath).Respond(BluelinkResponses.VehiclesPath, HttpStatusCode.OK, BluelinkResponses.Failure("5091", "Exceeds number of requests"));
        var (account, _, _) = TestAccount.Create(handler);

        // Act
        await account.PollOnceAsync(CancellationToken.None);
        var delay = await account.RunIterationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Error, account.Status);
        Assert.StartsWith("Hyundai's request limit was reached; polling resumes at 12:30", account.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromHours(2), delay);
    }

    [Fact]
    public async Task WhenBudgetLeavesOnlyTheReserve_ThenPollingWaitsUntilMidnight()
    {
        // Arrange
        var (account, handler, _) = TestAccount.Create();
        account.DailyRequestLimit = 14;
        await account.PollOnceAsync(CancellationToken.None);

        // Act
        var delay = await account.RunIterationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(TimeSpan.FromHours(13.5), delay);
        Assert.Equal(1, handler.Count(BluelinkResponses.VehiclesPath));
    }

    [Fact]
    public async Task WhenBudgetLeavesOnlyTheReserveAfterAnError_ThenStatusIsRunningWithTheReserveMessage()
    {
        // Arrange
        var (account, _, _) = TestAccount.Create();
        account.DailyRequestLimit = 14;
        await account.PollOnceAsync(CancellationToken.None);
        account.Status = ServiceStatus.Error;

        // Act
        await account.RunIterationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Running, account.Status);
        Assert.Equal("Polling pauses until midnight to keep the remaining requests for commands.", account.StatusMessage);
    }

    [Fact]
    public async Task WhenCredentialsAreMissing_ThenAccountIsStopped()
    {
        // Arrange
        var (account, handler, _) = TestAccount.Create();
        account.Tokens = string.Empty;

        // Act
        var delay = await account.RunIterationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Stopped, account.Status);
        Assert.Equal("Missing credentials", account.StatusMessage);
        Assert.Equal(Timeout.InfiniteTimeSpan, delay);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task WhenPollingIntervalIsTooShort_ThenAtLeastFifteenMinutesAreWaited()
    {
        // Arrange
        var (account, _, _) = TestAccount.Create();
        account.StatusPollingInterval = TimeSpan.FromMinutes(1);

        // Act
        var delay = await account.RunIterationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(15), delay);
    }

    [Fact]
    public async Task WhenDeviceIdIsMissing_ThenADeviceIsRegisteredFirst()
    {
        // Arrange
        var handler = TestAccount.CreateHandler()
            .Respond(BluelinkResponses.RegisterPath, HttpStatusCode.OK, BluelinkResponses.Success("""{"deviceId":"device-9"}"""));
        var (account, _, _) = TestAccount.Create(handler);
        account.DeviceId = string.Empty;

        // Act
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal("device-9", account.DeviceId);
        Assert.Equal("device-9", handler.Requests.Last(request => request.Uri.AbsolutePath == BluelinkResponses.VehiclesPath).Header("ccsp-device-id"));
    }

    [Fact]
    public async Task WhenTokenRefreshFailsTransientlyDuringAPoll_ThenAccountRetriesWithoutReauthentication()
    {
        // Arrange
        var (account, handler, time) = TestAccount.Create();
        await account.PollOnceAsync(CancellationToken.None);
        handler.Reset(BluelinkResponses.RefreshPath).Respond(BluelinkResponses.RefreshPath, HttpStatusCode.ServiceUnavailable, "{}");
        time.Advance(TimeSpan.FromHours(1));

        // Act
        await account.PollOnceAsync(CancellationToken.None);
        var delay = account.GetNextPollDelay();

        // Assert
        Assert.Equal(ServiceStatus.Error, account.Status);
        Assert.StartsWith("Bluelink returned 503", account.StatusMessage, StringComparison.Ordinal);
        Assert.False(account.IsReauthenticationRequired);
        Assert.Equal(TimeSpan.FromMinutes(5), delay);
        Assert.Equal(1, handler.Count(BluelinkResponses.VehiclesPath));
        Assert.Equal(1, handler.Count(BluelinkResponses.StatusPath("VEHICLE-2")));
    }

    [Fact]
    public async Task WhenReadingOneVehicleFailsWithANetworkError_ThenTheOtherIsStillRead()
    {
        // Arrange
        var handler = TestAccount.CreateHandler();
        handler.Reset(BluelinkResponses.StatusPath("VEHICLE-2"))
            .Respond(BluelinkResponses.StatusPath("VEHICLE-2"), () => throw new HttpRequestException("Network unreachable."));
        var (account, _, _) = TestAccount.Create(handler);

        // Act
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Running, account.Status);
        Assert.Equal("IONIQ 5: Network unreachable.", account.StatusMessage);
        Assert.Equal(0.465m, account.Vehicles["TESTVIN0000000001"].Charge.BatteryLevel);
        Assert.Equal(1, handler.Count(BluelinkResponses.ParkLocationPath("VEHICLE-1")));
    }

    [Fact]
    public async Task WhenTheParkingLocationTimesOut_ThenThePollSucceedsAndThePreviousLocationIsKept()
    {
        // Arrange
        var (account, handler, time) = TestAccount.Create();
        await account.PollOnceAsync(CancellationToken.None);
        var kona = account.Vehicles["TESTVIN0000000001"];
        var previousLocation = kona.Location;
        handler.Reset(BluelinkResponses.ParkLocationPath("VEHICLE-1")).RespondNever(BluelinkResponses.ParkLocationPath("VEHICLE-1"));
        var poll = account.PollOnceAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => handler.Count(BluelinkResponses.ParkLocationPath("VEHICLE-1")) == 2);

        // Act
        time.Advance(TimeSpan.FromSeconds(30));
        await poll;

        // Assert
        Assert.NotNull(previousLocation);
        Assert.Equal(previousLocation, kona.Location);
        Assert.Equal(ServiceStatus.Running, account.Status);
        Assert.Null(account.StatusMessage);
    }

    [Fact]
    public async Task WhenBudgetRunsOutDuringAPollAndTheLimitIsRaised_ThenTheNextIterationPolls()
    {
        // Arrange
        var (account, handler, _) = TestAccount.Create();
        account.DailyRequestLimit = 3;
        await account.PollOnceAsync(CancellationToken.None);
        var messageAfterLimit = account.StatusMessage;

        // Act
        account.DailyRequestLimit = 150;
        await account.RunIterationAsync(CancellationToken.None);

        // Assert
        Assert.Equal("Daily request limit reached.", messageAfterLimit);
        Assert.Equal(ServiceStatus.Running, account.Status);
        Assert.Equal(1, handler.Count(BluelinkResponses.StatusPath("VEHICLE-2")));
    }

    [Fact]
    public async Task WhenBudgetIsSpentAtTheStartOfAnIteration_ThenAccountReportsTheLimitAndWaitsUntilMidnight()
    {
        // Arrange
        var (account, handler, _) = TestAccount.Create();
        await account.PollOnceAsync(CancellationToken.None);
        account.DailyRequestLimit = 5;
        var requestCount = handler.Requests.Count;

        // Act
        var delay = await account.RunIterationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Error, account.Status);
        Assert.Equal("Daily request limit reached.", account.StatusMessage);
        Assert.Equal(TimeSpan.FromHours(13.5), delay);
        Assert.Equal(requestCount, handler.Requests.Count);
    }

    [Fact]
    public async Task WhenDeviceIsNotRegistered_ThenTheNextPollRegistersAgainWithANewTokenManager()
    {
        // Arrange
        var (account, handler, _) = TestAccount.Create();
        await account.PollOnceAsync(CancellationToken.None);
        handler.Reset(BluelinkResponses.StatusPath("VEHICLE-1")).Respond(BluelinkResponses.StatusPath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Failure("4002"));
        await account.PollOnceAsync(CancellationToken.None);
        var statusAfterFailure = account.Status;
        var deviceIdAfterFailure = account.DeviceId;
        handler.Reset(BluelinkResponses.StatusPath("VEHICLE-1")).Respond(BluelinkResponses.StatusPath("VEHICLE-1"), HttpStatusCode.OK, FixtureLoader.Load("carstatus-legacy-ev.json"))
            .Respond(BluelinkResponses.RegisterPath, HttpStatusCode.OK, BluelinkResponses.Success("""{"deviceId":"device-9"}"""));

        // Act
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Error, statusAfterFailure);
        Assert.Equal(string.Empty, deviceIdAfterFailure);
        Assert.Equal(ServiceStatus.Running, account.Status);
        Assert.Equal("device-9", account.DeviceId);
        Assert.Equal(2, handler.Count(BluelinkResponses.RefreshPath));
        Assert.Equal(1, handler.Count(BluelinkResponses.VehiclesPath));
        Assert.Equal("device-9", handler.Requests.Last(request => request.Uri.AbsolutePath == BluelinkResponses.StatusPath("VEHICLE-1")).Header("ccsp-device-id"));
    }

    [Fact]
    public async Task WhenTheVehicleListChanges_ThenVanishedVehiclesAreRemovedAndKnownOnesUpdatedInPlace()
    {
        // Arrange
        var (account, handler, time) = TestAccount.Create();
        await account.PollOnceAsync(CancellationToken.None);
        var kona = account.Vehicles["TESTVIN0000000001"];
        handler.Reset(BluelinkResponses.VehiclesPath).Respond(BluelinkResponses.VehiclesPath, HttpStatusCode.OK, BluelinkResponses.Success(
            """{"vehicles":[{"vin":"TESTVIN0000000001","vehicleId":"VEHICLE-1","vehicleName":"KONA","type":"EV","nickname":"Kona Electric","ccuCCS2ProtocolSupport":0}]}"""));

        // Act
        time.Advance(TimeSpan.FromHours(24));
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal(["TESTVIN0000000001"], account.Vehicles.Keys);
        Assert.Same(kona, account.Vehicles["TESTVIN0000000001"]);
        Assert.Equal("Kona Electric", kona.Nickname);
    }

    [Fact]
    public async Task WhenTheTokenExpiresAgainAfterTheRefresh_ThenThePollFailsWithoutReauthentication()
    {
        // Arrange
        var (account, handler, _) = TestAccount.Create();
        await account.PollOnceAsync(CancellationToken.None);
        handler.Reset(BluelinkResponses.StatusPath("VEHICLE-1")).Respond(BluelinkResponses.StatusPath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Failure("7501"));

        // Act
        await account.PollOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Error, account.Status);
        Assert.False(account.IsReauthenticationRequired);
        Assert.Equal(2, handler.Count(BluelinkResponses.RefreshPath));
        Assert.Equal(1, handler.Count(BluelinkResponses.StatusPath("VEHICLE-2")));
    }

    [Fact]
    public async Task WhenCredentialsAreRemoved_ThenAuthenticationStateIsCleared()
    {
        // Arrange
        var (account, _, _) = TestAccount.Create();
        await account.PollOnceAsync(CancellationToken.None);

        // Act
        account.Tokens = string.Empty;
        await account.RunIterationAsync(CancellationToken.None);

        // Assert
        Assert.Equal("Missing credentials", account.StatusMessage);
        Assert.False(account.IsAuthenticated);
        Assert.Null(account.AuthenticatedUser);
        Assert.Null(account.AuthenticatedAt);
    }
}
