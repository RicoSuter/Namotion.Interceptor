using System.Net;
using Microsoft.Extensions.Time.Testing;
using Namotion.Devices.Tesla.Client;
using Namotion.Devices.Tesla.Tests.Testing;

namespace Namotion.Devices.Tesla.Tests;

public class TeslaOwnerApiClientTests
{
    [Fact]
    public async Task WhenProductsAreRead_ThenOnlyVehiclesAreReturned()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler().Respond("/api/1/products", HttpStatusCode.OK,
            """{"response":[{"id":1,"vehicle_id":2,"vin":"V1","state":"asleep"},{"id":9,"energy_site_id":5}]}""");
        using var httpClient = new HttpClient(handler);
        var client = new TeslaOwnerApiClient(httpClient, TimeProvider.System);

        // Act
        var vehicles = await client.GetVehicleProductsAsync("access", CancellationToken.None);

        // Assert
        Assert.Equal(1, Assert.Single(vehicles).Id);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("Bearer access", request.Authorization);
    }

    [Fact]
    public async Task WhenVehicleIsAwake_ThenVehicleDataIsReturned()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler().Respond("/api/1/vehicles/1/vehicle_data", HttpStatusCode.OK, FixtureLoader.Load("vehicle-data.json"));
        using var httpClient = new HttpClient(handler);
        var client = new TeslaOwnerApiClient(httpClient, TimeProvider.System);

        // Act
        var data = await client.GetVehicleDataAsync("access", 1, CancellationToken.None);

        // Assert
        Assert.Equal("TESTVIN0000000001", data!.Vin);
        Assert.Equal("Bearer access", handler.Requests.Single().Authorization);
    }

    [Fact]
    public async Task WhenVehicleIsAsleep_ThenVehicleDataReturnsNull()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler().Respond("/api/1/vehicles/1/vehicle_data", HttpStatusCode.RequestTimeout,
            """{"response":null,"error":"vehicle unavailable: vehicle is offline or asleep"}""");
        using var httpClient = new HttpClient(handler);
        var client = new TeslaOwnerApiClient(httpClient, TimeProvider.System);

        // Act
        var data = await client.GetVehicleDataAsync("access", 1, CancellationToken.None);

        // Assert
        Assert.Null(data);
        Assert.Contains("endpoints=charge_state%3Bclimate_state%3Bclosures_state%3Bdrive_state%3Bgui_settings%3Blocation_data%3Bvehicle_config%3Bvehicle_state%3Bvehicle_data_combo", handler.Requests.Single().PathAndQuery);
    }

    [Fact]
    public async Task WhenVehicleIsWokenUp_ThenWakeUpIsPosted()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler().Respond("/api/1/vehicles/1/wake_up", HttpStatusCode.OK, """{"response":{"state":"asleep"}}""");
        using var httpClient = new HttpClient(handler);
        var client = new TeslaOwnerApiClient(httpClient, TimeProvider.System);

        // Act
        await client.WakeUpAsync("access", 1, CancellationToken.None);

        // Assert
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("Bearer access", request.Authorization);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":"too many"}""", 120)]
    [InlineData(HttpStatusCode.Forbidden, """{"error":"account disabled: EXCEEDED_LIMIT"}""", 900)]
    public async Task WhenTeslaLimitsRequests_ThenRetryAfterIsReported(HttpStatusCode statusCode, string body, int expectedSeconds)
    {
        // Arrange
        var handler = new FakeHttpMessageHandler().Respond("/api/1/products", statusCode, body, ("Retry-After", "120"));
        using var httpClient = new HttpClient(handler);
        var client = new TeslaOwnerApiClient(httpClient, TimeProvider.System);

        // Act
        var exception = await Assert.ThrowsAsync<TeslaApiException>(() => client.GetVehicleProductsAsync("access", CancellationToken.None));

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), exception.RetryAfter);
    }

    [Theory]
    [InlineData("Sat, 21 Nov 2026 20:02:00 GMT", 120)]
    [InlineData("Sat, 21 Nov 2026 19:58:00 GMT", 0)]
    public async Task WhenRetryAfterIsADate_ThenTheTimeUntilThenIsReported(string retryAfter, int expectedSeconds)
    {
        // Arrange
        var handler = new FakeHttpMessageHandler().Respond("/api/1/products", HttpStatusCode.TooManyRequests, "{}", ("Retry-After", retryAfter));
        using var httpClient = new HttpClient(handler);
        var client = new TeslaOwnerApiClient(httpClient, new FakeTimeProvider(new DateTimeOffset(2026, 11, 21, 20, 0, 0, TimeSpan.Zero)));

        // Act
        var exception = await Assert.ThrowsAsync<TeslaApiException>(() => client.GetVehicleProductsAsync("access", CancellationToken.None));

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), exception.RetryAfter);
    }

    [Fact]
    public async Task WhenTooManyRequestsHasNoRetryAfter_ThenFiveMinutesAreReported()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler().Respond("/api/1/products", HttpStatusCode.TooManyRequests, "{}");
        using var httpClient = new HttpClient(handler);
        var client = new TeslaOwnerApiClient(httpClient, TimeProvider.System);

        // Act
        var exception = await Assert.ThrowsAsync<TeslaApiException>(() => client.GetVehicleProductsAsync("access", CancellationToken.None));

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(5), exception.RetryAfter);
    }

    [Fact]
    public async Task WhenAccessTokenIsRejected_ThenUnauthorizedIsReportedWithoutRetryAfter()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler().Respond("/api/1/products", HttpStatusCode.Unauthorized, """{"error":"invalid bearer token"}""");
        using var httpClient = new HttpClient(handler);
        var client = new TeslaOwnerApiClient(httpClient, TimeProvider.System);

        // Act
        var exception = await Assert.ThrowsAsync<TeslaApiException>(() => client.GetVehicleProductsAsync("access", CancellationToken.None));

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal("invalid bearer token", exception.Error);
        Assert.Null(exception.RetryAfter);
    }

    [Fact]
    public async Task WhenErrorBodyIsNotJson_ThenItIsNotInTheMessage()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler().Respond("/api/1/products", HttpStatusCode.BadGateway, "<html>access upstream</html>");
        using var httpClient = new HttpClient(handler);
        var client = new TeslaOwnerApiClient(httpClient, TimeProvider.System);

        // Act
        var exception = await Assert.ThrowsAsync<TeslaApiException>(() => client.GetVehicleProductsAsync("access", CancellationToken.None));

        // Assert
        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.Null(exception.Error);
        Assert.DoesNotContain("html", exception.Message);
    }

    [Fact]
    public async Task WhenRequestIsSent_ThenTheHttpVersionOfTheClientIsUsed()
    {
        // Arrange
        var handler = new VersionRecordingHandler();
        using var httpClient = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        var client = new TeslaOwnerApiClient(httpClient, TimeProvider.System);

        // Act
        await client.GetVehicleProductsAsync("access", CancellationToken.None);

        // Assert
        Assert.Equal(HttpVersion.Version20, handler.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionExact, handler.VersionPolicy);
    }

    private sealed class VersionRecordingHandler : HttpMessageHandler
    {
        public Version? Version { get; private set; }

        public HttpVersionPolicy? VersionPolicy { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Version = request.Version;
            VersionPolicy = request.VersionPolicy;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"response":[]}""") });
        }
    }
}
