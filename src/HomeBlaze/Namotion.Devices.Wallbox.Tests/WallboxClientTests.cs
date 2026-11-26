using System.Net;
using System.Text;
using Xunit;

namespace Namotion.Devices.Wallbox.Tests;

public class WallboxClientTests
{
    // The start of a status reply of a Pulsar MAX with firmware 6.14.7 during a session, which reports no added range.
    private const string StatusWithoutAddedRange =
        """
        {"user_id":1,"user_name":"default","car_id":1,"car_plate":"","depot_price":0.3,"last_sync":"2026-11-23 21:01:16",
        "power_sharing_status":1,"mid_status":1,"status_id":194,"name":"Pulsar MAX Bastelraum","charging_power":7.6951,
        "max_available_power":16,"depot_name":"Family","charging_speed":0,"added_range":null,"added_energy":21.757,
        "added_green_energy":0,"added_discharged_energy":0,"added_grid_energy":21.757,"charging_time":10257,"finished":false,
        "cost":0,"current_mode":3,"preventive_discharge":false,"state_of_charge":null,"grid":{"status":0},"pru":{"status":0},
        "ocpp_status":1}
        """;

    // A reply in which every value of the session and of the power is null.
    private const string StatusWithNullValues =
        """
        {"status_id":161,"name":"Pulsar MAX","charging_power":null,"max_available_power":null,"charging_speed":null,
        "added_range":null,"added_energy":null,"added_green_energy":null,"added_discharged_energy":null,
        "added_grid_energy":null,"charging_time":null,"finished":null,"cost":null,"current_mode":null,
        "state_of_charge":null,"depot_price":null}
        """;

    [Fact]
    public async Task WhenStatusHasNoAddedRange_ThenTheOtherValuesAreRead()
    {
        // Arrange
        var client = CreateClient(StatusWithoutAddedRange);

        // Act
        var status = await client.GetChargerStatusAsync("669749", CancellationToken.None);

        // Assert
        Assert.Null(status.AddedRange);
        Assert.Equal(194, status.StatusId);
        Assert.Equal(7.6951m, status.ChargingPowerInKw);
        Assert.Equal(21.757m, status.AddedEnergy);
        Assert.Equal(10257, status.ChargingTime);
        Assert.Equal(3, status.CurrentMode);
        Assert.Null(status.StateOfCharge);
    }

    [Fact]
    public async Task WhenStatusHasNullValues_ThenTheyAreReadAsMissing()
    {
        // Arrange
        var client = CreateClient(StatusWithNullValues);

        // Act
        var status = await client.GetChargerStatusAsync("669749", CancellationToken.None);

        // Assert
        Assert.Equal(161, status.StatusId);
        Assert.Null(status.ChargingPowerInKw);
        Assert.Null(status.MaxAvailablePower);
        Assert.Null(status.ChargingCurrent);
        Assert.Null(status.AddedEnergy);
        Assert.Null(status.AddedGreenEnergy);
        Assert.Null(status.AddedDischargedEnergy);
        Assert.Null(status.AddedGridEnergy);
        Assert.Null(status.ChargingTime);
        Assert.Null(status.IsFinished);
        Assert.Null(status.Cost);
        Assert.Null(status.CurrentMode);
        Assert.Null(status.DepotPrice);
    }

    private static WallboxClient CreateClient(string statusJson)
    {
        var tokenExpiration = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();
        var signInJson = """{"data":{"attributes":{"token":"test-token","ttl":""" + tokenExpiration + "}}}";
        var handler = new StubHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            "https://user-api.wall-box.com/users/signin" => signInJson,
            "https://api.wall-box.com/chargers/status/669749" => statusJson,
            var uri => throw new InvalidOperationException($"Unexpected request to {uri}.")
        });

        return new WallboxClient(new StubHttpClientFactory(handler), "user@example.com", "password");
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request), Encoding.UTF8, "application/json")
            });
        }
    }
}
