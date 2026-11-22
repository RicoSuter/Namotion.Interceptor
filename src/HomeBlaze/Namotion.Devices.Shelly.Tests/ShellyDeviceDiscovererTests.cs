using System.Net;
using HomeBlaze.Abstractions.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Shelly.Model;
using Xunit;

namespace Namotion.Devices.Shelly.Tests;

public class ShellyDeviceDiscovererTests
{
    private static ShellyDevice CreateDevice() => new(new TestHttpClientFactory(), NullLogger<ShellyDevice>.Instance);

    private static ShellyDiscoveredDevice Found(string deviceId = "shellyplus1pm-a8032ab12345", string hostAddress = "192.168.1.41") =>
        new(deviceId, hostAddress, "Plus1PM", deviceId);

    private static MulticastDnsService Service(
        string instanceName,
        string? hostName = "ShellyPlus1PM-A8032AB12345.local",
        string address = "192.168.1.41",
        string? application = "Plus1PM")
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["gen"] = "2" };
        if (application is not null)
        {
            properties["app"] = application;
        }

        return new MulticastDnsService(instanceName, hostName, 80, [IPAddress.Parse(address)], properties);
    }

    [Fact]
    public void WhenAShellyIsAnnounced_ThenItsDeviceIdAddressAndApplicationAreRead()
    {
        // Arrange
        var service = Service("ShellyPlus1PM-A8032AB12345");

        // Act
        var devices = ShellyDeviceDiscoverer.ToDevices([service]);

        // Assert
        var device = Assert.Single(devices);
        Assert.Equal(Found(), device);
        Assert.Equal("shellyplus1pm-a8032ab12345", device.DisplayName);
        Assert.Equal("Plus1PM at 192.168.1.41", device.Detail);
    }

    [Fact]
    public void WhenADeviceAnnouncesItsIdAndItsUserGivenName_ThenOneDeviceWithTheUserGivenNameIsFound()
    {
        // Arrange
        var services = new[]
        {
            Service("shellyplus2pm-4022d8dbe028", "ShellyPlus2PM-4022D8DBE028.local.", "192.168.1.50", application: null),
            Service("Wohnzimmer Fenster Gross", "ShellyPlus2PM-4022D8DBE028.local.", "192.168.1.50", "Plus2PM")
        };

        // Act
        var devices = ShellyDeviceDiscoverer.ToDevices(services);

        // Assert
        var device = Assert.Single(devices);
        Assert.Equal("shellyplus2pm-4022d8dbe028", device.DeviceId);
        Assert.Equal("shellyplus2pm-4022d8dbe028", device.Key);
        Assert.Equal("Wohnzimmer Fenster Gross", device.DisplayName);
        Assert.Equal("192.168.1.50", device.HostAddress);
        Assert.Equal("Plus2PM", device.Application);
    }

    [Fact]
    public void WhenTwoDevicesAreAnnounced_ThenTwoDevicesAreFound()
    {
        // Arrange
        var services = new[]
        {
            Service("Kitchen", "ShellyPlus1PM-A8032AB12345.local", "192.168.1.41"),
            Service("Garage", "ShellyPlus1PM-A8032AB67890.local", "192.168.1.42")
        };

        // Act
        var devices = ShellyDeviceDiscoverer.ToDevices(services);

        // Assert
        Assert.Equal(2, devices.Count);
        Assert.Equal(
            new ShellyDiscoveredDevice("shellyplus1pm-a8032ab12345", "192.168.1.41", "Plus1PM", "Kitchen"),
            devices[0]);
        Assert.Equal(
            new ShellyDiscoveredDevice("shellyplus1pm-a8032ab67890", "192.168.1.42", "Plus1PM", "Garage"),
            devices[1]);
    }

    [Fact]
    public void WhenAServiceHasNoHostName_ThenItIsSkipped()
    {
        // Arrange
        var services = new[]
        {
            Service("Kitchen", hostName: null),
            Service("Garage", hostName: " . ", address: "192.168.1.42")
        };

        // Act
        var devices = ShellyDeviceDiscoverer.ToDevices(services);

        // Assert
        Assert.Empty(devices);
    }

    [Fact]
    public async Task WhenAScanRuns_ThenTheShellyServiceTypeIsBrowsed()
    {
        // Arrange
        var browser = new AnsweringBrowser(Service("Kitchen"));
        ISubjectDiscoverer discoverer = new ShellyDeviceDiscoverer();
        var scan = new DiscoveryScan(new EmptySsdpSearch(), browser, CancellationToken.None);

        // Act
        var discovered = await discoverer.DiscoverAsync(scan, [], CancellationToken.None).ToListAsync();

        // Assert
        var single = Assert.Single(discovered);
        var device = Assert.IsType<ShellyDiscoveredDevice>(single.Result);
        Assert.Equal("shellyplus1pm-a8032ab12345", device.DeviceId);
        Assert.Equal("Kitchen", device.DisplayName);
        Assert.Equal([ShellyDeviceDiscoverer.ServiceType], browser.ServiceTypes);
    }

    [Fact]
    public void WhenTheSubjectHasADeviceId_ThenTheDeviceIdDecides()
    {
        // Arrange
        var device = CreateDevice();
        device.HostAddress = "192.168.1.99";
        device.DeviceId = "ShellyPlus1PM-A8032AB12345";

        // Act & Assert
        Assert.True(ShellyDeviceDiscoverer.IsSameDevice(device, Found()));
        Assert.False(ShellyDeviceDiscoverer.IsSameDevice(device, Found(deviceId: "shellyplus1pm-other")));
    }

    [Fact]
    public void WhenTheRunningSubjectReadItsDeviceId_ThenThatDecides()
    {
        // Arrange
        var device = CreateDevice();
        device.HostAddress = "192.168.1.99";
        device.DeviceInfo = new ShellyDeviceInfo { Id = "shellyplus1pm-a8032ab12345" };

        // Act & Assert
        Assert.True(ShellyDeviceDiscoverer.IsSameDevice(device, Found()));
    }

    [Fact]
    public void WhenTheRunningSubjectReadAnotherDeviceIdThanTheStoredOne_ThenTheReadOneDecides()
    {
        // Arrange
        var device = CreateDevice();
        device.HostAddress = "192.168.1.41";
        device.DeviceId = "shellyplus1pm-replaced";
        device.DeviceInfo = new ShellyDeviceInfo { Id = "shellyplus1pm-a8032ab12345" };

        // Act & Assert
        Assert.True(ShellyDeviceDiscoverer.IsSameDevice(device, Found()));
        Assert.False(ShellyDeviceDiscoverer.IsSameDevice(device, Found(deviceId: "shellyplus1pm-replaced")));
    }

    [Fact]
    public void WhenTheSubjectHasNoDeviceId_ThenTheHostAddressDecides()
    {
        // Arrange
        var device = CreateDevice();
        device.HostAddress = " 192.168.1.41 ";

        // Act & Assert
        Assert.True(ShellyDeviceDiscoverer.IsSameDevice(device, Found()));
        Assert.False(ShellyDeviceDiscoverer.IsSameDevice(device, Found(hostAddress: "192.168.1.42")));
    }

    [Fact]
    public void WhenTheStoredDeviceIdIsBlank_ThenTheDeviceIdTheRunningSubjectReadDecides()
    {
        // Arrange
        var device = CreateDevice();
        device.HostAddress = "192.168.1.99";
        device.DeviceId = " ";
        device.DeviceInfo = new ShellyDeviceInfo { Id = "shellyplus1pm-a8032ab12345" };

        // Act & Assert
        Assert.True(ShellyDeviceDiscoverer.IsSameDevice(device, Found()));
        Assert.False(ShellyDeviceDiscoverer.IsSameDevice(device, Found(deviceId: "shellyplus1pm-other")));
    }

    private sealed class AnsweringBrowser(params MulticastDnsService[] services) : IMulticastDnsBrowser
    {
        public List<string> ServiceTypes { get; } = [];

        public Task<IReadOnlyList<MulticastDnsService>> BrowseAsync(string serviceType, CancellationToken cancellationToken)
        {
            ServiceTypes.Add(serviceType);
            return Task.FromResult<IReadOnlyList<MulticastDnsService>>(services);
        }
    }

    private sealed class EmptySsdpSearch : ISsdpSearch
    {
        public Task<IReadOnlyList<SsdpResponse>> SearchAsync(string searchTarget, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SsdpResponse>>([]);
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
