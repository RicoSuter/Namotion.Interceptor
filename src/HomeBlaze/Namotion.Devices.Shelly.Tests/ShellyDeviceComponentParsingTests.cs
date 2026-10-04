using System.Globalization;
using System.Net;
using System.Text;
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

    private const string EnergyMeterStatus = """
        "em:0": { "id": 0, "total_act_power": 250.0, "total_current": 1.1, "n_current": 0.1 }
        """;

    private static readonly string MappedComponents = ComponentsWithValues(100.0m, 50.0m);

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static async Task ReadVirtualComponentsAsync(ShellyDevice device, string json)
    {
        using var client = new HttpClient(new StubHttpMessageHandler(_ => JsonResponse(json)));
        await device.ReadVirtualEnergyCountersAsync(client, CancellationToken.None);
    }

    private static string ComponentsWithValues(decimal importedValue, decimal exportedValue, int configurationRevision = 7) => $$"""
        {
            "components": [
                { "key": "number:201", "status": { "value": {{importedValue.ToString(CultureInfo.InvariantCulture)}} }, "config": { "id": 201, "name": "TotalImportedEnergy" } },
                { "key": "number:205", "status": { "value": {{exportedValue.ToString(CultureInfo.InvariantCulture)}} }, "config": { "id": 205, "name": "TotalExportedEnergy" } }
            ],
            "cfg_rev": {{configurationRevision}}, "offset": 0, "total": 2
        }
        """;

    private static ShellyDevice CreateDeviceWithEnergyMeter(int configurationRevision)
    {
        var device = CreateDevice();
        device.HostAddress = "shelly.test";
        device.ParseStatusComponents(Json($$"""{ {{EnergyMeterStatus}}, "sys": { "cfg_rev": {{configurationRevision}} } }"""));
        return device;
    }

    private static ShellyDevice CreateDeviceWithDeviceCounters(int configurationRevision, decimal deviceImported, decimal deviceExported)
    {
        var device = CreateDevice();
        device.HostAddress = "shelly.test";
        device.ParseStatusComponents(Json(DeviceCountersStatus(configurationRevision, deviceImported, deviceExported)));
        return device;
    }

    private static string DeviceCountersStatus(int configurationRevision, decimal deviceImported, decimal deviceExported) => $$"""
        {
            {{EnergyMeterStatus}},
            "emdata:0": { "id": 0, "total_act": {{deviceImported.ToString(CultureInfo.InvariantCulture)}}, "total_act_ret": {{deviceExported.ToString(CultureInfo.InvariantCulture)}} },
            "sys": { "cfg_rev": {{configurationRevision}} }
        }
        """;

    private const string NoComponents = """{ "components": [], "cfg_rev": 7, "offset": 0, "total": 0 }""";

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
    public void WhenSwitchReportsEnergy_ThenSetsImportedAndExportedCounters()
    {
        // Arrange
        var device = CreateDevice();
        var json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "switch:0": { "id": 0, "output": true, "apower": -120.0, "aenergy": { "total": 1500.5 }, "ret_aenergy": { "total": 320.25 } }
        }
        """);

        // Act
        device.ParseStatusComponents(json);

        // Assert
        Assert.Equal(-120.0m, device.Switches[0].MeasuredPower);
        Assert.Equal(1500.5m, device.Switches[0].TotalImportedEnergy);
        Assert.Equal(320.25m, device.Switches[0].TotalExportedEnergy);
    }

    [Fact]
    public void WhenSwitchDoesNotReportReturnedEnergy_ThenExportedCounterIsNull()
    {
        // Arrange
        var device = CreateDevice();
        var json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "switch:0": { "id": 0, "output": true, "apower": 42.5, "aenergy": { "total": 1500.5 } }
        }
        """);

        // Act
        device.ParseStatusComponents(json);

        // Assert
        Assert.Equal(1500.5m, device.Switches[0].TotalImportedEnergy);
        Assert.Null(device.Switches[0].TotalExportedEnergy);
    }

    [Fact]
    public void WhenPartialSwitchUpdateHasReturnedEnergy_ThenOnlyExportedCounterChanges()
    {
        // Arrange
        var device = CreateDevice();
        device.ParseStatusComponents(JsonSerializer.Deserialize<JsonElement>("""
        {
            "switch:0": { "id": 0, "output": true, "aenergy": { "total": 1500.5 }, "ret_aenergy": { "total": 320.25 } }
        }
        """));

        // Act
        device.ParseStatusComponents(JsonSerializer.Deserialize<JsonElement>("""
        {
            "switch:0": { "id": 0, "ret_aenergy": { "total": 321.0 } }
        }
        """), isPartialUpdate: true);

        // Assert
        Assert.Equal(1500.5m, device.Switches[0].TotalImportedEnergy);
        Assert.Equal(321.0m, device.Switches[0].TotalExportedEnergy);
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
        Assert.True(device.Inputs[0].IsActive);
        Assert.Null(device.Inputs[0].TotalCount);
        Assert.False(device.Inputs[1].IsActive);
        Assert.Equal(42L, device.Inputs[1].TotalCount);
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
        Assert.Equal(4.4m, device.EnergyMeter.ElectricalCurrent);
        Assert.Null(device.EnergyMeter.TotalImportedEnergy);
        Assert.Null(device.EnergyMeter.TotalExportedEnergy);
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
    public void WhenPartialSysUpdateLacksAvailableUpdates_ThenAvailableSoftwareUpdateIsKept()
    {
        // Arrange
        var device = CreateDevice();
        device.ParseStatusComponents(JsonSerializer.Deserialize<JsonElement>("""
        {
            "sys": { "uptime": 3600, "cfg_rev": 7, "available_updates": { "stable": { "version": "1.5.0" } } }
        }
        """));

        // Act
        device.ParseStatusComponents(JsonSerializer.Deserialize<JsonElement>("""
        {
            "sys": { "cfg_rev": 8 }
        }
        """), isPartialUpdate: true);

        // Assert
        Assert.Equal("1.5.0", device.AvailableSoftwareUpdate);
    }

    [Fact]
    public void WhenFullStatusHasEmptyAvailableUpdates_ThenAvailableSoftwareUpdateIsCleared()
    {
        // Arrange
        var device = CreateDevice();
        device.ParseStatusComponents(JsonSerializer.Deserialize<JsonElement>("""
        {
            "sys": { "available_updates": { "stable": { "version": "1.5.0" } } }
        }
        """));

        // Act
        device.ParseStatusComponents(JsonSerializer.Deserialize<JsonElement>("""
        {
            "sys": { "available_updates": {} }
        }
        """));

        // Assert
        Assert.Null(device.AvailableSoftwareUpdate);
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
        Assert.True(device.IsWireless);
        Assert.Equal(-45, device.SignalStrength);
    }

    [Fact]
    public void WhenEthernetHasIp_ThenReportsWiredAdapter()
    {
        // Arrange
        var device = CreateDevice();
        var json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "eth": { "ip": "192.168.1.50", "ip6": null },
            "wifi": { "sta_ip": null, "status": "disconnected", "ssid": null, "rssi": 0 }
        }
        """);

        // Act
        device.ParseStatusComponents(json);

        // Assert
        Assert.Equal("192.168.1.50", device.IpAddress);
        Assert.False(device.IsWireless);
        Assert.Null(device.SignalStrength);
    }

    [Fact]
    public void WhenEthernetHasNoIp_ThenReportsWifiAdapter()
    {
        // Arrange
        var device = CreateDevice();
        var json = JsonSerializer.Deserialize<JsonElement>("""
        {
            "eth": { "ip": null, "ip6": null },
            "wifi": { "sta_ip": "192.168.1.133", "status": "got ip", "ssid": "MyNetwork", "rssi": -51 }
        }
        """);

        // Act
        device.ParseStatusComponents(json);

        // Assert
        Assert.Equal("192.168.1.133", device.IpAddress);
        Assert.True(device.IsWireless);
        Assert.Equal(-51, device.SignalStrength);
    }

    [Fact]
    public void WhenWifiPushContainsOnlyRssi_ThenKeepsIpAddress()
    {
        // Arrange
        var device = CreateDevice();
        device.ParseStatusComponents(JsonSerializer.Deserialize<JsonElement>("""
        {
            "wifi": { "sta_ip": "192.168.1.133", "status": "got ip", "ssid": "MyNetwork", "rssi": -51 }
        }
        """));

        // Act
        device.ParseStatusComponents(JsonSerializer.Deserialize<JsonElement>("""
        {
            "wifi": { "rssi": -60 }
        }
        """), isPartialUpdate: true);

        // Assert
        Assert.Equal("192.168.1.133", device.IpAddress);
        Assert.True(device.IsWireless);
        Assert.Equal(-60, device.SignalStrength);
    }

    [Fact]
    public void WhenDeviceStateIsReset_ThenNetworkAdapterIsCleared()
    {
        // Arrange
        var device = CreateDevice();
        device.ParseStatusComponents(JsonSerializer.Deserialize<JsonElement>("""
        {
            "eth": { "ip": "192.168.1.50" },
            "wifi": { "sta_ip": null, "rssi": 0 }
        }
        """));

        // Act
        device.ResetForConfigurationChange();

        // Assert
        Assert.Null(device.IpAddress);
        Assert.Null(device.IsWireless);
        Assert.Null(device.SignalStrength);
    }

    private const string EnergyDataPush = """
        {
            "emdata:0": {
                "id": 0, "total_act": 1010.0, "total_act_ret": 210.0,
                "a_total_act_energy": 400.0, "a_total_act_ret_energy": 70.0,
                "b_total_act_energy": 350.0, "b_total_act_ret_energy": 80.0,
                "c_total_act_energy": 260.0, "c_total_act_ret_energy": 60.0
            }
        }
        """;

    [Fact]
    public void WhenEnergyDataIsPushedAlone_ThenPhaseCountersAndSumsAreUpdated()
    {
        // Arrange
        var device = CreateDeviceWithDeviceCounters(configurationRevision: 7, deviceImported: 1000.0m, deviceExported: 200.0m);

        // Act
        device.ParseStatusComponents(Json(EnergyDataPush), isPartialUpdate: true);

        // Assert
        Assert.Equal(1010.0m, device.EnergyMeter!.TotalImportedPhaseEnergy);
        Assert.Equal(210.0m, device.EnergyMeter.TotalExportedPhaseEnergy);
        Assert.Equal(400.0m, device.EnergyMeter.Phases[0].TotalImportedEnergy);
        Assert.Equal(60.0m, device.EnergyMeter.Phases[2].TotalExportedEnergy);
        Assert.Equal(250.0m, device.EnergyMeter.MeasuredPower);
    }

    [Fact]
    public async Task WhenEnergyDataIsPushedAloneWithoutScript_ThenFallbackCountersAreUpdated()
    {
        // Arrange
        var device = CreateDeviceWithDeviceCounters(configurationRevision: 7, deviceImported: 1000.0m, deviceExported: 200.0m);
        await ReadVirtualComponentsAsync(device, NoComponents);

        // Act
        device.ParseStatusComponents(Json(EnergyDataPush), isPartialUpdate: true);

        // Assert
        Assert.False(device.EnergyMeter!.IsTotalEnergyPhaseNetted);
        Assert.Equal(1010.0m, device.EnergyMeter.TotalImportedEnergy);
        Assert.Equal(210.0m, device.EnergyMeter.TotalExportedEnergy);
    }

    [Fact]
    public async Task WhenEnergyDataIsPushedAloneWithScriptValues_ThenScriptCountersAreKept()
    {
        // Arrange
        var device = CreateDeviceWithDeviceCounters(configurationRevision: 7, deviceImported: 1000.0m, deviceExported: 200.0m);
        await ReadVirtualComponentsAsync(device, MappedComponents);

        // Act
        device.ParseStatusComponents(Json(EnergyDataPush), isPartialUpdate: true);

        // Assert
        Assert.Equal(1010.0m, device.EnergyMeter!.TotalImportedPhaseEnergy);
        Assert.Equal(100.0m, device.EnergyMeter.TotalImportedEnergy);
        Assert.Equal(50.0m, device.EnergyMeter.TotalExportedEnergy);
    }

    [Fact]
    public void WhenEnergyDataIsPushedWithoutEnergyMeter_ThenNoEnergyMeterIsCreated()
    {
        // Arrange
        var device = CreateDevice();

        // Act
        device.ParseStatusComponents(Json(EnergyDataPush), isPartialUpdate: true);

        // Assert
        Assert.Null(device.EnergyMeter);
    }

    [Fact]
    public async Task WhenVirtualEnergyCountersExist_ThenValuesAreMappedByName()
    {
        // Arrange
        var device = CreateDeviceWithEnergyMeter(configurationRevision: 7);

        // Act
        await ReadVirtualComponentsAsync(device, """
        {
            "components": [
                { "key": "number:205", "status": { "value": 12.5 }, "config": { "id": 205, "name": "TotalExportedEnergy" } },
                { "key": "number:201", "status": { "value": 1234.567 }, "config": { "id": 201, "name": "TotalImportedEnergy" } }
            ],
            "cfg_rev": 7, "offset": 0, "total": 2
        }
        """);

        // Assert
        Assert.True(device.EnergyMeter!.IsTotalEnergyPhaseNetted);
        Assert.Equal(1234.567m, device.EnergyMeter.TotalImportedEnergy);
        Assert.Equal(12.5m, device.EnergyMeter.TotalExportedEnergy);
    }

    [Fact]
    public async Task WhenFullStatusArrives_ThenVirtualEnergyCountersAreKept()
    {
        // Arrange
        var device = CreateDeviceWithEnergyMeter(configurationRevision: 7);
        await ReadVirtualComponentsAsync(device, MappedComponents);

        // Act
        device.ParseStatusComponents(Json($$"""{ {{EnergyMeterStatus}}, "sys": { "cfg_rev": 7 } }"""));

        // Assert
        Assert.Equal(100.0m, device.EnergyMeter!.TotalImportedEnergy);
        Assert.Equal(50.0m, device.EnergyMeter.TotalExportedEnergy);
    }

    [Fact]
    public async Task WhenStatusHasVirtualNumbers_ThenTheyAreIgnored()
    {
        // Arrange
        var device = CreateDeviceWithEnergyMeter(configurationRevision: 7);
        await ReadVirtualComponentsAsync(device, MappedComponents);

        // Act
        device.ParseStatusComponents(Json($$"""{ {{EnergyMeterStatus}}, "number:201": { "id": 201, "value": 2000.5 } }"""));
        device.ParseStatusComponents(Json("""{ "number:205": { "id": 205, "value": 60.0 } }"""), isPartialUpdate: true);

        // Assert
        Assert.Equal(100.0m, device.EnergyMeter!.TotalImportedEnergy);
        Assert.Equal(50.0m, device.EnergyMeter.TotalExportedEnergy);
    }

    [Fact]
    public async Task WhenReadOnEveryPoll_ThenEveryReadUpdatesTheCounters()
    {
        // Arrange
        var device = CreateDeviceWithEnergyMeter(configurationRevision: 7);
        var readCount = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            readCount++;
            return JsonResponse(ComponentsWithValues(100.0m * readCount, 10.0m * readCount));
        });
        using var client = new HttpClient(handler);

        // Act
        await device.ReadVirtualEnergyCountersAsync(client, CancellationToken.None);
        var importedAfterFirstRead = device.EnergyMeter!.TotalImportedEnergy;
        var exportedAfterFirstRead = device.EnergyMeter.TotalExportedEnergy;
        await device.ReadVirtualEnergyCountersAsync(client, CancellationToken.None);

        // Assert
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.Equal(100.0m, importedAfterFirstRead);
        Assert.Equal(10.0m, exportedAfterFirstRead);
        Assert.Equal(200.0m, device.EnergyMeter.TotalImportedEnergy);
        Assert.Equal(20.0m, device.EnergyMeter.TotalExportedEnergy);
    }

    [Fact]
    public void WhenComponentsWereNotReadYet_ThenPhaseNettingIsUnknown()
    {
        // Act
        var device = CreateDeviceWithDeviceCounters(configurationRevision: 7, deviceImported: 1000.0m, deviceExported: 200.0m);

        // Assert
        Assert.Null(device.EnergyMeter!.IsTotalEnergyPhaseNetted);
        Assert.Null(device.EnergyMeter.TotalImportedEnergy);
        Assert.Null(device.EnergyMeter.TotalExportedEnergy);
    }

    [Fact]
    public async Task WhenScriptIsNotInstalled_ThenCountersFollowThePhaseSums()
    {
        // Arrange
        var device = CreateDeviceWithDeviceCounters(configurationRevision: 7, deviceImported: 1000.0m, deviceExported: 200.0m);

        // Act
        await ReadVirtualComponentsAsync(device, NoComponents);
        var importedAfterRead = device.EnergyMeter!.TotalImportedEnergy;
        device.ParseStatusComponents(Json(DeviceCountersStatus(7, 1001.5m, 200.25m)));

        // Assert
        Assert.False(device.EnergyMeter.IsTotalEnergyPhaseNetted);
        Assert.Equal(1000.0m, importedAfterRead);
        Assert.Equal(1001.5m, device.EnergyMeter.TotalImportedEnergy);
        Assert.Equal(200.25m, device.EnergyMeter.TotalExportedEnergy);
    }

    [Fact]
    public async Task WhenVirtualNumberHasOtherName_ThenCountersFollowThePhaseSums()
    {
        // Arrange
        var device = CreateDeviceWithDeviceCounters(configurationRevision: 3, deviceImported: 1000.0m, deviceExported: 200.0m);

        // Act
        await ReadVirtualComponentsAsync(device, """
        {
            "components": [
                { "key": "number:200", "status": { "value": 42.0 }, "config": { "id": 200, "name": "Setpoint" } },
                { "key": "boolean:200", "status": { "value": true }, "config": { "id": 200, "name": "TotalImportedEnergy" } }
            ],
            "cfg_rev": 3, "offset": 0, "total": 2
        }
        """);

        // Assert
        Assert.False(device.EnergyMeter!.IsTotalEnergyPhaseNetted);
        Assert.Equal(1000.0m, device.EnergyMeter.TotalImportedEnergy);
        Assert.Equal(200.0m, device.EnergyMeter.TotalExportedEnergy);
    }

    [Fact]
    public async Task WhenOnlyOneComponentExists_ThenCountersFollowThePhaseSums()
    {
        // Arrange
        var device = CreateDeviceWithDeviceCounters(configurationRevision: 7, deviceImported: 1000.0m, deviceExported: 200.0m);

        // Act
        await ReadVirtualComponentsAsync(device, """
        {
            "components": [ { "key": "number:201", "status": { "value": 900.0 }, "config": { "id": 201, "name": "TotalImportedEnergy" } } ],
            "cfg_rev": 7, "offset": 0, "total": 1
        }
        """);

        // Assert
        Assert.False(device.EnergyMeter!.IsTotalEnergyPhaseNetted);
        Assert.Equal(1000.0m, device.EnergyMeter.TotalImportedEnergy);
        Assert.Equal(200.0m, device.EnergyMeter.TotalExportedEnergy);
    }

    [Fact]
    public async Task WhenComponentsAppearAfterConfigurationChange_ThenCountersComeFromTheScript()
    {
        // Arrange
        var device = CreateDeviceWithDeviceCounters(configurationRevision: 7, deviceImported: 1000.0m, deviceExported: 200.0m);
        await ReadVirtualComponentsAsync(device, NoComponents);
        device.ParseStatusComponents(Json("""{ "sys": { "cfg_rev": 8 } }"""), isPartialUpdate: true);

        // Act
        await ReadVirtualComponentsAsync(device, ComponentsWithValues(950.0m, 150.0m, configurationRevision: 8));
        device.ParseStatusComponents(Json(DeviceCountersStatus(8, 1002.0m, 202.0m)));

        // Assert
        Assert.True(device.EnergyMeter!.IsTotalEnergyPhaseNetted);
        Assert.Equal(950.0m, device.EnergyMeter.TotalImportedEnergy);
        Assert.Equal(150.0m, device.EnergyMeter.TotalExportedEnergy);
    }

    [Fact]
    public async Task WhenComponentsDisappearAfterScriptValues_ThenCountersAreUnknownUntilTheyReturn()
    {
        // Arrange
        var device = CreateDeviceWithDeviceCounters(configurationRevision: 7, deviceImported: 1000.0m, deviceExported: 200.0m);
        await ReadVirtualComponentsAsync(device, MappedComponents);
        device.ParseStatusComponents(Json("""{ "sys": { "cfg_rev": 8 } }"""), isPartialUpdate: true);

        // Act
        await ReadVirtualComponentsAsync(device, """{ "components": [], "cfg_rev": 8, "offset": 0, "total": 0 }""");
        device.ParseStatusComponents(Json(DeviceCountersStatus(8, 1001.0m, 201.0m)));
        var isPhaseNettedWhileMissing = device.EnergyMeter!.IsTotalEnergyPhaseNetted;
        var importedWhileMissing = device.EnergyMeter.TotalImportedEnergy;
        var exportedWhileMissing = device.EnergyMeter.TotalExportedEnergy;
        device.ParseStatusComponents(Json("""{ "sys": { "cfg_rev": 9 } }"""), isPartialUpdate: true);
        await ReadVirtualComponentsAsync(device, ComponentsWithValues(101.0m, 51.0m, configurationRevision: 9));

        // Assert
        Assert.Null(isPhaseNettedWhileMissing);
        Assert.Null(importedWhileMissing);
        Assert.Null(exportedWhileMissing);
        Assert.True(device.EnergyMeter.IsTotalEnergyPhaseNetted);
        Assert.Equal(101.0m, device.EnergyMeter.TotalImportedEnergy);
        Assert.Equal(51.0m, device.EnergyMeter.TotalExportedEnergy);
    }

    [Fact]
    public async Task WhenScriptStartsWhileFallbackIsActive_ThenIgnoredZerosLeaveNoPhaseSums()
    {
        // Arrange
        var device = CreateDeviceWithDeviceCounters(configurationRevision: 7, deviceImported: 1000.0m, deviceExported: 200.0m);
        await ReadVirtualComponentsAsync(device, NoComponents);
        device.ParseStatusComponents(Json("""{ "sys": { "cfg_rev": 8 } }"""), isPartialUpdate: true);

        // Act
        await ReadVirtualComponentsAsync(device, ComponentsWithValues(0.0m, 0.0m, configurationRevision: 8));

        // Assert
        Assert.True(device.EnergyMeter!.IsTotalEnergyPhaseNetted);
        Assert.Null(device.EnergyMeter.TotalImportedEnergy);
        Assert.Null(device.EnergyMeter.TotalExportedEnergy);
    }

    [Fact]
    public async Task WhenComponentsAndDeviceCountersReportZeroOnReconnect_ThenCountersStayUnset()
    {
        // Arrange
        var device = CreateDeviceWithDeviceCounters(configurationRevision: 7, deviceImported: 0.0m, deviceExported: 0.0m);

        // Act
        await ReadVirtualComponentsAsync(device, ComponentsWithValues(0.0m, 0.0m));

        // Assert
        Assert.True(device.EnergyMeter!.IsTotalEnergyPhaseNetted);
        Assert.Null(device.EnergyMeter.TotalImportedEnergy);
        Assert.Null(device.EnergyMeter.TotalExportedEnergy);
    }

    [Fact]
    public async Task WhenComponentValueIsMissing_ThenCurrentValueIsKept()
    {
        // Arrange
        var device = CreateDeviceWithDeviceCounters(configurationRevision: 7, deviceImported: 1000.0m, deviceExported: 200.0m);
        await ReadVirtualComponentsAsync(device, MappedComponents);

        // Act
        await ReadVirtualComponentsAsync(device, """
            {
                "components": [
                    { "key": "number:201", "status": { "value": 101.0 }, "config": { "id": 201, "name": "TotalImportedEnergy" } },
                    { "key": "number:205", "status": {}, "config": { "id": 205, "name": "TotalExportedEnergy" } }
                ],
                "cfg_rev": 7, "offset": 0, "total": 2
            }
            """);

        // Assert
        Assert.Equal(101.0m, device.EnergyMeter!.TotalImportedEnergy);
        Assert.Equal(50.0m, device.EnergyMeter.TotalExportedEnergy);
    }

    [Fact]
    public async Task WhenNettedExportIsZeroOnReconnect_ThenZeroIsApplied()
    {
        // Arrange
        var device = CreateDeviceWithDeviceCounters(configurationRevision: 7, deviceImported: 1000.0m, deviceExported: 0.8m);

        // Act
        await ReadVirtualComponentsAsync(device, ComponentsWithValues(1001.0m, 0.0m));

        // Assert
        Assert.True(device.EnergyMeter!.IsTotalEnergyPhaseNetted);
        Assert.Equal(1001.0m, device.EnergyMeter.TotalImportedEnergy);
        Assert.Equal(0.0m, device.EnergyMeter.TotalExportedEnergy);
    }

    [Fact]
    public async Task WhenOneCounterDropsToZero_ThenCurrentValueIsKept()
    {
        // Arrange
        var device = CreateDeviceWithDeviceCounters(configurationRevision: 7, deviceImported: 1000.0m, deviceExported: 200.0m);
        await ReadVirtualComponentsAsync(device, MappedComponents);

        // Act
        await ReadVirtualComponentsAsync(device, ComponentsWithValues(101.0m, 0.0m));

        // Assert
        Assert.Equal(101.0m, device.EnergyMeter!.TotalImportedEnergy);
        Assert.Equal(50.0m, device.EnergyMeter.TotalExportedEnergy);
    }

    [Fact]
    public async Task WhenComponentNamesAreDuplicated_ThenFirstComponentsAreUsed()
    {
        // Arrange
        var device = CreateDeviceWithDeviceCounters(configurationRevision: 7, deviceImported: 1000.0m, deviceExported: 200.0m);

        // Act
        await ReadVirtualComponentsAsync(device, """
            {
                "components": [
                    { "key": "number:200", "status": { "value": 1001.0 }, "config": { "id": 200, "name": "TotalImportedEnergy" } },
                    { "key": "number:201", "status": { "value": 201.0 }, "config": { "id": 201, "name": "TotalExportedEnergy" } },
                    { "key": "number:202", "status": { "value": 5.0 }, "config": { "id": 202, "name": "TotalImportedEnergy" } },
                    { "key": "number:203", "status": { "value": 6.0 }, "config": { "id": 203, "name": "TotalExportedEnergy" } }
                ],
                "cfg_rev": 7, "offset": 0, "total": 4
            }
            """);

        // Assert
        Assert.Equal(1001.0m, device.EnergyMeter!.TotalImportedEnergy);
        Assert.Equal(201.0m, device.EnergyMeter.TotalExportedEnergy);
    }

    [Fact]
    public async Task WhenComponentsReportZeroAfterReboot_ThenCurrentValuesAreKept()
    {
        // Arrange
        var device = CreateDeviceWithDeviceCounters(configurationRevision: 7, deviceImported: 1000.0m, deviceExported: 200.0m);
        await ReadVirtualComponentsAsync(device, MappedComponents);

        // Act
        await ReadVirtualComponentsAsync(device, ComponentsWithValues(0.0m, 0.0m));

        // Assert
        Assert.Equal(100.0m, device.EnergyMeter!.TotalImportedEnergy);
        Assert.Equal(50.0m, device.EnergyMeter.TotalExportedEnergy);
    }

    [Fact]
    public async Task WhenComponentsReportZeroAfterReconnect_ThenCountersStayUnset()
    {
        // Arrange
        var device = CreateDeviceWithDeviceCounters(configurationRevision: 7, deviceImported: 1000.0m, deviceExported: 200.0m);

        // Act
        await ReadVirtualComponentsAsync(device, ComponentsWithValues(0.0m, 0.0m));

        // Assert
        Assert.True(device.EnergyMeter!.IsTotalEnergyPhaseNetted);
        Assert.Null(device.EnergyMeter.TotalImportedEnergy);
        Assert.Null(device.EnergyMeter.TotalExportedEnergy);
    }

    [Fact]
    public async Task WhenOnlyRejectedScriptValuesWereRead_ThenMissingComponentsFallBackToThePhaseSums()
    {
        // Arrange
        var device = CreateDeviceWithDeviceCounters(configurationRevision: 7, deviceImported: 1000.0m, deviceExported: 200.0m);
        await ReadVirtualComponentsAsync(device, ComponentsWithValues(0.0m, 0.0m));
        device.ParseStatusComponents(Json("""{ "sys": { "cfg_rev": 8 } }"""), isPartialUpdate: true);

        // Act
        await ReadVirtualComponentsAsync(device, """{ "components": [], "cfg_rev": 8, "offset": 0, "total": 0 }""");

        // Assert
        Assert.False(device.EnergyMeter!.IsTotalEnergyPhaseNetted);
        Assert.Equal(1000.0m, device.EnergyMeter.TotalImportedEnergy);
        Assert.Equal(200.0m, device.EnergyMeter.TotalExportedEnergy);
    }

    [Fact]
    public async Task WhenNoComponentsExist_ThenReadsWaitForConfigurationChange()
    {
        // Arrange
        var device = CreateDeviceWithEnergyMeter(configurationRevision: 7);
        var readCount = 0;
        var handler = new StubHttpMessageHandler(_ => JsonResponse(++readCount == 1
            ? NoComponents
            : ComponentsWithValues(1.0m, 2.0m, configurationRevision: 8)));
        using var client = new HttpClient(handler);

        // Act
        await device.ReadVirtualEnergyCountersAsync(client, CancellationToken.None);
        await device.ReadVirtualEnergyCountersAsync(client, CancellationToken.None);
        var requestCountBeforeConfigurationChange = handler.RequestUris.Count;
        device.ParseStatusComponents(Json("""{ "sys": { "cfg_rev": 8 } }"""), isPartialUpdate: true);
        await device.ReadVirtualEnergyCountersAsync(client, CancellationToken.None);

        // Assert
        Assert.Equal(1, requestCountBeforeConfigurationChange);
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.Equal(1.0m, device.EnergyMeter!.TotalImportedEnergy);
        Assert.Equal(2.0m, device.EnergyMeter.TotalExportedEnergy);
    }

    [Fact]
    public async Task WhenComponentsRequestTimesOut_ThenNextPollRetries()
    {
        // Arrange
        var device = CreateDeviceWithEnergyMeter(configurationRevision: 7);
        var handler = new StubHttpMessageHandler(_ =>
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException()));
        using var client = new HttpClient(handler);

        // Act
        await device.ReadVirtualEnergyCountersAsync(client, CancellationToken.None);
        await device.ReadVirtualEnergyCountersAsync(client, CancellationToken.None);

        // Assert
        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Fact]
    public async Task WhenReadsFailBeforeFirstSuccess_ThenSourceAndCountersStayUnknown()
    {
        // Arrange
        var device = CreateDeviceWithDeviceCounters(configurationRevision: 7, deviceImported: 1000.0m, deviceExported: 200.0m);
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var client = new HttpClient(handler);

        // Act
        await device.ReadVirtualEnergyCountersAsync(client, CancellationToken.None);
        await device.ReadVirtualEnergyCountersAsync(client, CancellationToken.None);

        // Assert
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.Null(device.EnergyMeter!.IsTotalEnergyPhaseNetted);
        Assert.Null(device.EnergyMeter.TotalImportedEnergy);
        Assert.Null(device.EnergyMeter.TotalExportedEnergy);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task WhenComponentsRequestFailsWithStatusOtherThanNotFound_ThenValuesAreKeptAndNextPollRetries(HttpStatusCode statusCode)
    {
        // Arrange
        var device = CreateDeviceWithEnergyMeter(configurationRevision: 7);
        await ReadVirtualComponentsAsync(device, MappedComponents);
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode));
        using var client = new HttpClient(handler);

        // Act
        await device.ReadVirtualEnergyCountersAsync(client, CancellationToken.None);
        await device.ReadVirtualEnergyCountersAsync(client, CancellationToken.None);

        // Assert
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.True(device.EnergyMeter!.IsTotalEnergyPhaseNetted);
        Assert.Equal(100.0m, device.EnergyMeter.TotalImportedEnergy);
        Assert.Equal(50.0m, device.EnergyMeter.TotalExportedEnergy);
    }

    [Fact]
    public async Task WhenComponentsResponseIsMalformed_ThenSourceStaysUnknownAndNextPollRetries()
    {
        // Arrange
        var device = CreateDeviceWithDeviceCounters(configurationRevision: 7, deviceImported: 1000.0m, deviceExported: 200.0m);
        var handler = new StubHttpMessageHandler(_ => JsonResponse("{ not json"));
        using var client = new HttpClient(handler);

        // Act
        await device.ReadVirtualEnergyCountersAsync(client, CancellationToken.None);
        await device.ReadVirtualEnergyCountersAsync(client, CancellationToken.None);

        // Assert
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.Null(device.EnergyMeter!.IsTotalEnergyPhaseNetted);
        Assert.Null(device.EnergyMeter.TotalImportedEnergy);
    }

    [Fact]
    public async Task WhenComponentsRequestReturnsNotFound_ThenCountersFallBackAndReadsWait()
    {
        // Arrange
        var device = CreateDeviceWithDeviceCounters(configurationRevision: 7, deviceImported: 1000.0m, deviceExported: 200.0m);
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = new HttpClient(handler);

        // Act
        await device.ReadVirtualEnergyCountersAsync(client, CancellationToken.None);
        await device.ReadVirtualEnergyCountersAsync(client, CancellationToken.None);

        // Assert
        Assert.Single(handler.RequestUris);
        Assert.False(device.EnergyMeter!.IsTotalEnergyPhaseNetted);
        Assert.Equal(1000.0m, device.EnergyMeter.TotalImportedEnergy);
        Assert.Equal(200.0m, device.EnergyMeter.TotalExportedEnergy);
    }

    [Fact]
    public async Task WhenConfigurationRevisionChangesAfterNotFound_ThenComponentsAreReadAgain()
    {
        // Arrange
        var device = CreateDeviceWithDeviceCounters(configurationRevision: 7, deviceImported: 1000.0m, deviceExported: 200.0m);
        var readCount = 0;
        var handler = new StubHttpMessageHandler(_ => ++readCount == 1
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : JsonResponse(ComponentsWithValues(1000.5m, 200.5m, configurationRevision: 8)));
        using var client = new HttpClient(handler);
        await device.ReadVirtualEnergyCountersAsync(client, CancellationToken.None);

        // Act
        device.ParseStatusComponents(Json("""{ "sys": { "cfg_rev": 8 } }"""), isPartialUpdate: true);
        await device.ReadVirtualEnergyCountersAsync(client, CancellationToken.None);

        // Assert
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.True(device.EnergyMeter!.IsTotalEnergyPhaseNetted);
        Assert.Equal(1000.5m, device.EnergyMeter.TotalImportedEnergy);
        Assert.Equal(200.5m, device.EnergyMeter.TotalExportedEnergy);
    }

    [Fact]
    public async Task WhenComponentsRequestReturnsNotFoundAfterScriptValues_ThenCountersAreUnknown()
    {
        // Arrange
        var device = CreateDeviceWithDeviceCounters(configurationRevision: 7, deviceImported: 1000.0m, deviceExported: 200.0m);
        await ReadVirtualComponentsAsync(device, MappedComponents);
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = new HttpClient(handler);

        // Act
        await device.ReadVirtualEnergyCountersAsync(client, CancellationToken.None);

        // Assert
        Assert.Null(device.EnergyMeter!.IsTotalEnergyPhaseNetted);
        Assert.Null(device.EnergyMeter.TotalImportedEnergy);
        Assert.Null(device.EnergyMeter.TotalExportedEnergy);
    }

    [Fact]
    public async Task WhenComponentsArePaged_ThenAllPagesAreRead()
    {
        // Arrange
        var device = CreateDeviceWithEnergyMeter(configurationRevision: 7);
        var handler = new StubHttpMessageHandler(request => GetOffset(request) switch
        {
            0 => JsonResponse("""
                {
                    "components": [ { "key": "number:201", "status": { "value": 100.0 }, "config": { "id": 201, "name": "TotalImportedEnergy" } } ],
                    "cfg_rev": 7, "offset": 0, "total": 2
                }
                """),
            1 => JsonResponse("""
                {
                    "components": [ { "key": "number:205", "status": { "value": 50.0 }, "config": { "id": 205, "name": "TotalExportedEnergy" } } ],
                    "cfg_rev": 7, "offset": 1, "total": 2
                }
                """),
            _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        });
        using var client = new HttpClient(handler);

        // Act
        await device.ReadVirtualEnergyCountersAsync(client, CancellationToken.None);

        // Assert
        Assert.Equal([0, 1], handler.RequestUris.Select(GetOffset));
        Assert.Equal(100.0m, device.EnergyMeter!.TotalImportedEnergy);
        Assert.Equal(50.0m, device.EnergyMeter.TotalExportedEnergy);
    }

    [Fact]
    public async Task WhenDeviceIgnoresOffset_ThenPagingStops()
    {
        // Arrange
        var device = CreateDeviceWithEnergyMeter(configurationRevision: 7);
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""
            {
                "components": [
                    { "key": "number:201", "status": { "value": 100.0 }, "config": { "id": 201, "name": "TotalImportedEnergy" } },
                    { "key": "number:205", "status": { "value": 50.0 }, "config": { "id": 205, "name": "TotalExportedEnergy" } }
                ],
                "cfg_rev": 7, "offset": 0, "total": 5
            }
            """));
        using var client = new HttpClient(handler);

        // Act
        await device.ReadVirtualEnergyCountersAsync(client, CancellationToken.None);

        // Assert
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.Equal(100.0m, device.EnergyMeter!.TotalImportedEnergy);
    }

    [Fact]
    public async Task WhenRevisionChangesWhilePaging_ThenFirstPageRevisionIsRecorded()
    {
        // Arrange
        var device = CreateDeviceWithEnergyMeter(configurationRevision: 7);
        var handler = new StubHttpMessageHandler(request => JsonResponse($$"""
            {
                "components": [ { "key": "text:20{{GetOffset(request)}}", "config": { "id": 200, "name": "A" } } ],
                "cfg_rev": {{7 + GetOffset(request)}}, "offset": {{GetOffset(request)}}, "total": 2
            }
            """));
        using var client = new HttpClient(handler);

        // Act
        await device.ReadVirtualEnergyCountersAsync(client, CancellationToken.None);
        await device.ReadVirtualEnergyCountersAsync(client, CancellationToken.None);
        var requestCountBeforeConfigurationChange = handler.RequestUris.Count;
        device.ParseStatusComponents(Json("""{ "sys": { "cfg_rev": 8 } }"""), isPartialUpdate: true);
        await device.ReadVirtualEnergyCountersAsync(client, CancellationToken.None);

        // Assert
        Assert.Equal(2, requestCountBeforeConfigurationChange);
        Assert.Equal(4, handler.RequestUris.Count);
    }

    [Fact]
    public async Task WhenComponentsResponseLacksConfigurationRevision_ThenDeviceRevisionIsRecorded()
    {
        // Arrange
        var device = CreateDeviceWithEnergyMeter(configurationRevision: 12);
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""{ "components": [], "offset": 0, "total": 0 }"""));
        using var client = new HttpClient(handler);

        // Act
        await device.ReadVirtualEnergyCountersAsync(client, CancellationToken.None);
        await device.ReadVirtualEnergyCountersAsync(client, CancellationToken.None);

        // Assert
        Assert.Single(handler.RequestUris);
    }

    [Fact]
    public async Task WhenDeviceHasNoEnergyMeter_ThenComponentsAreNotRead()
    {
        // Arrange
        var device = CreateDevice();
        device.HostAddress = "shelly.test";
        device.ParseStatusComponents(Json("""{ "switch:0": { "id": 0, "output": true }, "sys": { "cfg_rev": 5 } }"""));
        var handler = new StubHttpMessageHandler(_ => JsonResponse(MappedComponents));
        using var client = new HttpClient(handler);

        // Act
        await device.ReadVirtualEnergyCountersAsync(client, CancellationToken.None);

        // Assert
        Assert.Empty(handler.RequestUris);
    }

    [Fact]
    public async Task WhenHostAddressChanges_ThenDeviceStartsWithNewEnergyMeter()
    {
        // Arrange
        var device = CreateDeviceWithDeviceCounters(configurationRevision: 7, deviceImported: 1000.0m, deviceExported: 200.0m);
        device.ParseStatusComponents(Json("""{ "switch:0": { "id": 0, "output": true } }"""), isPartialUpdate: false);
        await ReadVirtualComponentsAsync(device, MappedComponents);
        var previousEnergyMeter = device.EnergyMeter;

        // Act
        device.ResetForConfigurationChange();
        var energyMeterAfterReset = device.EnergyMeter;
        device.ParseStatusComponents(Json(DeviceCountersStatus(7, 1000.0m, 200.0m)));
        await ReadVirtualComponentsAsync(device, NoComponents);

        // Assert
        Assert.Null(energyMeterAfterReset);
        Assert.Empty(device.Switches);
        Assert.NotSame(previousEnergyMeter, device.EnergyMeter);
        Assert.False(device.EnergyMeter!.IsTotalEnergyPhaseNetted);
        Assert.Equal(1000.0m, device.EnergyMeter.TotalImportedEnergy);
    }

    [Fact]
    public void WhenComponentsResponseIsPaged_ThenNextOffsetAndTotalAreReturned()
    {
        // Arrange
        var numbers = new List<ShellyVirtualNumber>();

        // Act
        var (nextOffset, total, configurationRevision) = ShellyVirtualEnergyCounters.ReadVirtualNumbers(Json("""
        {
            "components": [
                { "key": "number:200", "config": { "id": 200, "name": "A" } },
                { "key": "text:200", "config": { "id": 200, "name": "B" } }
            ],
            "cfg_rev": 9, "offset": 4, "total": 10
        }
        """), numbers);

        // Assert
        Assert.Equal(6, nextOffset);
        Assert.Equal(10, total);
        Assert.Equal(9, configurationRevision);
        Assert.Equal(new[] { new ShellyVirtualNumber("A", null) }, numbers);
    }

    [Fact]
    public void WhenComponentsResponseHasMalformedComponents_ThenTheyAreSkipped()
    {
        // Arrange
        var numbers = new List<ShellyVirtualNumber>();

        // Act
        var (nextOffset, total, configurationRevision) = ShellyVirtualEnergyCounters.ReadVirtualNumbers(Json("""
        {
            "components": [
                "number:199",
                { "key": 200 },
                { "key": "number:201", "config": "TotalImportedEnergy", "status": { "value": "x" } },
                { "key": "number:202", "config": { "name": 5 }, "status": 7 },
                { "key": "number:203", "config": { "name": "TotalExportedEnergy" }, "status": { "value": 1e40 } },
                { "key": "number:204", "config": { "name": "TotalImportedEnergy" }, "status": { "value": 12.5 } }
            ],
            "cfg_rev": "9", "offset": 0, "total": 6
        }
        """), numbers);

        // Assert
        Assert.Equal(6, nextOffset);
        Assert.Equal(6, total);
        Assert.Null(configurationRevision);
        Assert.Equal(new[]
        {
            new ShellyVirtualNumber(null, null),
            new ShellyVirtualNumber(null, null),
            new ShellyVirtualNumber("TotalExportedEnergy", null),
            new ShellyVirtualNumber("TotalImportedEnergy", 12.5m)
        }, numbers);
    }

    [Fact]
    public void WhenComponentsResponseIsNotAnObject_ThenNothingIsRead()
    {
        // Arrange
        var numbers = new List<ShellyVirtualNumber>();

        // Act
        var (nextOffset, total, configurationRevision) = ShellyVirtualEnergyCounters.ReadVirtualNumbers(Json("[]"), numbers);

        // Assert
        Assert.Equal(0, nextOffset);
        Assert.Equal(0, total);
        Assert.Null(configurationRevision);
        Assert.Empty(numbers);
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

    private static int GetOffset(HttpRequestMessage request) => GetOffset(request.RequestUri!);

    private static int GetOffset(Uri uri)
    {
        var parameter = uri.Query.TrimStart('?').Split('&').Single(part => part.StartsWith("offset=", StringComparison.Ordinal));
        return int.Parse(parameter["offset=".Length..], CultureInfo.InvariantCulture);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }
}
