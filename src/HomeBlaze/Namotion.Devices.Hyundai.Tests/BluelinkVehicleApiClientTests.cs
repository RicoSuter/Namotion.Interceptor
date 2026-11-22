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

    private static (BluelinkVehicleApiClient Client, FakeHttpMessageHandler Handler, BluelinkRequestBudget Budget) Create(string path, HttpStatusCode statusCode, string body) =>
        Create(new FakeHttpMessageHandler().Respond(path, statusCode, body));

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
        var (client, _, _) = Create(BluelinkResponses.VehiclesPath, HttpStatusCode.OK, FixtureLoader.Load("vehicles.json"));

        // Act
        var vehicles = await client.GetVehiclesAsync("ccs-1", "device-1", CancellationToken.None);

        // Assert
        Assert.Equal(3, vehicles.Count);
        Assert.Equal(new BluelinkVehicleInfo("VEHICLE-1", "TESTVIN0000000001", "Kona", "KONA", "EV", BluelinkProtocol.Legacy), vehicles[0]);
        Assert.Equal(BluelinkProtocol.Ccs2, vehicles[1].Protocol);
        Assert.False(vehicles[2].IsElectric);
    }

    [Fact]
    public async Task WhenNicknameAndModelAreBlank_ThenTheyAreNull()
    {
        // Arrange
        var (client, _, _) = Create(BluelinkResponses.VehiclesPath, HttpStatusCode.OK, BluelinkResponses.Success(
            """{"vehicles":[{"vin":"TESTVIN0000000001","vehicleId":"VEHICLE-1","vehicleName":"  ","type":"EV","nickname":" ","ccuCCS2ProtocolSupport":0}]}"""));

        // Act
        var vehicles = await client.GetVehiclesAsync("ccs-1", "device-1", CancellationToken.None);

        // Assert
        var vehicle = Assert.Single(vehicles);
        Assert.Null(vehicle.Nickname);
        Assert.Null(vehicle.ModelName);
    }

    [Fact]
    public async Task WhenRequestIsSent_ThenStampAndServiceHeadersAreSetAndCounted()
    {
        // Arrange
        var (client, handler, budget) = Create(BluelinkResponses.VehiclesPath, HttpStatusCode.OK, FixtureLoader.Load("vehicles.json"));

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
        var (client, _, _) = Create(BluelinkResponses.VehiclesPath, HttpStatusCode.OK, BluelinkResponses.Failure("5091", "Exceeds number of requests"));

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
        var (client, _, _) = Create(BluelinkResponses.VehiclesPath, HttpStatusCode.Unauthorized, """{"error":"Key not authorized: Token is expired"}""");

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
        var (client, _, _) = Create(BluelinkResponses.StatusPath("VEHICLE-1"), HttpStatusCode.OK,
            BluelinkResponses.Success("""{"state":{"Vehicle":{"Drivetrain":{"Odometer":16773.9}}}}"""));

        // Act
        var state = await client.GetStoredStatusAsync(LegacyRequest, CancellationToken.None);

        // Assert
        Assert.Equal(16773.9m, state.GetDecimal("Drivetrain.Odometer"));
    }

    [Fact]
    public async Task WhenParkLocationIsNested_ThenCoordinateIsRead()
    {
        // Arrange
        var (client, _, _) = Create(BluelinkResponses.ParkLocationPath("VEHICLE-1"), HttpStatusCode.OK, FixtureLoader.Load("location-park.json"));

        // Act
        var location = await client.GetParkLocationAsync(LegacyRequest, CancellationToken.None);

        // Assert
        Assert.Equal(1.234567, location!.Value.Latitude, 6);
        Assert.Equal(2.345678, location.Value.Longitude, 6);
    }

    [Fact]
    public async Task WhenParkLocationIsReadForCcs2_ThenProtocolHeaderIsZero()
    {
        // Arrange
        var (client, handler, _) = Create(BluelinkResponses.ParkLocationPath("VEHICLE-2"), HttpStatusCode.OK, FixtureLoader.Load("location-park.json"));

        // Act
        await client.GetParkLocationAsync(Ccs2Request, CancellationToken.None);

        // Assert
        Assert.Equal("0", Assert.Single(handler.Requests).Header("Ccuccs2protocolsupport"));
    }

    [Fact]
    public async Task WhenLegacyChargingStarts_ThenActionAndDeviceIdAreSentWithTheAccessToken()
    {
        // Arrange
        var (client, handler, _) = Create(BluelinkResponses.LegacyChargePath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Command("MSG-7"));

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
        var (client, handler, _) = Create(BluelinkResponses.Ccs2ChargePath("VEHICLE-2"), HttpStatusCode.OK, BluelinkResponses.Command("MSG-8"));

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
        var (client, handler, _) = Create(BluelinkResponses.LegacyTemperaturePath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Command("MSG-9"));

        // Act
        await client.StartClimateAsync(LegacyRequest, 21m, defrost: true, 10, controlToken: null, CancellationToken.None);

        // Assert
        Assert.Equal("""{"action":"start","hvacType":0,"options":{"defrost":true,"heating1":0,"igniOnDuration":10},"tempCode":"0EH","unit":"C"}""", Assert.Single(handler.Requests).Body);
    }

    [Fact]
    public async Task WhenCcs2ClimateStarts_ThenTemperatureIsSentAsNumber()
    {
        // Arrange
        var (client, handler, _) = Create(BluelinkResponses.Ccs2TemperaturePath("VEHICLE-2"), HttpStatusCode.OK, BluelinkResponses.Command("MSG-10"));

        // Act
        await client.StartClimateAsync(Ccs2Request, 21.5m, defrost: false, 10, controlToken: "control-1", CancellationToken.None);

        // Assert
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal("start", body.RootElement.GetProperty("command").GetString());
        Assert.Equal(21.5m, body.RootElement.GetProperty("hvacTemp").GetDecimal());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("strgWhlHeating").ValueKind);
        Assert.Equal(10, body.RootElement.GetProperty("ignitionDuration").GetInt32());
        Assert.False(body.RootElement.GetProperty("windshieldFrontDefogState").GetBoolean());
    }

    [Fact]
    public async Task WhenChargeTargetsAreSet_ThenDcAndAcAreSent()
    {
        // Arrange
        var (client, handler, _) = Create(BluelinkResponses.ChargeTargetPath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Command("MSG-11"));

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
        var (client, _, _) = Create(BluelinkResponses.RecordsPath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Records("MSG-7", result));

        // Act
        var actual = await client.GetCommandResultAsync(LegacyRequest, "MSG-7", CancellationToken.None);

        // Assert
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task WhenPinIsAccepted_ThenControlTokenAndExpiryAreReturned()
    {
        // Arrange
        var (client, handler, _) = Create(BluelinkResponses.PinPath, HttpStatusCode.OK, """{"controlToken":"control-1","expiresTime":600}""");

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
        var (client, _, _) = Create(BluelinkResponses.PinPath, HttpStatusCode.BadRequest, """{"errId":"x","errCode":"4003","errMsg":"invalid pin"}""");

        // Act
        var exception = await Assert.ThrowsAsync<BluelinkPinRejectedException>(() => client.GetControlTokenAsync("ccs-1", "device-1", "0000", CancellationToken.None));

        // Assert
        Assert.Equal("4003", exception.Code);
        Assert.Contains("PIN", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenPinCallSaysKeyNotAuthorized_ThenTokenIsExpiredAndPinIsNotRejected()
    {
        // Arrange
        var (client, _, _) = Create(BluelinkResponses.PinPath, HttpStatusCode.Unauthorized, """{"error":"Key not authorized: Token is expired"}""");

        // Act
        var exception = await Assert.ThrowsAsync<BluelinkApiException>(() => client.GetControlTokenAsync("ccs-1", "device-1", "1234", CancellationToken.None));

        // Assert
        Assert.True(exception.IsTokenExpired);
        Assert.IsNotType<BluelinkPinRejectedException>(exception);
    }

    [Fact]
    public async Task WhenPinCallFailsWithServerErrorAndNoJson_ThenItIsNotAPinRejection()
    {
        // Arrange
        var (client, _, _) = Create(BluelinkResponses.PinPath, HttpStatusCode.InternalServerError, "oops");

        // Act
        var exception = await Assert.ThrowsAsync<BluelinkApiException>(() => client.GetControlTokenAsync("ccs-1", "device-1", "1234", CancellationToken.None));

        // Assert
        Assert.IsNotType<BluelinkPinRejectedException>(exception);
        Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
    }

    [Fact]
    public async Task WhenPinCallReturnsFailureEnvelope_ThenResCodeIsCarriedAndPinIsNotRejected()
    {
        // Arrange
        var (client, _, _) = Create(BluelinkResponses.PinPath, HttpStatusCode.BadRequest, BluelinkResponses.Failure("5091", "limit"));

        // Act
        var exception = await Assert.ThrowsAsync<BluelinkApiException>(() => client.GetControlTokenAsync("ccs-1", "device-1", "1234", CancellationToken.None));

        // Assert
        Assert.True(exception.IsRateLimited);
        Assert.IsNotType<BluelinkPinRejectedException>(exception);
    }

    [Fact]
    public async Task WhenPinCallReturnsEmptyObjectWithSuccessStatus_ThenItThrowsWithoutPinRejection()
    {
        // Arrange
        var (client, _, _) = Create(BluelinkResponses.PinPath, HttpStatusCode.OK, "{}");

        // Act
        var exception = await Assert.ThrowsAsync<BluelinkApiException>(() => client.GetControlTokenAsync("ccs-1", "device-1", "1234", CancellationToken.None));

        // Assert
        Assert.IsNotType<BluelinkPinRejectedException>(exception);
    }

    [Theory]
    [InlineData("""{"controlToken":"control-1"}""", 600)]
    [InlineData("""{"controlToken":"control-1","expiresTime":0}""", 600)]
    [InlineData("""{"controlToken":"control-1","expiresTime":-5}""", 600)]
    [InlineData("""{"controlToken":"control-1","expiresTime":86401}""", 600)]
    [InlineData("""{"controlToken":"control-1","expiresTime":86400}""", 86400)]
    [InlineData("""{"controlToken":"control-1","expiresTime":1}""", 1)]
    public async Task WhenExpiresTimeIsMissingOrOutOfRange_ThenLifetimeIsClamped(string body, int expectedSeconds)
    {
        // Arrange
        var (client, _, _) = Create(BluelinkResponses.PinPath, HttpStatusCode.OK, body);

        // Act
        var token = await client.GetControlTokenAsync("ccs-1", "device-1", "1234", CancellationToken.None);

        // Assert
        Assert.Equal(Now.AddSeconds(expectedSeconds), token.ExpiresAt);
    }

    [Fact]
    public async Task WhenPinIsExchanged_ThenTokenQueryIsEmptyAndCallIsCounted()
    {
        // Arrange
        var (client, handler, budget) = Create(BluelinkResponses.PinPath, HttpStatusCode.OK, """{"controlToken":"control-1","expiresTime":600}""");

        // Act
        await client.GetControlTokenAsync("ccs-1", "device-1", "1234", CancellationToken.None);

        // Assert
        Assert.Equal("https://prd.eu-ccapi.hyundai.com:8080/api/v1/user/pin?token=", Assert.Single(handler.Requests).Uri.ToString());
        Assert.Equal(1, budget.Update());
    }

    [Fact]
    public async Task WhenVehiclesAreListed_ThenRequestGoesToTheVehicleApiHostAndPort()
    {
        // Arrange
        var (client, handler, _) = Create(BluelinkResponses.VehiclesPath, HttpStatusCode.OK, FixtureLoader.Load("vehicles.json"));

        // Act
        await client.GetVehiclesAsync("ccs-1", "device-1", CancellationToken.None);

        // Assert
        Assert.Equal("https://prd.eu-ccapi.hyundai.com:8080/api/v1/spa/vehicles", Assert.Single(handler.Requests).Uri.ToString());
    }

    [Fact]
    public async Task WhenResponseIsNotJson_ThenExceptionIsThrown()
    {
        // Arrange
        var (client, _, _) = Create(BluelinkResponses.VehiclesPath, HttpStatusCode.OK, "<html>");

        // Act & Assert
        await Assert.ThrowsAsync<BluelinkApiException>(() => client.GetVehiclesAsync("ccs-1", "device-1", CancellationToken.None));
    }

    [Fact]
    public async Task WhenStatusIsServerErrorWithSuccessRetCode_ThenExceptionIsThrown()
    {
        // Arrange
        var (client, _, _) = Create(BluelinkResponses.VehiclesPath, HttpStatusCode.InternalServerError, BluelinkResponses.Success());

        // Act
        var exception = await Assert.ThrowsAsync<BluelinkApiException>(() => client.GetVehiclesAsync("ccs-1", "device-1", CancellationToken.None));

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
    }

    [Fact]
    public async Task WhenTransportFails_ThenTheCallIsStillCounted()
    {
        // Arrange
        var (client, _, budget) = Create(new FakeHttpMessageHandler().Respond(BluelinkResponses.VehiclesPath, () => throw new HttpRequestException("offline")));

        // Act
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetVehiclesAsync("ccs-1", "device-1", CancellationToken.None));

        // Assert
        Assert.Equal(1, budget.Update());
    }

    [Fact]
    public async Task WhenNoRecordMatchesTheMessage_ThenResultIsPending()
    {
        // Arrange
        var (client, _, _) = Create(BluelinkResponses.RecordsPath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Records("MSG-7", "success"));

        // Act
        var actual = await client.GetCommandResultAsync(LegacyRequest, "MSG-OTHER", CancellationToken.None);

        // Assert
        Assert.Equal(BluelinkCommandResult.Pending, actual);
    }

    [Fact]
    public async Task WhenLegacyStatusIsRequested_ThenTheStatusPathIsCalled()
    {
        // Arrange
        var (client, handler, _) = Create(BluelinkResponses.LegacyStatusRequestPath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Success());

        // Act
        await client.RequestStatusAsync(LegacyRequest, CancellationToken.None);

        // Assert
        Assert.Equal(BluelinkResponses.LegacyStatusRequestPath("VEHICLE-1"), Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task WhenCcs2StatusIsRequested_ThenTheCarStatusPathIsCalled()
    {
        // Arrange
        var (client, handler, _) = Create(BluelinkResponses.Ccs2StatusRequestPath("VEHICLE-2"), HttpStatusCode.OK, BluelinkResponses.Success());

        // Act
        await client.RequestStatusAsync(Ccs2Request, CancellationToken.None);

        // Assert
        var request = Assert.Single(handler.Requests);
        Assert.Equal(BluelinkResponses.Ccs2StatusRequestPath("VEHICLE-2"), request.Uri.AbsolutePath);
        Assert.Equal("1", request.Header("Ccuccs2protocolsupport"));
    }

    [Fact]
    public async Task WhenLegacyClimateStops_ThenTheStopPayloadIsSent()
    {
        // Arrange
        var (client, handler, _) = Create(BluelinkResponses.LegacyTemperaturePath("VEHICLE-1"), HttpStatusCode.OK, BluelinkResponses.Command("MSG-12"));

        // Act
        var messageId = await client.StopClimateAsync(LegacyRequest, controlToken: null, CancellationToken.None);

        // Assert
        Assert.Equal("MSG-12", messageId);
        Assert.Equal("""{"action":"stop","hvacType":0,"options":{"defrost":true,"heating1":1},"tempCode":"10H","unit":"C"}""", Assert.Single(handler.Requests).Body);
    }

    [Fact]
    public async Task WhenCcs2ClimateStops_ThenCommandIsSentWithTheControlToken()
    {
        // Arrange
        var (client, handler, _) = Create(BluelinkResponses.Ccs2TemperaturePath("VEHICLE-2"), HttpStatusCode.OK, BluelinkResponses.Command("MSG-13"));

        // Act
        await client.StopClimateAsync(Ccs2Request, controlToken: "control-1", CancellationToken.None);

        // Assert
        var request = Assert.Single(handler.Requests);
        Assert.Equal("""{"command":"stop"}""", request.Body);
        Assert.Equal("Bearer control-1", request.Header("Authorization"));
        Assert.Equal("Bearer control-1", request.Header("AuthorizationCCSP"));
    }

    [Fact]
    public async Task WhenCcs2CommandHasNoControlToken_ThenItThrowsWithoutSendingOrCounting()
    {
        // Arrange
        var (client, handler, budget) = Create(new FakeHttpMessageHandler());

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.SetChargingAsync(Ccs2Request, start: true, controlToken: null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.StartClimateAsync(Ccs2Request, 21m, defrost: false, 10, controlToken: null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.StopClimateAsync(Ccs2Request, controlToken: null, CancellationToken.None));
        Assert.Empty(handler.Requests);
        Assert.Equal(0, budget.Update());
    }

    [Fact]
    public async Task WhenClimateDurationIsInvalid_ThenItThrowsWithoutSendingOrCounting()
    {
        // Arrange
        var (client, handler, budget) = Create(new FakeHttpMessageHandler());

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.StartClimateAsync(LegacyRequest, 21m, defrost: false, 31, controlToken: null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.StartClimateAsync(Ccs2Request, 21m, defrost: false, 0, controlToken: "control-1", CancellationToken.None));
        Assert.Empty(handler.Requests);
        Assert.Equal(0, budget.Update());
    }

    [Fact]
    public async Task WhenDeviceIsRegistered_ThenTheDeviceIdIsReturnedWithoutAccessToken()
    {
        // Arrange
        var (client, handler, _) = Create(BluelinkResponses.RegisterPath, HttpStatusCode.OK, BluelinkResponses.Success("""{"deviceId":"device-9"}"""));

        // Act
        var deviceId = await client.RegisterDeviceAsync(CancellationToken.None);

        // Assert
        Assert.Equal("device-9", deviceId);
        var request = Assert.Single(handler.Requests);
        Assert.Null(request.Header("Authorization"));
        Assert.Contains("\"pushType\":\"GCM\"", request.Body, StringComparison.Ordinal);
    }
}
