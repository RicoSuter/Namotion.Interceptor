using System.Net;
using HomeBlaze.Abstractions;
using Namotion.Devices.Hyundai.Client;
using Namotion.Devices.Hyundai.Tests.Testing;
using Namotion.Interceptor.Testing;

namespace Namotion.Devices.Hyundai.Tests;

/// <summary>How refreshes and commands affect the account and vehicle state: cancellation, the device ID, the request budget and the busy vehicle.</summary>
public class BluelinkAccountCommandStateTests
{
    private const string Kona = "TESTVIN0000000001";
    private const string Ioniq = "TESTVIN0000000002";
    private const string BusyMessage = "Another operation of this vehicle is running.";

    [Fact]
    public async Task WhenCommandIsCanceledDuringTheResultWait_ThenAccountStateIsUnchanged()
    {
        // Arrange
        var (account, handler, _) = TestAccount.Create();
        await account.PollOnceAsync(CancellationToken.None);
        handler.Respond(BluelinkResponses.LegacyChargePath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Command("MSG-7"));
        using var cancellation = new CancellationTokenSource();
        var command = account.Vehicles[Kona].Charge.StartChargingAsync(cancellation.Token);
        await AsyncTestHelpers.WaitUntilAsync(() => handler.Count(BluelinkResponses.LegacyChargePath("VEHICLE-1")) == 1);

        // Act
        await cancellation.CancelAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command);
        Assert.Equal(ServiceStatus.Running, account.Status);
        Assert.Null(account.StatusMessage);
        Assert.True(account.IsAuthenticated);
    }

    [Fact]
    public async Task WhenCommandFollowsADeviceNotRegisteredAnswer_ThenTheDeviceIsRegisteredFirst()
    {
        // Arrange
        var (account, handler, time) = TestAccount.Create();
        await account.PollOnceAsync(CancellationToken.None);
        handler.Reset(BluelinkResponses.StatusPath("VEHICLE-1")).Respond(BluelinkResponses.StatusPath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Failure("4002"));
        await account.PollOnceAsync(CancellationToken.None);
        handler.Reset(BluelinkResponses.StatusPath("VEHICLE-1")).Respond(BluelinkResponses.StatusPath("VEHICLE-1"), HttpStatusCode.OK, FixtureLoader.Load("carstatus-legacy-ev.json"))
            .Respond(BluelinkResponses.RegisterPath, HttpStatusCode.OK, BluelinkResponses.Success("""{"deviceId":"device-9"}"""))
            .Respond(BluelinkResponses.LegacyChargePath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Command("MSG-7"))
            .Respond(BluelinkResponses.RecordsPath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Records("MSG-7", "success"));
        var deviceIdAfterFailure = account.DeviceId;

        // Act
        await TestAccount.AdvanceUntilCompletedAsync(time, account.Vehicles[Kona].Charge.StartChargingAsync(CancellationToken.None));

        // Assert
        Assert.Equal(string.Empty, deviceIdAfterFailure);
        Assert.Equal("device-9", account.DeviceId);
        var requests = handler.Requests.Select(request => request.Uri.AbsolutePath).ToList();
        Assert.True(requests.IndexOf(BluelinkResponses.RegisterPath) < requests.LastIndexOf(BluelinkResponses.RefreshPath));
        Assert.Equal("device-9", handler.Requests.Single(request => request.Uri.AbsolutePath == BluelinkResponses.LegacyChargePath("VEHICLE-1")).Header("ccsp-device-id"));
    }

    [Fact]
    public async Task WhenBudgetIsSpent_ThenCommandThrowsBeforeAnyRequest()
    {
        // Arrange
        var (account, handler, _) = TestAccount.Create();
        await account.PollOnceAsync(CancellationToken.None);
        account.DailyRequestLimit = 5;
        var requestCount = handler.Requests.Count;

        // Act & Assert
        await Assert.ThrowsAsync<BluelinkRequestLimitException>(() => account.Vehicles[Kona].Charge.StartChargingAsync(CancellationToken.None));
        Assert.Equal(requestCount, handler.Requests.Count);
        Assert.Equal(ServiceStatus.Error, account.Status);
        Assert.Equal("Daily request limit reached.", account.StatusMessage);
    }

    [Fact]
    public async Task WhenRefreshIsAskedWithALowAuxiliaryBattery_ThenItThrowsBeforeAnyRequest()
    {
        // Arrange
        var (account, handler, time) = TestAccount.Create();
        await account.PollOnceAsync(CancellationToken.None);
        account.MinimumAuxiliaryBatteryLevel = 1m;
        time.Advance(TimeSpan.FromHours(1));
        var requestCount = handler.Requests.Count;

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => account.Vehicles[Kona].RefreshAsync(CancellationToken.None));
        Assert.Equal(requestCount, handler.Requests.Count);
    }

    [Fact]
    public async Task WhenTheAuxiliaryBatteryLevelIsUnknown_ThenRefreshIsDisabledAndThrowsBeforeAnyRequest()
    {
        // Arrange
        var (account, handler, _) = TestAccount.Create();
        await account.PollOnceAsync(CancellationToken.None);
        var kona = account.Vehicles[Kona];
        kona.AuxiliaryBatteryLevel = null;
        var requestCount = handler.Requests.Count;

        // Act & Assert
        Assert.False(kona.Refresh_IsEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kona.RefreshAsync(CancellationToken.None));
        Assert.Equal(requestCount, handler.Requests.Count);
    }

    [Fact]
    public async Task WhenCredentialsAreRemoved_ThenCommandThrowsBeforeAnyRequest()
    {
        // Arrange
        var (account, handler, _) = TestAccount.Create();
        await account.PollOnceAsync(CancellationToken.None);
        account.Tokens = string.Empty;
        var requestCount = handler.Requests.Count;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => account.Vehicles[Kona].Charge.StartChargingAsync(CancellationToken.None));
        Assert.Equal("The Bluelink account has no credentials.", exception.Message);
        Assert.Equal(requestCount, handler.Requests.Count);
        Assert.False(account.Vehicles[Kona].IsBusy);
    }

    [Fact]
    public async Task WhenRefreshIsRunning_ThenTheVehicleIsBusyUntilItCompletes()
    {
        // Arrange
        var (account, handler, _) = TestAccount.Create();
        await account.PollOnceAsync(CancellationToken.None);
        var heldResponse = TestAccount.CreateHeldResponse();
        handler.RespondAfter(BluelinkResponses.LegacyStatusRequestPath("VEHICLE-1"), heldResponse.Task);
        var kona = account.Vehicles[Kona];

        // Act
        var refresh = kona.RefreshAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => handler.Count(BluelinkResponses.LegacyStatusRequestPath("VEHICLE-1")) == 1);

        // Assert
        Assert.True(kona.IsBusy);
        Assert.False(kona.Refresh_IsEnabled);
        Assert.False(kona.Charge.StartCharging_IsEnabled);
        Assert.False(kona.Charge.SetChargeLimits_IsEnabled);
        Assert.False(kona.Climate.StartClimate_IsEnabled);
        Assert.False(kona.Climate.StopClimate_IsEnabled);

        heldResponse.SetResult(TestAccount.CreateResponse(HttpStatusCode.OK, BluelinkResponses.Success()));
        await refresh;
        Assert.False(kona.IsBusy);
        Assert.True(kona.Refresh_IsEnabled);
        Assert.True(kona.Charge.StartCharging_IsEnabled);
        Assert.True(kona.Climate.StartClimate_IsEnabled);
    }

    [Fact]
    public async Task WhenAnotherOperationOfTheVehicleRuns_ThenACallFailsAtOnceWithoutARequest()
    {
        // Arrange
        var (account, handler, _) = TestAccount.Create();
        await account.PollOnceAsync(CancellationToken.None);
        var heldResponse = TestAccount.CreateHeldResponse();
        handler.RespondAfter(BluelinkResponses.LegacyStatusRequestPath("VEHICLE-1"), heldResponse.Task);
        var kona = account.Vehicles[Kona];
        var refresh = kona.RefreshAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => handler.Count(BluelinkResponses.LegacyStatusRequestPath("VEHICLE-1")) == 1);
        var requestCount = handler.Requests.Count;

        // Act
        var secondRefresh = kona.RefreshAsync(CancellationToken.None);
        var command = kona.Charge.StartChargingAsync(CancellationToken.None);

        // Assert
        Assert.True(secondRefresh.IsFaulted);
        Assert.True(command.IsFaulted);
        var refreshException = await Assert.ThrowsAsync<InvalidOperationException>(() => secondRefresh);
        var commandException = await Assert.ThrowsAsync<InvalidOperationException>(() => command);
        Assert.Equal(BusyMessage, refreshException.Message);
        Assert.Equal(BusyMessage, commandException.Message);
        Assert.Equal(requestCount, handler.Requests.Count);
        Assert.True(kona.IsBusy);

        heldResponse.SetResult(TestAccount.CreateResponse(HttpStatusCode.OK, BluelinkResponses.Success()));
        await refresh;
        Assert.Equal(1, handler.Count(BluelinkResponses.LegacyStatusRequestPath("VEHICLE-1")));
        Assert.Equal(0, handler.Count(BluelinkResponses.LegacyChargePath("VEHICLE-1")));
    }

    [Fact]
    public async Task WhenAnOperationOfAnotherVehicleRuns_ThenACommandWaitsForItInsteadOfFailing()
    {
        // Arrange
        var (account, handler, time) = TestAccount.Create();
        await account.PollOnceAsync(CancellationToken.None);
        var heldResponse = TestAccount.CreateHeldResponse();
        handler.RespondAfter(BluelinkResponses.LegacyStatusRequestPath("VEHICLE-1"), heldResponse.Task)
            .Respond(BluelinkResponses.ChargeTargetPath("VEHICLE-2"), HttpStatusCode.OK, BluelinkResponses.Command("MSG-7"))
            .Respond(BluelinkResponses.RecordsPath("VEHICLE-2"), HttpStatusCode.OK, BluelinkResponses.Records("MSG-7", "success"));
        var refresh = account.Vehicles[Kona].RefreshAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => handler.Count(BluelinkResponses.LegacyStatusRequestPath("VEHICLE-1")) == 1);
        var ioniq = account.Vehicles[Ioniq];

        // Act
        var command = ioniq.Charge.SetChargeLimitsAsync(0.8m, 0.9m, CancellationToken.None);

        // Assert
        Assert.False(command.IsCompleted);
        Assert.True(ioniq.IsBusy);
        Assert.False(ioniq.Charge.SetChargeLimits_IsEnabled);
        Assert.Equal(0, handler.Count(BluelinkResponses.ChargeTargetPath("VEHICLE-2")));

        heldResponse.SetResult(TestAccount.CreateResponse(HttpStatusCode.OK, BluelinkResponses.Success()));
        await refresh;
        await TestAccount.AdvanceUntilCompletedAsync(time, command);
        Assert.Equal(1, handler.Count(BluelinkResponses.ChargeTargetPath("VEHICLE-2")));
        Assert.False(ioniq.IsBusy);
    }

    [Fact]
    public async Task WhenTheFirstOperationIsStillWaitingForAPoll_ThenASecondOperationOfTheVehicleIsRejected()
    {
        // Arrange
        var (account, handler, _) = TestAccount.Create();
        await account.PollOnceAsync(CancellationToken.None);
        var heldStatus = TestAccount.CreateHeldResponse();
        handler.Reset(BluelinkResponses.StatusPath("VEHICLE-1"))
            .RespondAfter(BluelinkResponses.StatusPath("VEHICLE-1"), heldStatus.Task)
            .Respond(BluelinkResponses.StatusPath("VEHICLE-1"), HttpStatusCode.OK, FixtureLoader.Load("carstatus-legacy-ev.json"))
            .Respond(BluelinkResponses.LegacyStatusRequestPath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Success());
        var poll = account.PollOnceAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => handler.Count(BluelinkResponses.StatusPath("VEHICLE-1")) == 2);
        var kona = account.Vehicles[Kona];
        var refresh = kona.RefreshAsync(CancellationToken.None);

        // Act
        var command = kona.Charge.StartChargingAsync(CancellationToken.None);

        // Assert
        Assert.False(refresh.IsCompleted);
        Assert.True(kona.IsBusy);
        Assert.True(command.IsFaulted);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => command);
        Assert.Equal(BusyMessage, exception.Message);

        heldStatus.SetResult(TestAccount.CreateResponse(HttpStatusCode.OK, FixtureLoader.Load("carstatus-legacy-ev.json")));
        await poll;
        await refresh;
        Assert.False(kona.IsBusy);
        Assert.Equal(1, handler.Count(BluelinkResponses.LegacyStatusRequestPath("VEHICLE-1")));
        Assert.Equal(0, handler.Count(BluelinkResponses.LegacyChargePath("VEHICLE-1")));
    }

    [Fact]
    public async Task WhenRefreshTimesOut_ThenTheAccountStatusIsUnchangedAndTheVehicleIsNoLongerBusy()
    {
        // Arrange
        var (account, handler, time) = TestAccount.Create();
        await account.PollOnceAsync(CancellationToken.None);
        handler.RespondNever(BluelinkResponses.LegacyStatusRequestPath("VEHICLE-1"));
        var kona = account.Vehicles[Kona];
        var refresh = kona.RefreshAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => handler.Count(BluelinkResponses.LegacyStatusRequestPath("VEHICLE-1")) == 1);

        // Act
        time.Advance(TimeSpan.FromSeconds(90));

        // Assert
        await Assert.ThrowsAsync<TimeoutException>(() => refresh);
        Assert.Equal(ServiceStatus.Running, account.Status);
        Assert.Null(account.StatusMessage);
        Assert.False(kona.IsBusy);
        Assert.True(kona.Refresh_IsEnabled);
    }

    [Fact]
    public async Task WhenCommandFails_ThenTheVehicleIsNoLongerBusy()
    {
        // Arrange
        var (account, handler, time) = TestAccount.Create();
        await account.PollOnceAsync(CancellationToken.None);
        handler.Respond(BluelinkResponses.LegacyChargePath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Command("MSG-7"))
            .Respond(BluelinkResponses.RecordsPath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Records("MSG-7", "fail"));
        var kona = account.Vehicles[Kona];

        // Act
        await Assert.ThrowsAsync<BluelinkCommandException>(() =>
            TestAccount.AdvanceUntilCompletedAsync(time, kona.Charge.StartChargingAsync(CancellationToken.None)));

        // Assert
        Assert.False(kona.IsBusy);
        Assert.True(kona.Charge.StartCharging_IsEnabled);
    }

    [Fact]
    public async Task WhenCommandIsCanceled_ThenTheVehicleIsNoLongerBusy()
    {
        // Arrange
        var (account, handler, _) = TestAccount.Create();
        await account.PollOnceAsync(CancellationToken.None);
        handler.RespondNever(BluelinkResponses.LegacyChargePath("VEHICLE-1"));
        var kona = account.Vehicles[Kona];
        using var cancellation = new CancellationTokenSource();
        var command = kona.Charge.StartChargingAsync(cancellation.Token);
        await AsyncTestHelpers.WaitUntilAsync(() => handler.Count(BluelinkResponses.LegacyChargePath("VEHICLE-1")) == 1);
        Assert.True(kona.IsBusy);

        // Act
        await cancellation.CancelAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command);
        Assert.False(kona.IsBusy);
        Assert.True(kona.Charge.StartCharging_IsEnabled);
    }

    [Fact]
    public async Task WhenTemperatureIsInvalid_ThenTheVehicleIsNotBusy()
    {
        // Arrange
        var (account, handler, _) = TestAccount.Create();
        await account.PollOnceAsync(CancellationToken.None);
        var kona = account.Vehicles[Kona];
        var requestCount = handler.Requests.Count;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => kona.Climate.StartClimateAsync(40m, CancellationToken.None));
        Assert.False(kona.IsBusy);
        Assert.Equal(requestCount, handler.Requests.Count);
    }
}
