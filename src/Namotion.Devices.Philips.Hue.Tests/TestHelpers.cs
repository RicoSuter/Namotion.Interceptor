using HueApi.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;

namespace Namotion.Devices.Philips.Hue.Tests;

internal static class TestHelpers
{
    /// <summary>
    /// Creates a Device with the specified model ID and optional name.
    /// </summary>
    internal static Device CreateDevice(string modelId, string name = "Test Device")
    {
        return new Device
        {
            Id = Guid.NewGuid(),
            Metadata = new Metadata { Name = name },
            ProductData = new ProductData
            {
                ModelId = modelId,
                Certified = true,
                ProductName = "Test Product"
            },
            Services = new List<ResourceIdentifier>()
        };
    }

    /// <summary>
    /// Creates a ZigbeeConnectivity with the specified status.
    /// </summary>
    internal static ZigbeeConnectivity CreateZigbeeConnectivity(ConnectivityStatus status)
    {
        return new ZigbeeConnectivity
        {
            Id = Guid.NewGuid(),
            Status = status
        };
    }

    /// <summary>
    /// Creates a Light resource with the specified on state, brightness, and optional color temperature.
    /// </summary>
    internal static Light CreateLight(
        bool isOn,
        double? brightness = null,
        int? mirek = null,
        int mirekMin = 153,
        int mirekMax = 500,
        string type = "Dimmable light")
    {
        var light = new Light
        {
            Id = Guid.NewGuid(),
            On = new On { IsOn = isOn },
            Type = type
        };

        if (brightness.HasValue)
        {
            light.Dimming = new Dimming { Brightness = brightness.Value };
        }

        if (mirek.HasValue)
        {
            light.ColorTemperature = new ColorTemperature
            {
                Mirek = mirek.Value,
                MirekSchema = new MirekSchema
                {
                    MirekMinimum = mirekMin,
                    MirekMaximum = mirekMax
                }
            };
        }

        return light;
    }

    /// <summary>
    /// Creates a ButtonResource with the specified event and timestamp.
    /// </summary>
    internal static ButtonResource CreateButtonResource(ButtonEvent? buttonEvent, DateTimeOffset? updated = null)
    {
        var resource = new ButtonResource
        {
            Id = Guid.NewGuid(),
        };

        if (buttonEvent.HasValue)
        {
            resource.Button = new HueApi.Models.Button
            {
                ButtonReport = new ButtonReport
                {
                    Event = buttonEvent.Value,
                    Updated = updated ?? DateTimeOffset.UtcNow
                }
            };
        }

        return resource;
    }

    /// <summary>
    /// Creates a GroupedLight with the specified on state and brightness.
    /// </summary>
    internal static GroupedLight CreateGroupedLight(bool isOn, double? brightness = null)
    {
        var groupedLight = new GroupedLight
        {
            Id = Guid.NewGuid(),
            On = new On { IsOn = isOn }
        };

        if (brightness.HasValue)
        {
            groupedLight.Dimming = new Dimming { Brightness = brightness.Value };
        }

        return groupedLight;
    }

    /// <summary>
    /// Creates a Room HueResource with the specified name.
    /// </summary>
    internal static Room CreateRoom(string name = "Test Room")
    {
        return new Room
        {
            Id = Guid.NewGuid(),
            Metadata = new Metadata { Name = name },
            Children = new List<ResourceIdentifier>(),
            Services = new List<ResourceIdentifier>()
        };
    }

    /// <summary>
    /// Creates a test HueBridge with minimal dependencies.
    /// The bridge has a valid context but no real HTTP client or logger.
    /// Tests must not call methods that require network access (e.g., CreateClient).
    /// </summary>
    internal static HueBridge CreateTestBridge()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        services.AddSingleton<ILoggerFactory, NullLoggerFactory>();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        var serviceProvider = services.BuildServiceProvider();
        var logger = serviceProvider.GetRequiredService<ILogger<HueBridge>>();

        var bridge = new HueBridge(logger);
        bridge.BridgeId = "test-bridge-id";
        return bridge;
    }

    /// <summary>
    /// Creates a HueLightbulb for testing without a real bridge.
    /// The bridge parameter is null, so tests must not call methods that use the bridge.
    /// </summary>
    internal static HueLightbulb CreateLightbulb(
        string modelId,
        bool isOn,
        double? brightness = null,
        int? mirek = null,
        int mirekMin = 153,
        int mirekMax = 500,
        string lightType = "Dimmable light",
        ConnectivityStatus connectivityStatus = ConnectivityStatus.connected)
    {
        var device = CreateDevice(modelId);
        var zigbee = CreateZigbeeConnectivity(connectivityStatus);
        var light = CreateLight(isOn, brightness, mirek, mirekMin, mirekMax, lightType);

        return new HueLightbulb(device, zigbee, light, null!);
    }

    /// <summary>
    /// Creates a HueLightbulb without zigbee connectivity (null zigbee).
    /// </summary>
    internal static HueLightbulb CreateLightbulbWithoutZigbee(
        string modelId,
        bool isOn,
        double? brightness = null,
        int? mirek = null,
        int mirekMin = 153,
        int mirekMax = 500,
        string lightType = "Dimmable light")
    {
        var device = CreateDevice(modelId);
        var light = CreateLight(isOn, brightness, mirek, mirekMin, mirekMax, lightType);

        return new HueLightbulb(device, null, light, null!);
    }

    /// <summary>
    /// Creates a HueDevice for testing without a real bridge.
    /// </summary>
    internal static HueDevice CreateHueDevice(
        ConnectivityStatus? connectivityStatus = ConnectivityStatus.connected,
        string name = "Test Device")
    {
        var device = CreateDevice("TEST001", name);
        var zigbee = connectivityStatus.HasValue
            ? CreateZigbeeConnectivity(connectivityStatus.Value)
            : null;

        return new HueDevice(device, zigbee, null!);
    }

    /// <summary>
    /// Creates a HueButtonDevice with a real test bridge so that button event
    /// processing (which accesses Bridge.BridgeId via HueButton.Id) works correctly.
    /// </summary>
    internal static HueButtonDevice CreateButtonDevice(
        ButtonResource[] buttonResources,
        ConnectivityStatus connectivityStatus = ConnectivityStatus.connected)
    {
        var device = CreateDevice("RWL021", "Test Button Device");
        var zigbee = CreateZigbeeConnectivity(connectivityStatus);
        var bridge = CreateTestBridge();

        return new HueButtonDevice(device, zigbee, null, buttonResources, bridge);
    }
}
