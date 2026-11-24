using System.Net;
using HomeBlaze.Abstractions.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Shelly.Model;
using Xunit;

namespace Namotion.Devices.Shelly.Tests;

public class ShellyDeviceDiscoveryTests
{
    private static ShellyDevice CreateDevice() => new(new TestHttpClientFactory(), NullLogger<ShellyDevice>.Instance);

    private static ShellyDiscoveredDevice Found(string? deviceId = "shellyplus1pm-a8032ab12345", string hostAddress = "192.0.2.41", string? name = null) =>
        new(deviceId, hostAddress, "Plus1PM", name);

    private static MulticastDnsServiceInstance Instance(
        string instanceName,
        string? hostName = "ShellyPlus1PM-A8032AB12345.local",
        string address = "192.0.2.41",
        string? application = "Plus1PM")
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["gen"] = "2" };
        if (application is not null)
        {
            properties["app"] = application;
        }

        return new MulticastDnsServiceInstance(instanceName, hostName, [IPAddress.Parse(address)], properties);
    }

    [Fact]
    public void WhenAShellyIsAnnounced_ThenItsDeviceIdAddressAndApplicationAreRead()
    {
        // Arrange
        var instance = Instance("ShellyPlus1PM-A8032AB12345");

        // Act
        var devices = ShellyDeviceDiscovery.ToDevices([instance]);

        // Assert
        var device = Assert.Single(devices);
        Assert.Equal(Found(), device);
        Assert.Equal("shellyplus1pm-a8032ab12345", device.DisplayName);
        Assert.Equal("Plus1PM at 192.0.2.41", device.Detail);
    }

    [Fact]
    public void WhenADeviceAnnouncesItsUserGivenNameOnTheHostOfItsId_ThenOneDeviceWithTheUserGivenNameIsFound()
    {
        // Arrange
        var instances = new[]
        {
            Instance("shellyplus2pm-02abcd000001", "ShellyPlus2PM-02ABCD000001.local.", "192.0.2.50", application: null),
            Instance("Hallway Light", "ShellyPlus2PM-02ABCD000001.local.", "192.0.2.50", "Plus2PM")
        };

        // Act
        var devices = ShellyDeviceDiscovery.ToDevices(instances);

        // Assert
        var device = Assert.Single(devices);
        Assert.Equal("shellyplus2pm-02abcd000001", device.DeviceId);
        Assert.Equal("shellyplus2pm-02abcd000001", device.Key);
        Assert.Equal("Hallway Light", device.Name);
        Assert.Equal("Hallway Light", device.DisplayName);
        Assert.Equal("192.0.2.50", device.HostAddress);
        Assert.Equal("Plus2PM", device.Application);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenADeviceAnnouncesItsUserGivenNameOnAHostOfThatName_ThenOneDeviceWithItsIdAndTheUserGivenNameIsFound(bool isNamedInstanceFirst)
    {
        // Arrange
        var namedInstance = Instance(" Garden Pump ", "Garden-Pump.local", "192.0.2.50", "Plus2PM");
        var idInstance = Instance("shellyplus2pm-02abcd000001", "ShellyPlus2PM-02ABCD000001.local", "192.0.2.50", "Plus2PM");
        MulticastDnsServiceInstance[] instances = isNamedInstanceFirst ? [namedInstance, idInstance] : [idInstance, namedInstance];

        // Act
        var devices = ShellyDeviceDiscovery.ToDevices(instances);

        // Assert
        var device = Assert.Single(devices);
        Assert.Equal(new ShellyDiscoveredDevice("shellyplus2pm-02abcd000001", "192.0.2.50", "Plus2PM", "Garden Pump"), device);
        Assert.Equal("shellyplus2pm-02abcd000001", device.Key);
        Assert.Equal("Garden Pump", device.DisplayName);
    }

    [Fact]
    public void WhenADeviceAnnouncesOnlyItsUserGivenName_ThenItIsFoundByItsAddress()
    {
        // Arrange
        var instance = Instance("Garden Pump", "Garden-Pump.local", "192.0.2.50", "Plus2PM");

        // Act
        var devices = ShellyDeviceDiscovery.ToDevices([instance]);

        // Assert
        var device = Assert.Single(devices);
        Assert.Equal(new ShellyDiscoveredDevice(null, "192.0.2.50", "Plus2PM", "Garden Pump"), device);
        Assert.Equal("host:192.0.2.50", device.Key);
        Assert.Equal("Garden Pump", device.DisplayName);
        Assert.Equal("Plus2PM at 192.0.2.50", device.Detail);
    }

    [Theory]
    [InlineData("ShellyPlus2PM-02ABCD00001.local")]
    [InlineData("ShellyPlus2PM-02ABCD0000011.local")]
    [InlineData("ShellyPlus2PM-02ABCD00000G.local")]
    [InlineData("Plus2PM-02ABCD000001.local")]
    [InlineData("shelly-02abcd000001.local")]
    [InlineData("ShellyPlus2PM-02ABCD000001.example.local")]
    public void WhenTheHostNameIsNotShapedLikeADeviceId_ThenItIsNoDeviceId(string hostName)
    {
        // Arrange
        var instance = Instance("Garage", hostName, "192.0.2.50");

        // Act
        var devices = ShellyDeviceDiscovery.ToDevices([instance]);

        // Assert
        var device = Assert.Single(devices);
        Assert.Null(device.DeviceId);
        Assert.Equal("Garage", device.DisplayName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenTwoDeviceIdsAreAnnouncedAtOneAddress_ThenEachIsADeviceAndTheNameWithoutAnIdIsLeftOut(bool isReversed)
    {
        // Arrange
        MulticastDnsServiceInstance[] instances =
        [
            Instance("shellyplus2pm-02abcd000001", "ShellyPlus2PM-02ABCD000001.local", "192.0.2.50", "Plus2PM"),
            Instance("Garden Pump", "Garden-Pump.local", "192.0.2.50", "Plus2PM"),
            Instance("shellypro3em-02abcd000002", "ShellyPro3EM-02ABCD000002.local", "192.0.2.50", "Pro3EM")
        ];
        if (isReversed)
        {
            Array.Reverse(instances);
        }

        // Act
        var devices = ShellyDeviceDiscovery.ToDevices(instances);

        // Assert
        Assert.Equal(
            [
                new ShellyDiscoveredDevice("shellyplus2pm-02abcd000001", "192.0.2.50", "Plus2PM", null),
                new ShellyDiscoveredDevice("shellypro3em-02abcd000002", "192.0.2.50", "Pro3EM", null)
            ],
            devices);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenOneDeviceAnnouncesDifferentValues_ThenTheResultDoesNotDependOnTheirOrder(bool isReversed)
    {
        // Arrange
        MulticastDnsServiceInstance[] instances =
        [
            Instance("shellyplus2pm-02abcd000001", "ShellyPlus2PM-02ABCD000001.local", "192.0.2.50", "Plus2PM"),
            Instance("Window B", "Window-B.local", "192.0.2.50", "Plus2PMv2"),
            Instance("Window A", "Window-A.local", "192.0.2.50", application: null)
        ];
        if (isReversed)
        {
            Array.Reverse(instances);
        }

        // Act
        var devices = ShellyDeviceDiscovery.ToDevices(instances);

        // Assert
        Assert.Equal(new ShellyDiscoveredDevice("shellyplus2pm-02abcd000001", "192.0.2.50", "Plus2PM", "Window A"), Assert.Single(devices));
    }

    [Theory]
    [InlineData(" Garden Pump ", "Garden Pump")]
    [InlineData(null, "shellyplus2pm-02abcd000001")]
    public void WhenAnInstanceIsRenamedByConflictResolution_ThenItIsStillOneDeviceAndNoUserGivenName(string? userGivenName, string expectedDisplayName)
    {
        // Arrange
        var instances = new List<MulticastDnsServiceInstance>
        {
            Instance("shellyplus2pm-02abcd000001 (2)", "ShellyPlus2PM-02ABCD000001.local", "192.0.2.50", "Plus2PM"),
            Instance("shellyplus2pm-02abcd000001", "ShellyPlus2PM-02ABCD000001.local", "192.0.2.50", "Plus2PM")
        };
        if (userGivenName is not null)
        {
            instances.Add(Instance(userGivenName, "Garden-Pump.local", "192.0.2.50", "Plus2PM"));
        }

        // Act
        var devices = ShellyDeviceDiscovery.ToDevices(instances);

        // Assert
        var device = Assert.Single(devices);
        Assert.Equal("shellyplus2pm-02abcd000001", device.DeviceId);
        Assert.Equal(userGivenName?.Trim(), device.Name);
        Assert.Equal(expectedDisplayName, device.DisplayName);
    }

    [Fact]
    public void WhenOnlyAConflictRenamedInstanceWithoutHostNameIsAnnounced_ThenItsDeviceIdIsRead()
    {
        // Arrange
        var instance = Instance("ShellyPlus2PM-02ABCD000001 (3)", hostName: null, address: "192.0.2.50");

        // Act
        var devices = ShellyDeviceDiscovery.ToDevices([instance]);

        // Assert
        Assert.Equal(new ShellyDiscoveredDevice("shellyplus2pm-02abcd000001", "192.0.2.50", "Plus1PM", null), Assert.Single(devices));
    }

    [Fact]
    public void WhenOneDeviceIdIsAnnouncedAtTwoAddresses_ThenOneDeviceWithTheSmallestAddressIsFound()
    {
        // Arrange
        var instances = new[]
        {
            Instance("shellyplus2pm-02abcd000001", "ShellyPlus2PM-02ABCD000001.local", "192.0.2.52", "Plus2PM"),
            Instance("shellyplus2pm-02abcd000001", hostName: null, address: "192.0.2.51", application: "Plus2PM"),
            Instance("Garden Pump", "Garden-Pump.local", "192.0.2.52", "Plus2PM")
        };

        // Act
        var devices = ShellyDeviceDiscovery.ToDevices(instances);

        // Assert
        Assert.Equal(new ShellyDiscoveredDevice("shellyplus2pm-02abcd000001", "192.0.2.51", "Plus2PM", "Garden Pump"), Assert.Single(devices));
    }

    [Fact]
    public void WhenTwoDevicesAreAnnounced_ThenTwoDevicesAreFound()
    {
        // Arrange
        var instances = new[]
        {
            Instance("Kitchen", "ShellyPlus1PM-A8032AB12345.local", "192.0.2.41"),
            Instance("Garage", "ShellyPlus1PM-A8032AB67890.local", "192.0.2.42")
        };

        // Act
        var devices = ShellyDeviceDiscovery.ToDevices(instances);

        // Assert
        Assert.Equal(2, devices.Count);
        Assert.Equal(
            new ShellyDiscoveredDevice("shellyplus1pm-a8032ab12345", "192.0.2.41", "Plus1PM", "Kitchen"),
            devices[0]);
        Assert.Equal(
            new ShellyDiscoveredDevice("shellyplus1pm-a8032ab67890", "192.0.2.42", "Plus1PM", "Garage"),
            devices[1]);
    }

    [Fact]
    public void WhenAnInstanceHasNoHostName_ThenOnlyAnInstanceNameShapedLikeADeviceIdIsADeviceId()
    {
        // Arrange
        var instances = new[]
        {
            Instance("Kitchen", hostName: null),
            Instance("Garage", hostName: " . ", address: "192.0.2.42"),
            Instance("ShellyPlus1PM-A8032AB67890", hostName: null, address: "192.0.2.43")
        };

        // Act
        var devices = ShellyDeviceDiscovery.ToDevices(instances);

        // Assert
        Assert.Equal(
            [
                new ShellyDiscoveredDevice(null, "192.0.2.41", "Plus1PM", "Kitchen"),
                new ShellyDiscoveredDevice(null, "192.0.2.42", "Plus1PM", "Garage"),
                new ShellyDiscoveredDevice("shellyplus1pm-a8032ab67890", "192.0.2.43", "Plus1PM", null)
            ],
            devices);
    }

    [Fact]
    public void WhenAnInstanceNameIsEmpty_ThenTheDeviceIsNamedByItsIdOrAddress()
    {
        // Arrange
        var instances = new[]
        {
            Instance(" "),
            Instance(" ", "Garage.local", "192.0.2.42", application: null)
        };

        // Act
        var devices = ShellyDeviceDiscovery.ToDevices(instances);

        // Assert
        Assert.Equal(["192.0.2.42", "shellyplus1pm-a8032ab12345"], devices.Select(device => device.DisplayName));
        Assert.Equal(["host:192.0.2.42", "shellyplus1pm-a8032ab12345"], devices.Select(device => device.Key));
    }

    [Fact]
    public async Task WhenAScanRuns_ThenTheShellyServiceTypeIsBrowsed()
    {
        // Arrange
        var browser = new AnsweringBrowser(Instance("Kitchen"));
        ISubjectDiscovery discovery = new ShellyDeviceDiscovery(browser);

        // Act
        var results = await discovery.DiscoverAsync(CancellationToken.None).ToListAsync();

        // Assert
        var device = Assert.IsType<ShellyDiscoveredDevice>(Assert.Single(results));
        Assert.Equal("shellyplus1pm-a8032ab12345", device.DeviceId);
        Assert.Equal("Kitchen", device.DisplayName);
        Assert.Equal([ShellyDeviceDiscovery.ServiceType], browser.ServiceTypes);
    }

    [Fact]
    public void WhenTheRunningSubjectReadItsDeviceId_ThenThatDecides()
    {
        // Arrange
        var device = CreateDevice();
        device.HostAddress = "192.0.2.99";
        device.DeviceInfo = new ShellyDeviceInfo { Id = "shellyplus1pm-a8032ab12345" };

        // Act & Assert
        Assert.True(Found().Matches(device));
        Assert.False(Found(deviceId: "shellyplus1pm-other").Matches(device));
    }

    [Fact]
    public void WhenTheRunningSubjectReadAnotherDeviceId_ThenTheSameHostAddressDoesNotMatch()
    {
        // Arrange
        var device = CreateDevice();
        device.HostAddress = "192.0.2.41";
        device.DeviceInfo = new ShellyDeviceInfo { Id = "shellyplus1pm-replaced" };

        // Act & Assert
        Assert.False(Found().Matches(device));
    }

    [Fact]
    public void WhenTheRunningSubjectReadNoDeviceId_ThenTheHostAddressDecides()
    {
        // Arrange
        var device = CreateDevice();
        device.HostAddress = " 192.0.2.41 ";

        // Act & Assert
        Assert.True(Found().Matches(device));
        Assert.False(Found(hostAddress: "192.0.2.42").Matches(device));
    }

    [Fact]
    public void WhenTheDeviceAnnouncedNoDeviceId_ThenTheHostAddressDecides()
    {
        // Arrange
        var device = CreateDevice();
        device.HostAddress = "192.0.2.50";
        device.DeviceInfo = new ShellyDeviceInfo { Id = "shellyplus2pm-02abcd000001" };
        var found = Found(deviceId: null, hostAddress: "192.0.2.50", name: "Garden Pump");

        // Act & Assert
        Assert.True(found.Matches(device));
        Assert.False((found with { HostAddress = "192.0.2.51" }).Matches(device));
    }

    [Fact]
    public void WhenNeitherSideKnowsADeviceId_ThenTheHostAddressDecides()
    {
        // Arrange
        var device = CreateDevice();
        device.HostAddress = "192.0.2.50 ";
        var found = Found(deviceId: null, hostAddress: "192.0.2.50", name: "Garden Pump");

        // Act & Assert
        Assert.True(found.Matches(device));
        Assert.False((found with { HostAddress = "192.0.2.51" }).Matches(device));
    }

    private sealed class AnsweringBrowser(params MulticastDnsServiceInstance[] instances) : IMulticastDnsBrowser
    {
        public List<string> ServiceTypes { get; } = [];

        public Task<IReadOnlyList<MulticastDnsServiceInstance>> BrowseAsync(string serviceType, CancellationToken cancellationToken)
        {
            ServiceTypes.Add(serviceType);
            return Task.FromResult<IReadOnlyList<MulticastDnsServiceInstance>>(instances);
        }
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
