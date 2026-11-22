using System.Net;
using HomeBlaze.Abstractions;
using Namotion.Devices.Hyundai.Client;
using Namotion.Devices.Hyundai.Tests.Testing;
using Namotion.Interceptor.Testing;

namespace Namotion.Devices.Hyundai.Tests;

/// <summary>How refreshes and commands affect the account state: cancellation, the device ID and the request budget.</summary>
public class BluelinkAccountCommandStateTests
{
    private const string Kona = "TESTVIN0000000001";

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
    }
}
