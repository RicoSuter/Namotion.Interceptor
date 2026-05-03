using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Namotion.Devices.Shelly.Tests;

public class ShellyDeviceComponentParsingTests
{
    private static ShellyDevice CreateDevice()
    {
        var httpClientFactory = new TestHttpClientFactory();
        return new ShellyDevice(httpClientFactory, NullLogger<ShellyDevice>.Instance);
    }

    [Fact]
    public void WhenStatusHasSwitches_ThenCreatesShellySwitch()
    {
        // Arrange
        var device = CreateDevice();
        var json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "switch:0": { "id": 0, "output": true, "source": "button", "apower": 42.5, "voltage": 230.1, "current": 0.185 },
            "switch:1": { "id": 1, "output": false, "source": "init" }
        }
        """);

        // Act
        device.ParseStatusComponents(json);

        // Assert
        Assert.Equal(2, device.Switches.Length);
        Assert.True(device.Switches[0].IsOn);
        Assert.Equal("button", device.Switches[0].Source);
        Assert.Equal(42.5m, device.Switches[0].MeasuredPower);
        Assert.Equal(230.1m, device.Switches[0].ElectricalVoltage);
        Assert.Equal(0.185m, device.Switches[0].ElectricalCurrent);
        Assert.False(device.Switches[1].IsOn);
    }

    [Fact]
    public void WhenStatusHasCovers_ThenCreatesShellyCover()
    {
        // Arrange
        var device = CreateDevice();
        var json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "cover:0": {
                "id": 0, "state": "open", "apower": 0, "voltage": 231.2,
                "current": 0.0, "pf": 0.0, "freq": 50.0,
                "current_pos": 100, "pos_control": true, "last_direction": "open",
                "temperature": { "tC": 38.5 }
            }
        }
        """);

        // Act
        device.ParseStatusComponents(json);

        // Assert
        Assert.Single(device.Covers);
        Assert.Equal("open", device.Covers[0].ApiState);
        Assert.Equal(100, device.Covers[0].CurrentPosition);
        Assert.Equal(231.2m, device.Covers[0].ElectricalVoltage);
        Assert.Equal(50.0m, device.Covers[0].ElectricalFrequency);
        Assert.Equal(38.5m, device.Covers[0].Temperature);
    }

    [Fact]
    public void WhenStatusHasInputs_ThenCreatesShellyInput()
    {
        // Arrange
        var device = CreateDevice();
        var json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "input:0": { "id": 0, "state": true },
            "input:1": { "id": 1, "state": false, "counts": { "total": 42 }, "freq": 1.5 }
        }
        """);

        // Act
        device.ParseStatusComponents(json);

        // Assert
        Assert.Equal(2, device.Inputs.Length);
        Assert.True(device.Inputs[0].State);
        Assert.Null(device.Inputs[0].CountTotal);
        Assert.False(device.Inputs[1].State);
        Assert.Equal(42L, device.Inputs[1].CountTotal);
        Assert.Equal(1.5, device.Inputs[1].CountFrequency);
    }

    [Fact]
    public void WhenStatusHasTemperatureSensor_ThenCreatesShellyTemperatureSensor()
    {
        // Arrange
        var device = CreateDevice();
        var json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "temperature:0": { "id": 0, "tC": 22.5 }
        }
        """);

        // Act
        device.ParseStatusComponents(json);

        // Assert
        Assert.Single(device.TemperatureSensors);
        Assert.Equal(22.5m, device.TemperatureSensors[0].Temperature);
    }

    [Fact]
    public void WhenStatusHasEnergyMeter_ThenCreatesShellyEnergyMeter()
    {
        // Arrange
        var device = CreateDevice();
        var json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "em:0": {
                "id": 0,
                "a_current": 1.5, "a_voltage": 230.0, "a_act_power": 345.0, "a_freq": 50.0,
                "b_current": 2.1, "b_voltage": 229.5, "b_act_power": 482.0, "b_freq": 50.0,
                "c_current": 0.8, "c_voltage": 231.0, "c_act_power": 184.0, "c_freq": 50.0,
                "total_act_power": 1011.0, "total_current": 4.4, "n_current": 0.3
            }
        }
        """);

        // Act
        device.ParseStatusComponents(json);

        // Assert
        Assert.NotNull(device.EnergyMeter);
        Assert.Equal(1011.0m, device.EnergyMeter!.MeasuredPower);
        Assert.Equal(1011.0m, device.EnergyMeter.TotalActivePower);
        Assert.Equal(4.4m, device.EnergyMeter.TotalCurrent);
        Assert.Equal(0.3m, device.EnergyMeter.NeutralCurrent);
        Assert.Equal(230.0m, device.EnergyMeter.Phases[0].ElectricalVoltage);
        Assert.Equal(229.5m, device.EnergyMeter.Phases[1].ElectricalVoltage);
        Assert.Equal(231.0m, device.EnergyMeter.Phases[2].ElectricalVoltage);
    }

    [Fact]
    public void WhenSecondParseWithSameCount_ThenUpdatesInPlace()
    {
        // Arrange
        var device = CreateDevice();
        var json1 = JsonSerializer.Deserialize<JsonElement>("""
        {
            "switch:0": { "id": 0, "output": true }
        }
        """);
        device.ParseStatusComponents(json1);
        var originalSwitch = device.Switches[0];

        var json2 = JsonSerializer.Deserialize<JsonElement>("""
        {
            "switch:0": { "id": 0, "output": false }
        }
        """);

        // Act
        device.ParseStatusComponents(json2);

        // Assert
        Assert.Same(originalSwitch, device.Switches[0]);
        Assert.False(device.Switches[0].IsOn);
    }

    [Fact]
    public void WhenSecondParseWithDifferentCount_ThenReplacesArray()
    {
        // Arrange
        var device = CreateDevice();
        var json1 = JsonSerializer.Deserialize<JsonElement>("""
        {
            "switch:0": { "id": 0, "output": true }
        }
        """);
        device.ParseStatusComponents(json1);
        var originalSwitch = device.Switches[0];

        var json2 = JsonSerializer.Deserialize<JsonElement>("""
        {
            "switch:0": { "id": 0, "output": true },
            "switch:1": { "id": 1, "output": false }
        }
        """);

        // Act
        device.ParseStatusComponents(json2);

        // Assert
        Assert.Equal(2, device.Switches.Length);
        Assert.NotSame(originalSwitch, device.Switches[0]);
    }

    [Fact]
    public void WhenUnknownComponentType_ThenIgnored()
    {
        // Arrange
        var device = CreateDevice();
        var json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "light:0": { "id": 0, "output": true },
            "switch:0": { "id": 0, "output": true }
        }
        """);

        // Act
        device.ParseStatusComponents(json);

        // Assert
        Assert.Single(device.Switches);
        Assert.Empty(device.Covers);
    }

    [Fact]
    public void WhenSysComponentPresent_ThenUpdatesUptime()
    {
        // Arrange
        var device = CreateDevice();
        var json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "sys": { "uptime": 3600, "available_updates": { "stable": { "version": "1.5.0" } } }
        }
        """);

        // Act
        device.ParseStatusComponents(json);

        // Assert
        Assert.Equal(TimeSpan.FromHours(1), device.Uptime);
        Assert.Equal("1.5.0", device.AvailableSoftwareUpdate);
    }

    [Fact]
    public void WhenWifiComponentPresent_ThenUpdatesNetworkInfo()
    {
        // Arrange
        var device = CreateDevice();
        var json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "wifi": { "sta_ip": "192.168.1.100", "ssid": "MyNetwork", "rssi": -45 }
        }
        """);

        // Act
        device.ParseStatusComponents(json);

        // Assert
        Assert.Equal("192.168.1.100", device.IpAddress);
        Assert.Equal(-45, device.SignalStrength);
    }

    [Theory]
    [InlineData("switch:0", "switch", 0)]
    [InlineData("cover:1", "cover", 1)]
    [InlineData("em:0", "em", 0)]
    [InlineData("input:2", "input", 2)]
    [InlineData("sys", "sys", 0)]
    [InlineData("wifi", "wifi", 0)]
    public void WhenParsingComponentKey_ThenReturnsCorrectParts(string key, string expectedType, int expectedIndex)
    {
        // Act
        var (componentType, index) = ShellyDevice.ParseComponentKey(key);

        // Assert
        Assert.Equal(expectedType, componentType);
        Assert.Equal(expectedIndex, index);
    }

    private class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
