using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Namotion.Devices.Tesla.Client;
using Namotion.Devices.Tesla.Tests.Testing;

namespace Namotion.Devices.Tesla.Tests;

public class TeslaAccountConnectorTests
{
    private const string TokenPath = "/oauth2/v3/token";
    private const string ProductsPath = "/api/1/products";

    private static readonly DateTimeOffset Start = new(2026, 11, 21, 20, 0, 0, TimeSpan.Zero);

    private static string TokenJson(string accessToken, string refreshToken) =>
        $$"""{"access_token":"{{accessToken}}","refresh_token":"{{refreshToken}}","id_token":"{{IdToken("owner@example.com")}}","expires_in":28800,"token_type":"Bearer"}""";

    private static string IdToken(string email)
    {
        static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Encode("{\"alg\":\"none\"}") + "." + Encode($$"""{"email":"{{email}}"}""") + ".";
    }

    [Fact]
    public async Task WhenConnectingWithARefreshToken_ThenTheRotatedTokenAndTheVehiclesAreReturned()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler()
            .Respond(TokenPath, HttpStatusCode.OK, TokenJson("access-1", "refresh-2"))
            .Respond(ProductsPath, HttpStatusCode.OK, FixtureLoader.Load("products.json"));
        using var httpClient = new HttpClient(handler);

        // Act
        var connection = await TeslaAccountConnector.ConnectWithRefreshTokenAsync(
            httpClient, "  refresh-1 \n", new FakeTimeProvider(Start), CancellationToken.None);

        // Assert
        Assert.Equal("refresh-2", connection.RefreshToken);
        Assert.Equal("owner@example.com", connection.Email);
        var vehicle = Assert.Single(connection.Vehicles);
        Assert.Equal("TESTVIN0000000001", vehicle.Vin);
        Assert.Null(vehicle.DisplayName);
        Assert.Equal("online", vehicle.State);

        Assert.Contains("refresh_token=refresh-1&", handler.Requests[0].Body);
        Assert.Equal("Bearer access-1", handler.Requests[1].Authorization);
        Assert.DoesNotContain(handler.Requests, request => request.PathAndQuery.Contains("wake_up"));
    }

    [Fact]
    public async Task WhenConnectingWithAPastedCallback_ThenTheCodeIsExchangedWithTheVerifierOfTheLogin()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler()
            .Respond(TokenPath, HttpStatusCode.OK, TokenJson("access-1", "refresh-1"))
            .Respond(ProductsPath, HttpStatusCode.OK, """{"response":[]}""");
        using var httpClient = new HttpClient(handler);
        var login = TeslaAuthClient.CreateLogin();

        // Act
        var connection = await TeslaAccountConnector.ConnectWithLoginAsync(
            httpClient, login, $"tesla://auth/callback?code=the-code&state={login.State}&issuer=x", new FakeTimeProvider(Start), CancellationToken.None);

        // Assert
        Assert.Equal("refresh-1", connection.RefreshToken);
        Assert.Empty(connection.Vehicles);
        var body = handler.Requests[0].Body!;
        Assert.Contains("grant_type=authorization_code", body);
        Assert.Contains("code=the-code", body);
        Assert.Contains("code_verifier=" + login.CodeVerifier, body);
    }

    [Fact]
    public async Task WhenTheVehiclesCannotBeListed_ThenTheRotatedTokenIsStillReturned()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler()
            .Respond(TokenPath, HttpStatusCode.OK, TokenJson("access-1", "refresh-2"))
            .Respond(ProductsPath, HttpStatusCode.ServiceUnavailable, """{"error":"upstream"}""");
        using var httpClient = new HttpClient(handler);

        // Act
        var connection = await TeslaAccountConnector.ConnectWithRefreshTokenAsync(
            httpClient, "refresh-1", new FakeTimeProvider(Start), CancellationToken.None);

        // Assert
        Assert.Equal("refresh-2", connection.RefreshToken);
        Assert.Empty(connection.Vehicles);
        Assert.Contains("503", connection.VehicleListError);
    }

    [Fact]
    public async Task WhenThePastedCallbackBelongsToAnotherLogin_ThenNothingIsSent()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var login = TeslaAuthClient.CreateLogin();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => TeslaAccountConnector.ConnectWithLoginAsync(
            httpClient, login, "tesla://auth/callback?code=the-code&state=other", new FakeTimeProvider(Start), CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void WhenAConnectionIsPrinted_ThenTheRefreshTokenIsLeftOut()
    {
        // Arrange
        var connection = new TeslaConnection("secret-refresh-token", "owner@example.com", []);

        // Act
        var text = connection.ToString();

        // Assert
        Assert.DoesNotContain("secret-refresh-token", text);
        Assert.Contains("owner@example.com", text);
    }
}
