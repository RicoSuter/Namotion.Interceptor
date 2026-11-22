using System.Net;
using Microsoft.Extensions.Time.Testing;
using Namotion.Devices.Hyundai.Client;
using Namotion.Devices.Hyundai.Tests.Testing;

namespace Namotion.Devices.Hyundai.Tests;

public class BluelinkAccountConnectorTests
{
    private static FakeHttpMessageHandler CreateHandler(out IDisposable key)
    {
        var handler = new FakeHttpMessageHandler()
            .Respond(BluelinkResponses.RegisterPath, HttpStatusCode.OK, BluelinkResponses.Success("""{"deviceId":"device-1"}"""))
            .Respond(BluelinkResponses.ExchangePath, HttpStatusCode.OK, BluelinkResponses.Ccs(1))
            .Respond(BluelinkResponses.VehiclesPath, HttpStatusCode.OK, FixtureLoader.Load("vehicles.json"));
        key = LoginFixture.Respond(handler);
        return handler;
    }

    private static Task<BluelinkConnection> ConnectAsync(FakeHttpMessageHandler handler, string pin = "") =>
        BluelinkAccountConnector.ConnectAsync(new HttpClient(handler), " user@example.invalid ", "password-1", pin, new FakeTimeProvider(), CancellationToken.None);

    [Fact]
    public async Task WhenConnecting_ThenTokensDeviceAndVehiclesAreReturned()
    {
        // Arrange
        var handler = CreateHandler(out var key);
        using var _ = key;

        // Act
        var connection = await ConnectAsync(handler);

        // Assert
        Assert.Equal("user@example.invalid", connection.Email);
        Assert.Equal("device-1", connection.DeviceId);
        Assert.Equal(BluelinkResponses.TokenSet(1), BluelinkTokenSet.Parse(connection.Tokens));
        Assert.Equal([true, true, false], connection.Vehicles.Select(vehicle => vehicle.IsSupported));
        Assert.Equal(0, handler.Count(BluelinkResponses.PinPath));
        Assert.DoesNotContain("password-1", connection.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenPinIsGivenAndACcs2VehicleExists_ThenThePinIsChecked()
    {
        // Arrange
        var handler = CreateHandler(out var key);
        using var _ = key;
        handler.Respond(BluelinkResponses.PinPath, HttpStatusCode.OK, """{"controlToken":"control-1","expiresTime":600}""");

        // Act
        await ConnectAsync(handler, pin: "1234");

        // Assert
        Assert.Equal(1, handler.Count(BluelinkResponses.PinPath));
    }

    [Fact]
    public async Task WhenPinIsRejected_ThenConnectingFails()
    {
        // Arrange
        var handler = CreateHandler(out var key);
        using var _ = key;
        handler.Respond(BluelinkResponses.PinPath, HttpStatusCode.BadRequest, """{"errCode":"4003"}""");

        // Act & Assert
        var exception = await Assert.ThrowsAsync<BluelinkPinRejectedException>(() => ConnectAsync(handler, pin: "0000"));
        Assert.Contains("PIN", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenDeviceRegistrationFails_ThenARandomDeviceIdIsUsed()
    {
        // Arrange
        var handler = CreateHandler(out var key);
        using var _ = key;
        handler.Reset(BluelinkResponses.RegisterPath).Respond(BluelinkResponses.RegisterPath, HttpStatusCode.InternalServerError, "{}");

        // Act
        var connection = await ConnectAsync(handler);

        // Assert
        Assert.True(Guid.TryParse(connection.DeviceId, out var deviceId));
        Assert.NotEqual(Guid.Empty, deviceId);
    }
}
