using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Namotion.Devices.Hyundai.Client;
using Namotion.Devices.Hyundai.Tests.Testing;

namespace Namotion.Devices.Hyundai.Tests;

public class BluelinkVehicleApiClientTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1700000000);

    private static readonly BluelinkVehicleRequest LegacyRequest = new("ccs-1", "device-1", "VEHICLE-1", BluelinkProtocol.Legacy);
    private static readonly BluelinkVehicleRequest Ccs2Request = new("ccs-1", "device-1", "VEHICLE-2", BluelinkProtocol.Ccs2);

    private static (BluelinkVehicleApiClient Client, FakeHttpMessageHandler Handler, BluelinkRequestBudget Budget) Create(FakeHttpMessageHandler handler, int limit = 100)
    {
        var time = new FakeTimeProvider(Now);
        var budget = new BluelinkRequestBudget(time, () => limit);
        return (new BluelinkVehicleApiClient(new HttpClient(handler), time, budget), handler, budget);
    }

    [Fact]
    public async Task WhenVehiclesAreListed_ThenTypeAndProtocolAreRead()
    {
        // Arrange
        var (client, _, _) = Create(new FakeHttpMessageHandler().Respond(BluelinkResponses.VehiclesPath, HttpStatusCode.OK, FixtureLoader.Load("vehicles.json")));

        // Act
        var vehicles = await client.GetVehiclesAsync("ccs-1", "device-1", CancellationToken.None);

        // Assert
        Assert.Equal(3, vehicles.Count);
        Assert.Equal(new BluelinkVehicleInfo("VEHICLE-1", "TESTVIN0000000001", "Kona", "KONA", "EV", BluelinkProtocol.Legacy), vehicles[0]);
        Assert.Equal(BluelinkProtocol.Ccs2, vehicles[1].Protocol);
        Assert.False(vehicles[2].IsElectric);
    }

    [Fact]
    public async Task WhenRequestIsSent_ThenStampAndServiceHeadersAreSetAndCounted()
    {
        // Arrange
        var (client, handler, budget) = Create(new FakeHttpMessageHandler().Respond(BluelinkResponses.VehiclesPath, HttpStatusCode.OK, FixtureLoader.Load("vehicles.json")));

        // Act
        await client.GetVehiclesAsync("ccs-1", "device-1", CancellationToken.None);

        // Assert
        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer ccs-1", request.Header("Authorization"));
        Assert.Equal("6d477c38-3ca4-4cf3-9557-2a1929a94654", request.Header("ccsp-service-id"));
        Assert.Equal("014d2225-8495-4735-812d-2616334fd15d", request.Header("ccsp-application-id"));
        Assert.Equal(BluelinkStamp.Create(Now), request.Header("Stamp"));
        Assert.Equal("device-1", request.Header("ccsp-device-id"));
        Assert.Equal("0", request.Header("Ccuccs2protocolsupport"));
        Assert.Equal(1, budget.Update());
    }

    [Fact]
    public async Task WhenRetCodeIsFailure_ThenExceptionCarriesTheResCode()
    {
        // Arrange
        var (client, _, _) = Create(new FakeHttpMessageHandler().Respond(BluelinkResponses.VehiclesPath, HttpStatusCode.OK, BluelinkResponses.Failure("5091", "Exceeds number of requests")));

        // Act
        var exception = await Assert.ThrowsAsync<BluelinkApiException>(() => client.GetVehiclesAsync("ccs-1", "device-1", CancellationToken.None));

        // Assert
        Assert.True(exception.IsRateLimited);
        Assert.Equal("Hyundai's request limit was reached (5091).", exception.Message);
    }

    [Fact]
    public async Task WhenErrorSaysKeyNotAuthorized_ThenTokenIsExpired()
    {
        // Arrange
        var (client, _, _) = Create(new FakeHttpMessageHandler().Respond(BluelinkResponses.VehiclesPath, HttpStatusCode.Unauthorized, """{"error":"Key not authorized: Token is expired"}"""));

        // Act
        var exception = await Assert.ThrowsAsync<BluelinkApiException>(() => client.GetVehiclesAsync("ccs-1", "device-1", CancellationToken.None));

        // Assert
        Assert.True(exception.IsTokenExpired);
    }

    [Fact]
    public async Task WhenBudgetIsExhausted_ThenNoRequestIsSent()
    {
        // Arrange
        var (client, handler, _) = Create(new FakeHttpMessageHandler(), limit: 0);

        // Act & Assert
        await Assert.ThrowsAsync<BluelinkRequestLimitException>(() => client.GetVehiclesAsync("ccs-1", "device-1", CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task WhenStoredStatusIsRead_ThenVehicleStateIsReturned()
    {
        // Arrange
        var (client, _, _) = Create(new FakeHttpMessageHandler().Respond(BluelinkResponses.StatusPath("VEHICLE-1"), HttpStatusCode.OK,
            BluelinkResponses.Success("""{"state":{"Vehicle":{"Drivetrain":{"Odometer":16773.9}}}}""")));

        // Act
        var state = await client.GetStoredStatusAsync(LegacyRequest, CancellationToken.None);

        // Assert
        Assert.Equal(16773.9m, state.GetDecimal("Drivetrain.Odometer"));
    }

    [Fact]
    public async Task WhenParkLocationIsNested_ThenCoordinateIsRead()
    {
        // Arrange
        var (client, _, _) = Create(new FakeHttpMessageHandler().Respond(BluelinkResponses.ParkLocationPath("VEHICLE-1"), HttpStatusCode.OK, FixtureLoader.Load("location-park.json")));

        // Act
        var location = await client.GetParkLocationAsync(LegacyRequest, CancellationToken.None);

        // Assert
        Assert.Equal(1.234567, location!.Value.Latitude, 6);
        Assert.Equal(2.345678, location.Value.Longitude, 6);
    }

    [Fact]
    public async Task WhenLegacyChargingStarts_ThenActionAndDeviceIdAreSentWithTheAccessToken()
    {
        // Arrange
        var (client, handler, _) = Create(new FakeHttpMessageHandler().Respond(BluelinkResponses.LegacyChargePath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Command("MSG-7")));

        // Act
        var messageId = await client.SetChargingAsync(LegacyRequest, start: true, controlToken: null, CancellationToken.None);

        // Assert
        Assert.Equal("MSG-7", messageId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("""{"action":"start","deviceId":"device-1"}""", request.Body);
        Assert.Equal("Bearer ccs-1", request.Header("Authorization"));
    }

    [Fact]
    public async Task WhenCcs2ChargingStops_ThenCommandIsSentWithTheControlToken()
    {
        // Arrange
        var (client, handler, _) = Create(new FakeHttpMessageHandler().Respond(BluelinkResponses.Ccs2ChargePath("VEHICLE-2"), HttpStatusCode.OK, BluelinkResponses.Command("MSG-8")));

        // Act
        await client.SetChargingAsync(Ccs2Request, start: false, controlToken: "control-1", CancellationToken.None);

        // Assert
        var request = Assert.Single(handler.Requests);
        Assert.Equal("""{"command":"stop"}""", request.Body);
        Assert.Equal("Bearer control-1", request.Header("Authorization"));
        Assert.Equal("Bearer control-1", request.Header("AuthorizationCCSP"));
        Assert.Equal("1", request.Header("Ccuccs2protocolsupport"));
    }

    [Fact]
    public async Task WhenLegacyClimateStarts_ThenTemperatureCodeAndDurationAreSent()
    {
        // Arrange
        var (client, handler, _) = Create(new FakeHttpMessageHandler().Respond(BluelinkResponses.LegacyTemperaturePath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Command("MSG-9")));

        // Act
        await client.StartClimateAsync(LegacyRequest, 21m, defrost: true, TimeSpan.FromMinutes(10), controlToken: null, CancellationToken.None);

        // Assert
        Assert.Equal("""{"action":"start","hvacType":0,"options":{"defrost":true,"heating1":0,"igniOnDuration":10},"tempCode":"0EH","unit":"C"}""", Assert.Single(handler.Requests).Body);
    }

    [Fact]
    public async Task WhenCcs2ClimateStarts_ThenTemperatureIsSentAsNumber()
    {
        // Arrange
        var (client, handler, _) = Create(new FakeHttpMessageHandler().Respond(BluelinkResponses.Ccs2TemperaturePath("VEHICLE-2"), HttpStatusCode.OK, BluelinkResponses.Command("MSG-10")));

        // Act
        await client.StartClimateAsync(Ccs2Request, 21.5m, defrost: false, TimeSpan.FromMinutes(10), controlToken: "control-1", CancellationToken.None);

        // Assert
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal("start", body.RootElement.GetProperty("command").GetString());
        Assert.Equal(21.5m, body.RootElement.GetProperty("hvacTemp").GetDecimal());
        Assert.Equal(10, body.RootElement.GetProperty("ignitionDuration").GetInt32());
        Assert.False(body.RootElement.GetProperty("windshieldFrontDefogState").GetBoolean());
    }

    [Fact]
    public async Task WhenChargeTargetsAreSet_ThenDcAndAcAreSent()
    {
        // Arrange
        var (client, handler, _) = Create(new FakeHttpMessageHandler().Respond(BluelinkResponses.ChargeTargetPath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Command("MSG-11")));

        // Act
        await client.SetChargeTargetsAsync(LegacyRequest, acPercent: 80, dcPercent: 90, CancellationToken.None);

        // Assert
        Assert.Equal("""{"targetSOClist":[{"plugType":0,"targetSOClevel":90},{"plugType":1,"targetSOClevel":80}]}""", Assert.Single(handler.Requests).Body);
    }

    [Theory]
    [InlineData("success", BluelinkCommandResult.Success)]
    [InlineData("fail", BluelinkCommandResult.Failed)]
    [InlineData("non-response", BluelinkCommandResult.NoResponse)]
    [InlineData(null, BluelinkCommandResult.Pending)]
    public async Task WhenRecordMatchesTheMessage_ThenItsResultIsRead(string? result, BluelinkCommandResult expected)
    {
        // Arrange
        var (client, _, _) = Create(new FakeHttpMessageHandler().Respond(BluelinkResponses.RecordsPath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Records("MSG-7", result)));

        // Act
        var actual = await client.GetCommandResultAsync(LegacyRequest, "MSG-7", CancellationToken.None);

        // Assert
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task WhenPinIsAccepted_ThenControlTokenAndExpiryAreReturned()
    {
        // Arrange
        var (client, handler, _) = Create(new FakeHttpMessageHandler().Respond(BluelinkResponses.PinPath, HttpStatusCode.OK, """{"controlToken":"control-1","expiresTime":600}"""));

        // Act
        var token = await client.GetControlTokenAsync("ccs-1", "device-1", "1234", CancellationToken.None);

        // Assert
        Assert.Equal("control-1", token.AccessToken);
        Assert.Equal(Now.AddSeconds(600), token.ExpiresAt);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("""{"deviceId":"device-1","pin":"1234"}""", request.Body);
    }

    [Fact]
    public async Task WhenPinIsRejected_ThenExceptionSaysSo()
    {
        // Arrange
        var (client, _, _) = Create(new FakeHttpMessageHandler().Respond(BluelinkResponses.PinPath, HttpStatusCode.BadRequest, """{"errId":"x","errCode":"4003","errMsg":"invalid pin"}"""));

        // Act
        var exception = await Assert.ThrowsAsync<BluelinkApiException>(() => client.GetControlTokenAsync("ccs-1", "device-1", "0000", CancellationToken.None));

        // Assert
        Assert.Contains("PIN", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenDeviceIsRegistered_ThenTheDeviceIdIsReturnedWithoutAccessToken()
    {
        // Arrange
        var (client, handler, _) = Create(new FakeHttpMessageHandler().Respond(BluelinkResponses.RegisterPath, HttpStatusCode.OK, BluelinkResponses.Success("""{"deviceId":"device-9"}""")));

        // Act
        var deviceId = await client.RegisterDeviceAsync(CancellationToken.None);

        // Assert
        Assert.Equal("device-9", deviceId);
        var request = Assert.Single(handler.Requests);
        Assert.Null(request.Header("Authorization"));
        Assert.Contains("\"pushType\":\"GCM\"", request.Body, StringComparison.Ordinal);
    }
}
