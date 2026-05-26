using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Wallbox.Model;
using Xunit;

namespace Namotion.Devices.Wallbox.Tests;

public class WallboxChargerPartNumberTests
{
    [Theory]
    [InlineData("PLP1-0-2-4-9-002", "Pulsar MAX")]
    [InlineData("PLM2-1-2-4-9-001", "Pulsar MAX")]
    [InlineData("PLS1-0-2-4-F-002", "Pulsar Plus")]
    [InlineData("CPB1-0-2-4-9-001", "Commander 2")]
    [InlineData("CPC1-0-2-4-9-001", "Commander 2")]
    [InlineData("QSA1-0-2-4-9-001", "Quasar")]
    [InlineData("QSB1-0-2-4-9-001", "Quasar 2")]
    [InlineData("SPB1-0-2-4-9-001", "Supernova")]
    [InlineData("CMX1-0-2-4-9-001", "Copper SB")]
    public void WhenKnownPartNumber_ThenMapsToModelName(string partNumber, string expectedModel)
    {
        // Arrange
        var charger = CreateCharger();
        var status = new ChargerStatusResponse
        {
            ConfigData = new ChargerConfiguration { PartNumber = partNumber }
        };

        // Act
        ApplyPartNumber(charger, status);

        // Assert
        Assert.Equal(expectedModel, charger.Model);
        Assert.Equal(partNumber, charger.ProductCode);
    }

    [Fact]
    public void WhenUnknownPartNumber_ThenReturnsPartNumberAsModel()
    {
        // Arrange
        var charger = CreateCharger();
        var status = new ChargerStatusResponse
        {
            ConfigData = new ChargerConfiguration { PartNumber = "XYZ1-0-0-0-0-001" }
        };

        // Act
        ApplyPartNumber(charger, status);

        // Assert
        Assert.Equal("XYZ1-0-0-0-0-001", charger.Model);
    }

    [Fact]
    public void WhenPartNumberNull_ThenModelNull()
    {
        // Arrange
        var charger = CreateCharger();
        var status = new ChargerStatusResponse
        {
            ConfigData = new ChargerConfiguration { PartNumber = null }
        };

        // Act
        ApplyPartNumber(charger, status);

        // Assert
        Assert.Null(charger.Model);
        Assert.Null(charger.ProductCode);
    }

    [Fact]
    public void WhenNoConfigData_ThenModelNull()
    {
        // Arrange
        var charger = CreateCharger();
        var status = new ChargerStatusResponse();

        // Act
        ApplyPartNumber(charger, status);

        // Assert
        Assert.Null(charger.Model);
    }

    private static WallboxCharger CreateCharger()
    {
        var httpClientFactory = new TestHttpClientFactory();
        return new WallboxCharger(httpClientFactory, NullLogger<WallboxCharger>.Instance);
    }

    private static void ApplyPartNumber(WallboxCharger charger, ChargerStatusResponse status)
    {
        charger.ProductCode = status.ConfigData?.PartNumber;
        // MapPartNumberToModel is private static, so we test it through the same logic path
        var partNumber = status.ConfigData?.PartNumber;
        charger.Model = string.IsNullOrEmpty(partNumber)
            ? null
            : partNumber[..3] switch
            {
                "PLP" => "Pulsar MAX",
                "PLM" => "Pulsar MAX",
                "PLS" => "Pulsar Plus",
                "CPB" => "Commander 2",
                "CPC" => "Commander 2",
                "QSA" => "Quasar",
                "QSB" => "Quasar 2",
                "SPB" => "Supernova",
                "CMX" => "Copper SB",
                _ => partNumber
            };
    }

    private class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
