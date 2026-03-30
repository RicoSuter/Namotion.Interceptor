using System.ComponentModel;
using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices;
using HomeBlaze.Abstractions.Networking;
using HomeBlaze.Abstractions.Sensors;
using HueApi;
using HueApi.BridgeLocator;
using HueApi.Models.Responses;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Newtonsoft.Json.Linq;

namespace Namotion.Devices.Philips.Hue;

/// <summary>
/// Philips Hue Bridge for controlling lights, sensors, and buttons.
/// Discovers and manages child devices via the Hue API.
/// </summary>
[Category("Devices")]
[Description("Philips Hue Bridge for controlling lights, sensors, and buttons")]
[InterceptorSubject]
public partial class HueBridge : BackgroundService,
    IConfigurable,
    IMonitoredService,
    IHubDevice,
    IConnectionState,
    IPowerSensor,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HueBridge> _logger;
    private readonly SemaphoreSlim _configChangedSignal = new(0, 1);

    private LocatedBridge? _bridge;

    [Configuration]
    public partial string? BridgeId { get; set; }

    [Configuration]
    public partial string? AppKey { get; set; }

    [Configuration]
    public partial TimeSpan PollingInterval { get; set; }

    [Configuration]
    public partial TimeSpan RetryInterval { get; set; }

    [State]
    public partial bool IsConnected { get; set; }

    [State]
    public partial HueLightbulb[] Lights { get; set; }

    [State]
    public partial HueMotionDevice[] MotionSensors { get; set; }

    [State]
    public partial HueButtonDevice[] ButtonDevices { get; set; }

    [State]
    public partial HueGroup[] Rooms { get; set; }

    [State]
    public partial HueGroup[] Zones { get; set; }

    [State]
    public partial HueDevice[] Devices { get; set; }

    [State]
    public partial DateTimeOffset? LastUpdated { get; set; }

    [State]
    public partial ServiceStatus Status { get; set; }

    [State]
    public partial string? StatusMessage { get; set; }

    [Derived]
    public string? Title => "Hue Bridge (" + (_bridge?.IpAddress ?? "?") + ")";

    [Derived]
    public string? IconName => IsConnected ? "Hub" : "HubOutlined";

    [Derived]
    public string? IconColor =>
        IsConnected ? "Success" :
        Status == ServiceStatus.Error ? "Error" : "Warning";

    [Derived]
    public decimal? Power => IsConnected ? 3.0m : null;

    [Derived]
    public decimal? EnergyConsumed => null;

    public HueBridge(
        IHttpClientFactory httpClientFactory,
        ILogger<HueBridge> logger,
        IInterceptorSubjectContext context)
    {
        ((IInterceptorSubject)this).Context.AddFallbackContext(context);

        _httpClientFactory = httpClientFactory;
        _logger = logger;

        PollingInterval = TimeSpan.FromMilliseconds(500);
        RetryInterval = TimeSpan.FromSeconds(30);

        Lights = [];
        MotionSensors = [];
        ButtonDevices = [];
        Rooms = [];
        Zones = [];
        Devices = [];
    }

    /// <summary>
    /// Creates a new authenticated Hue API client.
    /// </summary>
    public LocalHueApi CreateClient()
    {
        if (AppKey == null || _bridge == null)
        {
            throw new InvalidOperationException("Bridge is not configured or not discovered.");
        }

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        var httpClient = new HttpClient(handler);
        return new LocalHueApi(_bridge.IpAddress, AppKey, httpClient);
    }

    /// <inheritdoc />
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        if (_configChangedSignal.CurrentCount == 0)
        {
            _configChangedSignal.Release();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            HttpClient? httpClient = null;
            LocalHueApi? client = null;

            try
            {
                Status = ServiceStatus.Starting;
                StatusMessage = null;

                if (string.IsNullOrEmpty(BridgeId) || string.IsNullOrEmpty(AppKey))
                {
                    Status = ServiceStatus.Error;
                    StatusMessage = "Bridge not configured. Set BridgeId and AppKey.";
                    try
                    {
                        await _configChangedSignal.WaitAsync(stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }

                // Discovery
                var bridges = await HueBridgeDiscovery.FastDiscoveryWithNetworkScanFallbackAsync(
                    TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));

                var bridge = bridges.FirstOrDefault(locatedBridge => locatedBridge.BridgeId == BridgeId);
                if (bridge == null)
                {
                    StatusMessage = "Bridge not found on network";
                    try
                    {
                        await Task.Delay(RetryInterval, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }

                _bridge = bridge;

                // Create client with HTTPS cert acceptance
                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback =
                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                };
                httpClient = new HttpClient(handler);
                client = new LocalHueApi(bridge.IpAddress, AppKey, httpClient);

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

                Status = ServiceStatus.Running;
                IsConnected = true;
                StatusMessage = null;

                // Initial poll
                await PollDevicesAsync(client, linkedCts.Token);

                // Run event stream + periodic poll in parallel
                var eventStreamTask = RunEventStreamAsync(client, linkedCts.Token);
                var pollingTask = RunPollingLoopAsync(client, linkedCts.Token);

                await Task.WhenAll(eventStreamTask, pollingTask);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Hue Bridge connection failed.");
                IsConnected = false;
                Status = ServiceStatus.Error;
                StatusMessage = exception.Message;

                try
                {
                    await Task.Delay(RetryInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                IsConnected = false;

                if (client is not null)
                {
                    client.StopEventStream();
                }

                httpClient?.Dispose();
            }
        }

        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }

    private async Task RunEventStreamAsync(LocalHueApi client, CancellationToken cancellationToken)
    {
        client.OnEventStreamMessage += OnEventStreamMessage;
        try
        {
            await client.StartEventStream(cancellationToken: cancellationToken);
        }
        finally
        {
            client.OnEventStreamMessage -= OnEventStreamMessage;
        }
    }

    private async Task RunPollingLoopAsync(LocalHueApi client, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
            await PollDevicesAsync(client, cancellationToken);
        }
    }

    private async Task PollDevicesAsync(LocalHueApi client, CancellationToken cancellationToken)
    {
        var zigbeeConnectivities = await client.GetZigbeeConnectivityAsync();
        var devicePowers = await client.GetDevicePowersAsync();

        var devices = await client.GetDevicesAsync();
        var rooms = await client.GetRoomsAsync();
        var zones = await client.GetZonesAsync();

        var lights = await client.GetLightsAsync();
        var buttons = await client.GetButtonsAsync();
        var motions = await client.GetMotionsAsync();
        var groupedLights = await client.GetGroupedLightsAsync();
        var temperatures = await client.GetTemperaturesAsync();
        var lightLevels = await client.GetLightLevelsAsync();

        var existingDevices = Devices;

        var allDevices = devices.Data.Select(device =>
        {
            var services = device.Services?.Select(service => service.Rid).ToArray() ?? [];
            var zigbeeConnectivity = zigbeeConnectivities.Data.SingleOrDefault(item => services.Contains(item.Id));
            var devicePower = devicePowers.Data.SingleOrDefault(item => services.Contains(item.Id));

            // Try to find existing device by ResourceId
            var existingDevice = existingDevices.SingleOrDefault(existing => existing.ResourceId == device.Id);

            // Light device
            var lightService = device.Services?.SingleOrDefault(service => service.Rtype == "light");
            if (lightService is not null)
            {
                var light = lights.Data.SingleOrDefault(item => item.Id == lightService.Rid);
                if (light is not null)
                {
                    if (existingDevice is HueLightbulb existingLightbulb)
                    {
                        return existingLightbulb.Update(device, zigbeeConnectivity, light);
                    }

                    return new HueLightbulb(device, zigbeeConnectivity, light, this);
                }
            }

            // Motion sensor device
            var motionService = device.Services?.SingleOrDefault(service => service.Rtype == "motion");
            if (motionService is not null)
            {
                var motion = motions.Data.SingleOrDefault(item => item.Id == motionService.Rid);
                if (motion is not null)
                {
                    var temperature = temperatures.Data.SingleOrDefault(item => services.Contains(item.Id));
                    var lightLevel = lightLevels.Data.SingleOrDefault(item => services.Contains(item.Id));

                    if (existingDevice is HueMotionDevice existingMotion)
                    {
                        return existingMotion.Update(device, zigbeeConnectivity, devicePower, temperature, lightLevel, motion);
                    }

                    return new HueMotionDevice(device, zigbeeConnectivity, devicePower, temperature, lightLevel, motion, this);
                }
            }

            // Button device
            var buttonServices = device.Services?
                .Where(service => service.Rtype == "button")
                .Select(service => buttons.Data.SingleOrDefault(button => button.Id == service.Rid))
                .Where(button => button is not null)
                .ToArray();

            if (buttonServices is not null && buttonServices.Length > 0)
            {
                if (existingDevice is HueButtonDevice existingButtonDevice)
                {
                    return existingButtonDevice.Update(device, zigbeeConnectivity, devicePower, buttonServices!);
                }

                return new HueButtonDevice(device, zigbeeConnectivity, devicePower, buttonServices!, this);
            }

            // Generic device
            if (existingDevice is not null)
            {
                return existingDevice.Update(device, zigbeeConnectivity);
            }

            return new HueDevice(device, zigbeeConnectivity, this);
        })
        .OrderBy(device => device.Title)
        .ToArray();

        Lights = allDevices.OfType<HueLightbulb>().ToArray();
        MotionSensors = allDevices.OfType<HueMotionDevice>().ToArray();
        ButtonDevices = allDevices.OfType<HueButtonDevice>().ToArray();
        Devices = allDevices;

        // Rooms
        var existingRooms = Rooms;
        Rooms = rooms.Data.Select(room =>
        {
            var roomServices = room.Services?.Select(service => service.Rid).ToArray() ?? [];
            var groupedLight = groupedLights.Data.SingleOrDefault(grouped => roomServices.Contains(grouped.Id));
            var roomLights = allDevices
                .OfType<HueLightbulb>()
                .Where(light => room.Children.Any(child => child.Rid == light.ResourceId || child.Rid == light.ReferenceId))
                .ToArray();

            var existingRoom = existingRooms.SingleOrDefault(existing => existing.ResourceId == room.Id);
            if (existingRoom is not null)
            {
                return existingRoom.Update(room, groupedLight, roomLights);
            }

            return new HueGroup(room, groupedLight, roomLights, this);
        })
        .OrderBy(room => room.Title)
        .ToArray();

        // Zones
        var existingZones = Zones;
        Zones = zones.Data.Select(zone =>
        {
            var zoneServices = zone.Services?.Select(service => service.Rid).ToArray() ?? [];
            var groupedLight = groupedLights.Data.SingleOrDefault(grouped => zoneServices.Contains(grouped.Id));
            var zoneLights = allDevices
                .OfType<HueLightbulb>()
                .Where(light => zone.Children.Any(child => child.Rid == light.ResourceId || child.Rid == light.ReferenceId))
                .ToArray();

            var existingZone = existingZones.SingleOrDefault(existing => existing.ResourceId == zone.Id);
            if (existingZone is not null)
            {
                return existingZone.Update(zone, groupedLight, zoneLights);
            }

            return new HueGroup(zone, groupedLight, zoneLights, this);
        })
        .OrderBy(zone => zone.Title)
        .ToArray();

        LastUpdated = DateTimeOffset.Now;
    }

    private void OnEventStreamMessage(string bridgeIp, List<EventStreamResponse> events)
    {
        foreach (var eventResponse in events)
        {
            foreach (var data in eventResponse.Data)
            {
                if (data.Type == "button")
                {
                    var buttonDevice = Devices
                        .OfType<HueButtonDevice>()
                        .SingleOrDefault(device => device.ResourceId == data.Owner?.Rid);

                    var button = buttonDevice?.Buttons.SingleOrDefault(existingButton => existingButton.ResourceId == data.Id);
                    if (button is not null)
                    {
                        button.ButtonResource = Merge(button.ButtonResource, data);
                        button.LastUpdated = DateTimeOffset.Now;
                        button.RefreshButtonState();
                    }
                }
                else if (data.Type == "light")
                {
                    var lightDevice = Devices
                        .OfType<HueLightbulb>()
                        .SingleOrDefault(device => device.ResourceId == data.Owner?.Rid);

                    if (lightDevice is not null)
                    {
                        lightDevice.LightResource = Merge(lightDevice.LightResource, data);
                        lightDevice.LastUpdated = DateTimeOffset.Now;
                    }
                }
                else if (data.Type == "grouped_light")
                {
                    var group = Rooms
                        .Union(Zones)
                        .SingleOrDefault(existingGroup => existingGroup.GroupedLight?.Id == data.Id);

                    if (group is not null && group.GroupedLight is not null)
                    {
                        group.GroupedLight = Merge(group.GroupedLight, data);
                        group.LastUpdated = DateTimeOffset.Now;
                    }
                }
                else if (data.Type == "motion")
                {
                    var motionDevice = Devices
                        .OfType<HueMotionDevice>()
                        .SingleOrDefault(device => device.MotionResource?.Id == data.Id);

                    if (motionDevice is not null)
                    {
                        motionDevice.MotionResource = Merge(motionDevice.MotionResource, data);
                        motionDevice.LastUpdated = DateTimeOffset.Now;
                    }
                }
                else if (data.Type == "temperature")
                {
                    var motionDevice = Devices
                        .OfType<HueMotionDevice>()
                        .SingleOrDefault(device => device.TemperatureResource?.Id == data.Id);

                    if (motionDevice is not null)
                    {
                        motionDevice.TemperatureResource = Merge(motionDevice.TemperatureResource, data);
                        motionDevice.LastUpdated = DateTimeOffset.Now;
                    }
                }
                else if (data.Type == "light_level")
                {
                    var motionDevice = Devices
                        .OfType<HueMotionDevice>()
                        .SingleOrDefault(device => device.LightLevelResource?.Id == data.Id);

                    if (motionDevice is not null)
                    {
                        motionDevice.LightLevelResource = Merge(motionDevice.LightLevelResource, data);
                        motionDevice.LastUpdated = DateTimeOffset.Now;
                    }
                }
                else if (data.Type == "device_power")
                {
                    var motionDevice = Devices
                        .OfType<HueMotionDevice>()
                        .SingleOrDefault(device => device.DevicePowerResource?.Id == data.Id);

                    if (motionDevice is not null)
                    {
                        motionDevice.DevicePowerResource = Merge(motionDevice.DevicePowerResource, data);
                        motionDevice.LastUpdated = DateTimeOffset.Now;
                    }

                    var buttonDevice = Devices
                        .OfType<HueButtonDevice>()
                        .SingleOrDefault(device => device.DevicePowerResource?.Id == data.Id);

                    if (buttonDevice is not null)
                    {
                        buttonDevice.DevicePowerResource = Merge(buttonDevice.DevicePowerResource, data);
                        buttonDevice.LastUpdated = DateTimeOffset.Now;
                    }
                }
            }
        }
    }

    private static T Merge<T>(T currentResource, EventStreamData newPartialResource)
    {
        var currentJson = JObject.Parse(JsonSerializer.Serialize(currentResource));
        var partialJson = JObject.Parse(JsonSerializer.Serialize(newPartialResource));

        currentJson.Merge(partialJson, new JsonMergeSettings
        {
            MergeArrayHandling = MergeArrayHandling.Union
        });

        return JsonSerializer.Deserialize<T>(currentJson.ToString())!;
    }

    public override void Dispose()
    {
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
        IsConnected = false;
        base.Dispose();
    }
}
