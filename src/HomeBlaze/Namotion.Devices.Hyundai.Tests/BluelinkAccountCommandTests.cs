using System.Net;
using HomeBlaze.Abstractions;
using Namotion.Devices.Hyundai.Client;
using Namotion.Devices.Hyundai.Tests.Testing;
using Namotion.Interceptor.Testing;

namespace Namotion.Devices.Hyundai.Tests;

/// <summary>
/// The command and refresh rules. The preconditions that throw before any call (spent budget, low 12 V battery, missing
/// credentials) are covered in <see cref="BluelinkAccountCommandStateTests"/>; this class only checks that the derived
/// <c>IsEnabled</c> attributes agree with them.
/// </summary>
public class BluelinkAccountCommandTests
{
    private const string Kona = "TESTVIN0000000001";
    private const string Ioniq = "TESTVIN0000000002";

    private static async Task<(BluelinkAccount Account, FakeHttpMessageHandler Handler, TestTimeProvider Time)> CreatePolledAsync(string pin = "", RecordingLogger? logger = null)
    {
        var (account, handler, time) = TestAccount.Create(pin: pin, loggerFactory: logger is null ? null : new RecordingLoggerFactory(logger));
        await account.PollOnceAsync(CancellationToken.None);
        return (account, handler, time);
    }

    [Fact]
    public async Task WhenLegacyChargingStarts_ThenCommandIsSentResultAwaitedAndStatusReadOnce()
    {
        // Arrange
        var (account, handler, time) = await CreatePolledAsync();
        handler.Respond(BluelinkResponses.LegacyChargePath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Command("MSG-7"))
            .Respond(BluelinkResponses.RecordsPath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Records("MSG-7", "success"));

        // Act
        await TestAccount.AdvanceUntilCompletedAsync(time, account.Vehicles[Kona].Charge.StartChargingAsync(CancellationToken.None));

        // Assert
        Assert.Equal("""{"action":"start","deviceId":"device-1"}""", handler.Requests.Single(request => request.Uri.AbsolutePath == BluelinkResponses.LegacyChargePath("VEHICLE-1")).Body);
        Assert.Equal(1, handler.Count(BluelinkResponses.RecordsPath("VEHICLE-1")));
        Assert.Equal(2, handler.Count(BluelinkResponses.StatusPath("VEHICLE-1")));
        Assert.Equal(0, handler.Count(BluelinkResponses.PinPath));
        Assert.Equal(0, handler.Count(BluelinkResponses.LegacyStatusRequestPath("VEHICLE-1")));
    }

    [Fact]
    public async Task WhenResultIsPendingFirst_ThenItIsCheckedAgain()
    {
        // Arrange
        var (account, handler, time) = await CreatePolledAsync();
        handler.Respond(BluelinkResponses.LegacyChargePath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Command("MSG-7"))
            .Respond(BluelinkResponses.RecordsPath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Records("MSG-7", null))
            .Respond(BluelinkResponses.RecordsPath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Records("MSG-7", "success"));

        // Act
        await TestAccount.AdvanceUntilCompletedAsync(time, account.Vehicles[Kona].Charge.StopChargingAsync(CancellationToken.None));

        // Assert
        Assert.Equal(2, handler.Count(BluelinkResponses.RecordsPath("VEHICLE-1")));
    }

    [Fact]
    public async Task WhenVehicleRejectsTheCommand_ThenCommandExceptionIsThrown()
    {
        // Arrange
        var (account, handler, time) = await CreatePolledAsync();
        handler.Respond(BluelinkResponses.LegacyTemperaturePath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Command("MSG-7"))
            .Respond(BluelinkResponses.RecordsPath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Records("MSG-7", "fail"));

        // Act
        var exception = await Assert.ThrowsAsync<BluelinkCommandException>(() =>
            TestAccount.AdvanceUntilCompletedAsync(time, account.Vehicles[Kona].Climate.StartClimateAsync(21m, CancellationToken.None)));

        // Assert
        Assert.Equal(BluelinkCommandResult.Failed, exception.Result);
    }

    [Fact]
    public async Task WhenCommandFails_ThenOneWarningIsLogged()
    {
        // Arrange
        var logger = new RecordingLogger();
        var (account, handler, time) = await CreatePolledAsync(logger: logger);
        handler.Respond(BluelinkResponses.LegacyTemperaturePath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Command("MSG-7"))
            .Respond(BluelinkResponses.RecordsPath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Records("MSG-7", "fail"));
        var warningCount = logger.Warnings.Count;

        // Act
        await Assert.ThrowsAsync<BluelinkCommandException>(() =>
            TestAccount.AdvanceUntilCompletedAsync(time, account.Vehicles[Kona].Climate.StartClimateAsync(21m, CancellationToken.None)));

        // Assert
        Assert.Equal($"The Bluelink operation StartClimate of the vehicle {Kona} failed.", Assert.Single(logger.Warnings.Skip(warningCount)));
    }

    [Fact]
    public async Task WhenRefreshFails_ThenOneWarningIsLogged()
    {
        // Arrange
        var logger = new RecordingLogger();
        var (account, handler, time) = await CreatePolledAsync(logger: logger);
        handler.Respond(BluelinkResponses.LegacyStatusRequestPath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Failure("5031"));
        var warningCount = logger.Warnings.Count;

        // Act
        await Assert.ThrowsAsync<BluelinkApiException>(() => TestAccount.AdvanceUntilCompletedAsync(time, account.Vehicles[Kona].RefreshAsync(CancellationToken.None)));

        // Assert
        Assert.Equal($"The Bluelink operation Refresh of the vehicle {Kona} failed.", Assert.Single(logger.Warnings.Skip(warningCount)));
    }

    [Fact]
    public async Task WhenCallerCancelsTheCommand_ThenNothingIsLogged()
    {
        // Arrange
        var logger = new RecordingLogger();
        var (account, handler, _) = await CreatePolledAsync(logger: logger);
        handler.Respond(BluelinkResponses.LegacyChargePath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Command("MSG-7"));
        using var cancellation = new CancellationTokenSource();
        var command = account.Vehicles[Kona].Charge.StartChargingAsync(cancellation.Token);
        await AsyncTestHelpers.WaitUntilAsync(() => handler.Count(BluelinkResponses.LegacyChargePath("VEHICLE-1")) == 1);
        var warningCount = logger.Warnings.Count;

        // Act
        await cancellation.CancelAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command);
        Assert.Equal(warningCount, logger.Warnings.Count);
    }

    [Fact]
    public async Task WhenAccountIsNotConnected_ThenTheRejectedCommandIsNotLogged()
    {
        // Arrange
        var logger = new RecordingLogger();
        var (account, _, _) = await CreatePolledAsync(logger: logger);
        account.IsAuthenticated = false;
        var warningCount = logger.Warnings.Count;

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() => account.Vehicles[Kona].Charge.StartChargingAsync(CancellationToken.None));

        // Assert
        Assert.Equal(warningCount, logger.Warnings.Count);
    }

    [Fact]
    public async Task WhenCommandRequiresReauthentication_ThenOnlyTheAccountLogsIt()
    {
        // Arrange
        var logger = new RecordingLogger();
        var (account, handler, time) = await CreatePolledAsync(logger: logger);
        handler.Respond(BluelinkResponses.LegacyChargePath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Failure("7501"))
            .Reset(BluelinkResponses.RefreshPath)
            .Respond(BluelinkResponses.RefreshPath, HttpStatusCode.BadRequest, BluelinkResponses.CciError(4111));
        var warningCount = logger.Warnings.Count;

        // Act
        await Assert.ThrowsAsync<BluelinkReauthenticationRequiredException>(() =>
            TestAccount.AdvanceUntilCompletedAsync(time, account.Vehicles[Kona].Charge.StartChargingAsync(CancellationToken.None)));

        // Assert
        Assert.True(account.IsReauthenticationRequired);
        Assert.StartsWith("Bluelink re-authentication is required", Assert.Single(logger.Warnings.Skip(warningCount)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenVehicleIsBusy_ThenTheRejectionIsNotLogged()
    {
        // Arrange
        var logger = new RecordingLogger();
        var (account, handler, _) = await CreatePolledAsync(logger: logger);
        var heldResponse = TestAccount.CreateHeldResponse();
        handler.RespondAfter(BluelinkResponses.LegacyStatusRequestPath("VEHICLE-1"), heldResponse.Task);
        var kona = account.Vehicles[Kona];
        var refresh = kona.RefreshAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => handler.Count(BluelinkResponses.LegacyStatusRequestPath("VEHICLE-1")) == 1);
        var warningCount = logger.Warnings.Count;

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() => kona.Charge.StartChargingAsync(CancellationToken.None));

        // Assert
        Assert.Equal(warningCount, logger.Warnings.Count);
        heldResponse.SetResult(TestAccount.CreateResponse(HttpStatusCode.OK, BluelinkResponses.Success()));
        await refresh;
    }

    [Fact]
    public async Task WhenNoResultArrives_ThenCommandTimesOutAfterNineChecks()
    {
        // Arrange
        var (account, handler, time) = await CreatePolledAsync();
        handler.Respond(BluelinkResponses.LegacyTemperaturePath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Command("MSG-7"))
            .Respond(BluelinkResponses.RecordsPath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Records("MSG-7", null));
        var start = time.GetUtcNow();

        // Act
        var exception = await Assert.ThrowsAsync<BluelinkCommandException>(() =>
            TestAccount.AdvanceUntilCompletedAsync(time, account.Vehicles[Kona].Climate.StopClimateAsync(CancellationToken.None)));

        // Assert
        Assert.Equal(BluelinkCommandResult.TimedOut, exception.Result);
        Assert.Equal(9, handler.Count(BluelinkResponses.RecordsPath("VEHICLE-1")));
        Assert.True(time.GetUtcNow() - start >= TimeSpan.FromSeconds(90));
    }

    [Fact]
    public async Task WhenCcs2CommandHasNoPin_ThenItThrowsBeforeAnyCall()
    {
        // Arrange
        var (account, handler, _) = await CreatePolledAsync();
        var requestCount = handler.Requests.Count;

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => account.Vehicles[Ioniq].Charge.StartChargingAsync(CancellationToken.None));
        Assert.Equal(requestCount, handler.Requests.Count);
        Assert.False(account.Vehicles[Ioniq].Charge.StartCharging_IsEnabled);
    }

    [Fact]
    public async Task WhenClimateDurationIsInvalid_ThenClimateStartThrowsBeforeAnyCall()
    {
        // Arrange
        var (account, handler, _) = await CreatePolledAsync(pin: "1234");
        account.ClimateDuration = TimeSpan.FromMinutes(31);
        var requestCount = handler.Requests.Count;
        var status = account.Status;

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => account.Vehicles[Ioniq].Climate.StartClimateAsync(21m, CancellationToken.None));

        // Assert
        Assert.Equal("The climate duration of the account must be whole minutes from 1 to 30.", exception.Message);
        Assert.Equal(requestCount, handler.Requests.Count);
        Assert.Equal(0, handler.Count(BluelinkResponses.PinPath));
        Assert.Equal(status, account.Status);
    }

    [Fact]
    public async Task WhenCcs2CommandsAreSent_ThenTheControlTokenIsFetchedOnceAndReused()
    {
        // Arrange
        var (account, handler, time) = await CreatePolledAsync(pin: "1234");
        handler.Respond(BluelinkResponses.PinPath, HttpStatusCode.OK, """{"controlToken":"control-1","expiresTime":600}""")
            .Respond(BluelinkResponses.Ccs2ChargePath("VEHICLE-2"), HttpStatusCode.OK, BluelinkResponses.Command("MSG-7"))
            .Respond(BluelinkResponses.RecordsPath("VEHICLE-2"), HttpStatusCode.OK, BluelinkResponses.Records("MSG-7", "success"));
        var charge = account.Vehicles[Ioniq].Charge;

        // Act
        await TestAccount.AdvanceUntilCompletedAsync(time, charge.StartChargingAsync(CancellationToken.None));
        await TestAccount.AdvanceUntilCompletedAsync(time, charge.StopChargingAsync(CancellationToken.None));

        // Assert
        Assert.Equal(1, handler.Count(BluelinkResponses.PinPath));
        Assert.All(handler.Requests.Where(request => request.Uri.AbsolutePath == BluelinkResponses.Ccs2ChargePath("VEHICLE-2")),
            request => Assert.Equal("Bearer control-1", request.Header("Authorization")));
    }

    [Fact]
    public async Task WhenPinIsRejected_ThenCommandFailsWithoutRetry()
    {
        // Arrange
        var (account, handler, time) = await CreatePolledAsync(pin: "0000");
        handler.Respond(BluelinkResponses.PinPath, HttpStatusCode.BadRequest, """{"errCode":"4003","errMsg":"invalid pin"}""");

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestAccount.AdvanceUntilCompletedAsync(time, account.Vehicles[Ioniq].Climate.StartClimateAsync(21m, CancellationToken.None)));

        // Assert
        Assert.Contains("rejected the PIN", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.Count(BluelinkResponses.PinPath));
        Assert.Equal(0, handler.Count(BluelinkResponses.Ccs2TemperaturePath("VEHICLE-2")));
    }

    [Fact]
    public async Task WhenCcs2ChargeLimitsAreSetWithoutPin_ThenNoPinIsNeeded()
    {
        // Arrange
        var (account, handler, time) = await CreatePolledAsync();
        handler.Respond(BluelinkResponses.ChargeTargetPath("VEHICLE-2"), HttpStatusCode.OK, BluelinkResponses.Command("MSG-7"))
            .Respond(BluelinkResponses.RecordsPath("VEHICLE-2"), HttpStatusCode.OK, BluelinkResponses.Records("MSG-7", "success"));

        // Act
        await TestAccount.AdvanceUntilCompletedAsync(time, account.Vehicles[Ioniq].Charge.SetChargeLimitsAsync(0.8m, 0.9m, CancellationToken.None));

        // Assert
        Assert.Equal("""{"targetSOClist":[{"plugType":0,"targetSOClevel":90},{"plugType":1,"targetSOClevel":80}]}""",
            handler.Requests.Single(request => request.Uri.AbsolutePath == BluelinkResponses.ChargeTargetPath("VEHICLE-2")).Body);
        Assert.Equal(0, handler.Count(BluelinkResponses.PinPath));
        Assert.True(account.Vehicles[Ioniq].Charge.SetChargeLimits_IsEnabled);
    }

    [Fact]
    public async Task WhenAccountIsNotConnected_ThenCommandsAreDisabledAndThrowBeforeAnyCall()
    {
        // Arrange
        var (account, handler, _) = await CreatePolledAsync();
        var kona = account.Vehicles[Kona];
        account.IsAuthenticated = false;
        var requestCount = handler.Requests.Count;

        // Act & Assert
        Assert.False(kona.Charge.StartCharging_IsEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kona.Charge.StartChargingAsync(CancellationToken.None));
        Assert.Equal(requestCount, handler.Requests.Count);
    }

    [Fact]
    public async Task WhenBudgetIsSpent_ThenCommandsAreDisabled()
    {
        // Arrange
        var (account, _, _) = await CreatePolledAsync();
        var kona = account.Vehicles[Kona];
        Assert.True(kona.Charge.StartCharging_IsEnabled);

        // Act
        account.DailyRequestLimit = account.RequestsToday;

        // Assert
        Assert.False(kona.Charge.StartCharging_IsEnabled);
        Assert.False(kona.Refresh_IsEnabled);
    }

    [Fact]
    public async Task WhenLegacyVehicleIsRefreshed_ThenStatusIsRequestedAndReadAgain()
    {
        // Arrange
        var (account, handler, time) = await CreatePolledAsync();
        handler.Respond(BluelinkResponses.LegacyStatusRequestPath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Success());
        var kona = account.Vehicles[Kona];

        // Act
        await TestAccount.AdvanceUntilCompletedAsync(time, kona.RefreshAsync(CancellationToken.None));

        // Assert
        Assert.True(kona.Refresh_IsEnabled);
        Assert.Equal(1, handler.Count(BluelinkResponses.LegacyStatusRequestPath("VEHICLE-1")));
        Assert.Equal(2, handler.Count(BluelinkResponses.StatusPath("VEHICLE-1")));
    }

    [Fact]
    public async Task WhenCcs2VehicleIsRefreshed_ThenItRequestsThenWaitsTwentyFiveSecondsThenReads()
    {
        // Arrange
        var (account, handler, time) = await CreatePolledAsync();
        handler.Respond(BluelinkResponses.Ccs2StatusRequestPath("VEHICLE-2"), HttpStatusCode.OK, BluelinkResponses.Success());

        // Act
        var refresh = account.Vehicles[Ioniq].RefreshAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, handler.Count(BluelinkResponses.Ccs2StatusRequestPath("VEHICLE-2")));
        Assert.Equal(1, handler.Count(BluelinkResponses.StatusPath("VEHICLE-2")));

        time.Advance(TimeSpan.FromSeconds(24));
        Assert.False(refresh.IsCompleted);
        Assert.Equal(1, handler.Count(BluelinkResponses.StatusPath("VEHICLE-2")));

        time.Advance(TimeSpan.FromSeconds(1));
        await refresh;
        Assert.Equal(2, handler.Count(BluelinkResponses.StatusPath("VEHICLE-2")));
    }

    [Fact]
    public async Task WhenAuxiliaryBatteryIsBelowTheMinimum_ThenRefreshIsDisabled()
    {
        // Arrange
        var (account, _, _) = await CreatePolledAsync();
        var kona = account.Vehicles[Kona];
        Assert.True(kona.Refresh_IsEnabled);

        // Act
        account.MinimumAuxiliaryBatteryLevel = 0.9m;

        // Assert
        Assert.False(kona.Refresh_IsEnabled);
    }

    [Fact]
    public async Task WhenCommandIsRateLimited_ThenAccountPausesAndLaterCommandsFailFast()
    {
        // Arrange
        var (account, handler, _) = await CreatePolledAsync();
        handler.Respond(BluelinkResponses.LegacyChargePath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Failure("5091"));
        var charge = account.Vehicles[Kona].Charge;
        await Assert.ThrowsAsync<BluelinkApiException>(() => charge.StartChargingAsync(CancellationToken.None));
        var requestCount = handler.Requests.Count;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => charge.StartChargingAsync(CancellationToken.None));
        Assert.Contains("paused", exception.Message, StringComparison.Ordinal);
        Assert.Equal(requestCount, handler.Requests.Count);
        Assert.Equal(ServiceStatus.Error, account.Status);
    }
}
