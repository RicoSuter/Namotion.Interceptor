using System.Text.Json;
using Xunit;

namespace Namotion.Devices.Ecowitt.Tests;

public class EcowittClientTests
{
    [Fact]
    public void WhenCommonListHasMetricValues_ThenParsesOutdoorData()
    {
        // Arrange
        var json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "common_list": [
                {"id":"0x02","val":"18.1","unit":"℃"},
                {"id":"0x07","val":"44%"},
                {"id":"0x03","val":"5.7","unit":"℃"},
                {"id":"3","val":"13.1","unit":"℃"},
                {"id":"0x0B","val":"1.8 m/s"},
                {"id":"0x0C","val":"3.6 m/s"},
                {"id":"0x19","val":"9.4 m/s"},
                {"id":"0x15","val":"0.50 Klux"},
                {"id":"0x17","val":"3"},
                {"id":"0x0A","val":"99"}
            ]
        }
        """);

        // Act
        var data = EcowittClient.ParseLiveData(json);

        // Assert
        Assert.NotNull(data.Outdoor);
        Assert.Equal(18.1m, data.Outdoor!.Temperature);
        Assert.Equal(0.44m, data.Outdoor.Humidity);
        Assert.Equal(5.7m, data.Outdoor.DewPoint);
        Assert.Equal(13.1m, data.Outdoor.FeelsLikeTemperature);
        Assert.Equal(1.8m, data.Outdoor.WindSpeed);
        Assert.Equal(3.6m, data.Outdoor.WindGust);
        Assert.Equal(9.4m, data.Outdoor.MaxDailyGust);
        Assert.Equal(500m, data.Outdoor.Illuminance);
        Assert.Equal(3m, data.Outdoor.UvIndex);
        Assert.Equal(99m, data.Outdoor.WindDirection);
    }

    [Fact]
    public void WhenWh25Present_ThenParsesIndoorData()
    {
        // Arrange
        var json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "wh25": [
                {"intemp":"19.3","unit":"℃","inhumi":"46%","abs":"946.2 hPa","rel":"1008.3 hPa"}
            ]
        }
        """);

        // Act
        var data = EcowittClient.ParseLiveData(json);

        // Assert
        Assert.NotNull(data.Indoor);
        Assert.Equal(19.3m, data.Indoor!.Temperature);
        Assert.Equal(0.46m, data.Indoor.Humidity);
        Assert.Equal(946.2m, data.Indoor.AbsolutePressure);
        Assert.Equal(1008.3m, data.Indoor.RelativePressure);
    }

    // Rain and piezoRain sections returned by a GW2000A (firmware V3.3.2), which sends neither
    // 0x0F nor 0x14. The zero readings of the rain section are replaced so every bucket differs.
    internal const string Gw2000RainJson = """
        {
            "rain": [
                {"id":"0x0D","val":"1.2 mm"},
                {"id":"0x0E","val":"0.6 mm/Hr"},
                {"id":"0x7D","val":"0.3 mm"},
                {"id":"0x7C","val":"2.4 mm"},
                {"id":"0x10","val":"0.9 mm"},
                {"id":"0x11","val":"14.8 mm"},
                {"id":"0x12","val":"20.5 mm"},
                {"id":"0x13","val":"506.1 mm","battery":"2","voltage":"1.3"}
            ],
            "piezoRain": [
                {"id":"srain_piezo","val":"0"},
                {"id":"0x0D","val":"0.0 mm"},
                {"id":"0x0E","val":"0.0 mm/Hr"},
                {"id":"0x7D","val":"0.0 mm"},
                {"id":"0x7C","val":"0.0 mm"},
                {"id":"0x10","val":"0.0 mm"},
                {"id":"0x11","val":"10.4 mm"},
                {"id":"0x12","val":"12.9 mm"},
                {"id":"0x13","val":"294.9 mm","battery":"1","voltage":"0.28","ws90cap_volt":"5.1","ws90_ver":"156"}
            ]
        }
        """;

    [Fact]
    public void WhenGw2000RainSectionPresent_ThenMapsEachItemIdToItsBucket()
    {
        // Arrange
        var json = JsonSerializer.Deserialize<JsonElement>(Gw2000RainJson);

        // Act
        var data = EcowittClient.ParseLiveData(json);

        // Assert
        Assert.NotNull(data.Rain);
        Assert.Equal(1.2m, data.Rain!.RainEvent);
        Assert.Equal(0.6m, data.Rain.RainRate);
        Assert.Equal(0.3m, data.Rain.HourlyRain);
        Assert.Equal(2.4m, data.Rain.Last24HoursRain);
        Assert.Equal(0.9m, data.Rain.DailyRain);
        Assert.Equal(14.8m, data.Rain.WeeklyRain);
        Assert.Equal(20.5m, data.Rain.MonthlyRain);
        Assert.Equal(506.1m, data.Rain.YearlyRain);
        Assert.Null(data.Rain.TotalRain);
        Assert.Equal(2, data.Rain.Battery);
    }

    [Fact]
    public void WhenGw2000PiezoRainSectionPresent_ThenMapsEachItemIdToItsBucket()
    {
        // Arrange
        var json = JsonSerializer.Deserialize<JsonElement>(Gw2000RainJson);

        // Act
        var data = EcowittClient.ParseLiveData(json);

        // Assert
        Assert.NotNull(data.PiezoRain);
        Assert.Equal(0.0m, data.PiezoRain!.RainEvent);
        Assert.Equal(0.0m, data.PiezoRain.RainRate);
        Assert.Equal(0.0m, data.PiezoRain.HourlyRain);
        Assert.Equal(0.0m, data.PiezoRain.Last24HoursRain);
        Assert.Equal(0.0m, data.PiezoRain.DailyRain);
        Assert.Equal(10.4m, data.PiezoRain.WeeklyRain);
        Assert.Equal(12.9m, data.PiezoRain.MonthlyRain);
        Assert.Equal(294.9m, data.PiezoRain.YearlyRain);
        Assert.Null(data.PiezoRain.TotalRain);
        Assert.Equal(1, data.PiezoRain.Battery);
    }

    [Fact]
    public void WhenRainSectionHasTotalsItem_ThenParsesTotalRain()
    {
        // Arrange
        var json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "rain": [
                {"id":"0x13","val":"506.1 mm"},
                {"id":"0x14","val":"1834.7 mm"}
            ]
        }
        """);

        // Act
        var data = EcowittClient.ParseLiveData(json);

        // Assert
        Assert.NotNull(data.Rain);
        Assert.Equal(506.1m, data.Rain!.YearlyRain);
        Assert.Equal(1834.7m, data.Rain.TotalRain);
    }

    [Fact]
    public void WhenChannelsPresent_ThenParsesChannelData()
    {
        // Arrange
        var json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "ch_aisle": [
                {"channel":"1","name":"Living Room","temp":"21.3","unit":"℃","humidity":"44%","battery":"6"},
                {"channel":"3","name":"Bedroom","temp":"19.8","unit":"℃","humidity":"52%","battery":"5"}
            ]
        }
        """);

        // Act
        var data = EcowittClient.ParseLiveData(json);

        // Assert
        Assert.Equal(2, data.Channels.Length);
        Assert.Equal(1, data.Channels[0].Channel);
        Assert.Equal("Living Room", data.Channels[0].Name);
        Assert.Equal(21.3m, data.Channels[0].Temperature);
        Assert.Equal(0.44m, data.Channels[0].Humidity);
        Assert.Equal(6, data.Channels[0].Battery);
        Assert.Equal(3, data.Channels[1].Channel);
    }

    [Fact]
    public void WhenLightningPresent_ThenParsesLightningData()
    {
        // Arrange
        var json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "lightning": {"distance":"15","timestamp":"1712345678","count":"3","battery":"5"}
        }
        """);

        // Act
        var data = EcowittClient.ParseLiveData(json);

        // Assert
        Assert.NotNull(data.Lightning);
        Assert.Equal(15m, data.Lightning!.Distance);
        Assert.Equal(3, data.Lightning.StrikeCount);
        Assert.NotNull(data.Lightning.LastStrikeTime);
        Assert.Equal(5, data.Lightning.Battery);
    }

    [Fact]
    public void WhenLightningDisconnected_ThenParsesNullValues()
    {
        // Arrange
        var json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "lightning": {"distance":"--.-","timestamp":"--","count":"0","battery":"5"}
        }
        """);

        // Act
        var data = EcowittClient.ParseLiveData(json);

        // Assert
        Assert.NotNull(data.Lightning);
        Assert.Null(data.Lightning!.Distance);
        Assert.Equal(0, data.Lightning.StrikeCount);
        Assert.Null(data.Lightning.LastStrikeTime);
    }

    [Fact]
    public void WhenSectionsMissing_ThenReturnsNullForAbsentSensors()
    {
        // Arrange
        var json = JsonSerializer.Deserialize<JsonElement>("{}");

        // Act
        var data = EcowittClient.ParseLiveData(json);

        // Assert
        Assert.Null(data.Outdoor);
        Assert.Null(data.Indoor);
        Assert.Null(data.Rain);
        Assert.Null(data.PiezoRain);
        Assert.Null(data.Lightning);
        Assert.Empty(data.Channels);
        Assert.Empty(data.SoilMoisture);
    }

    // Version parsing

    [Fact]
    public void WhenVersionHasPrefix_ThenStripsPrefix()
    {
        // Arrange
        var json = JsonSerializer.Deserialize<JsonElement>("""
        {"version":"Version: GW2000A_V3.3.0","newVersion":"0"}
        """);

        // Act
        var result = EcowittClient.ParseVersionInfo(json);

        // Assert
        Assert.Equal("GW2000A_V3.3.0", result.Version);
    }

    [Fact]
    public void WhenVersionHasNoPrefix_ThenReturnsAsIs()
    {
        // Arrange
        var json = JsonSerializer.Deserialize<JsonElement>("""
        {"version":"GW1000_V1.2.3"}
        """);

        // Act
        var result = EcowittClient.ParseVersionInfo(json);

        // Assert
        Assert.Equal("GW1000_V1.2.3", result.Version);
    }

    // Network info parsing

    [Fact]
    public void WhenNetworkInfoHasEthernetFields_ThenParsesCorrectly()
    {
        // Arrange
        var json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "mac":"3C:8A:1F:32:E1:57",
            "ethIpType":"1",
            "ethIP":"192.168.1.216",
            "ethMask":"255.255.255.0",
            "ethGateway":"192.168.1.1",
            "ethDNS":"192.168.1.1",
            "wLanFunc":false
        }
        """);

        // Act
        var result = EcowittClient.ParseNetworkInfo(json);

        // Assert
        Assert.Equal("3C:8A:1F:32:E1:57", result.MacAddress);
        Assert.Equal("192.168.1.216", result.IpAddress);
        Assert.Equal("255.255.255.0", result.SubnetMask);
        Assert.Equal("192.168.1.1", result.Gateway);
        Assert.Equal("192.168.1.1", result.Dns);
        Assert.False(result.IsWireless);
    }

    [Fact]
    public void WhenNetworkInfoHasWifiFields_ThenParsesCorrectly()
    {
        // Arrange
        var json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "mac":"AA:BB:CC:DD:EE:FF",
            "ip":"10.0.0.50",
            "mask":"255.255.0.0",
            "gw":"10.0.0.1",
            "dns":"8.8.8.8",
            "wLanFunc":true
        }
        """);

        // Act
        var result = EcowittClient.ParseNetworkInfo(json);

        // Assert
        Assert.Equal("AA:BB:CC:DD:EE:FF", result.MacAddress);
        Assert.Equal("10.0.0.50", result.IpAddress);
        Assert.Equal("255.255.0.0", result.SubnetMask);
        Assert.Equal("10.0.0.1", result.Gateway);
        Assert.Equal("8.8.8.8", result.Dns);
        Assert.True(result.IsWireless);
    }

    // Device info parsing

    [Fact]
    public void WhenDeviceInfoHasModelField_ThenUsesModelDirectly()
    {
        // Arrange
        var json = JsonSerializer.Deserialize<JsonElement>("""
        {"model":"GW2000","stationtype":"EasyWeather","freq":"868M"}
        """);

        // Act
        var result = EcowittClient.ParseDeviceInfo(json);

        // Assert
        Assert.Equal("GW2000", result.Model);
        Assert.Equal("EasyWeather", result.StationType);
        Assert.Equal("868M", result.Frequency);
    }

    [Fact]
    public void WhenDeviceInfoHasNoModelButHasApName_ThenExtractsModelFromApName()
    {
        // Arrange
        var json = JsonSerializer.Deserialize<JsonElement>("""
        {"sensorType":"1","rf_freq":"1","apName":"GW2000A-WIFIE157"}
        """);

        // Act
        var result = EcowittClient.ParseDeviceInfo(json);

        // Assert
        Assert.Equal("GW2000A", result.Model);
        Assert.Equal("1", result.Frequency);
    }

    [Fact]
    public void WhenDeviceInfoHasApNameWithoutDash_ThenUsesFullApName()
    {
        // Arrange
        var json = JsonSerializer.Deserialize<JsonElement>("""
        {"apName":"GW1000"}
        """);

        // Act
        var result = EcowittClient.ParseDeviceInfo(json);

        // Assert
        Assert.Equal("GW1000", result.Model);
    }

    // Rain data with missing yearly

    [Fact]
    public void WhenRainSectionMissingYearly_ThenYearlyRainIsNull()
    {
        // Arrange
        var json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "rain": [
                {"id":"0x0D","val":"0.0 mm"},
                {"id":"0x0E","val":"0.0 mm/Hr"},
                {"id":"0x10","val":"3.8 mm"},
                {"id":"0x12","val":"175.1 mm"}
            ]
        }
        """);

        // Act
        var data = EcowittClient.ParseLiveData(json);

        // Assert
        Assert.NotNull(data.Rain);
        Assert.Equal(3.8m, data.Rain!.DailyRain);
        Assert.Equal(175.1m, data.Rain.MonthlyRain);
        Assert.Null(data.Rain.YearlyRain);
        Assert.Null(data.Rain.TotalRain);
    }

    [Fact]
    public void WhenRainHasUnknownIds_ThenIgnoredGracefully()
    {
        // Arrange
        var json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "rain": [
                {"id":"0x0F","val":"0.0 mm"},
                {"id":"srain_piezo","val":"0"},
                {"id":"0x10","val":"3.8 mm"}
            ]
        }
        """);

        // Act
        var data = EcowittClient.ParseLiveData(json);

        // Assert
        Assert.NotNull(data.Rain);
        Assert.Equal(3.8m, data.Rain!.DailyRain);
        Assert.Null(data.Rain.RainEvent);
    }

    // Sensor info parsing

    [Fact]
    public void WhenSensorsInfoHasActiveSensors_ThenParsesCorrectly()
    {
        // Arrange
        var json = JsonSerializer.Deserialize<JsonElement>("""
        [
            {"img":"wh90","type":"48","name":"Outdoor","id":"9637","batt":"1","rssi":"-48","signal":"4","idst":"1"},
            {"img":"wh31","type":"6","name":"Temp CH1","id":"A6","batt":"0","rssi":"-69","signal":"3","idst":"1"},
            {"img":"wh31","type":"7","name":"Temp CH2","id":"FFFFFFFF","batt":"9","rssi":"--","signal":"--","idst":"1"}
        ]
        """);

        // Act
        var result = EcowittClient.ParseSensorsInfo(json);

        // Assert
        Assert.Equal(2, result.Length); // unpaired sensor filtered out
        Assert.Equal("0x9637", result[0].SensorId);
        Assert.Equal("wh90", result[0].SensorType);
        Assert.Equal(48, result[0].TypeCode);
        Assert.Equal(-48, result[0].Rssi);
        Assert.Equal(4, result[0].SignalLevel);
        Assert.Equal("0xA6", result[1].SensorId);
        Assert.Equal(-69, result[1].Rssi);
    }

    [Fact]
    public void WhenSensorsInfoHasDisabledSensor_ThenFiltered()
    {
        // Arrange
        var json = JsonSerializer.Deserialize<JsonElement>("""
        [
            {"img":"wh25","type":"4","name":"Indoor","id":"FFFFFFFE","batt":"9","rssi":"--","signal":"--","idst":"0"}
        ]
        """);

        // Act
        var result = EcowittClient.ParseSensorsInfo(json);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void WhenLeakSensorPresent_ThenParsesLeakData()
    {
        // Arrange
        var json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "ch_leak": [
                {"channel":"1","leak":"0","battery":"5"},
                {"channel":"2","leak":"1","battery":"3"}
            ]
        }
        """);

        // Act
        var data = EcowittClient.ParseLiveData(json);

        // Assert
        Assert.Equal(2, data.Leaks.Length);
        Assert.False(data.Leaks[0].IsLeaking);
        Assert.True(data.Leaks[1].IsLeaking);
    }
}
