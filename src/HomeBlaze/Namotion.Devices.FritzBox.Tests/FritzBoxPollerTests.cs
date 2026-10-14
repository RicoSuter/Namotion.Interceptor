using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.FritzBox.Client;
using Namotion.Devices.FritzBox.Tests.Testing;
using Xunit;

namespace Namotion.Devices.FritzBox.Tests;

public class FritzBoxPollerTests
{
    [Fact]
    public async Task WhenTheRouterIsPolled_ThenEveryPartOfTheSnapshotIsRead()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        using var connection = await OpenAsync(fake);

        // Act
        var snapshot = await FritzBoxPoller.PollAsync(connection, CancellationToken.None);

        // Assert
        Assert.Equal("FRITZ!Box 7590", snapshot.Router!.Model);
        Assert.Equal("SERIAL0001", snapshot.Router.SerialNumber);
        Assert.Equal(TimeSpan.FromHours(1), snapshot.Router.Uptime);
        Assert.Null(snapshot.AvailableSoftwareUpdate);
        Assert.True(snapshot.Internet!.IsConnected);
        Assert.Equal("203.0.113.7", snapshot.Internet.ExternalIpAddress);
        Assert.Equal("2001:db8::7", snapshot.Internet.ExternalIpv6Address);
        Assert.Equal(TimeSpan.FromHours(2), snapshot.Internet.Uptime);
        Assert.Equal(40m, snapshot.Internet.MaximumUploadRate);
        Assert.Equal(250m, snapshot.Internet.MaximumDownloadRate);
        Assert.Equal(5m, snapshot.Traffic!.UploadRate);
        Assert.Equal(120m, snapshot.Traffic.DownloadRate);
        Assert.Equal([FritzBoxWlanRoles.Band24, FritzBoxWlanRoles.Band5, FritzBoxWlanRoles.Guest], snapshot.Wlans.Select(wlan => wlan.Role));
        Assert.False(snapshot.Wlans[2].IsEnabled);
        Assert.Equal(FakeFritzBox.GuestWlanServiceType, snapshot.Wlans[2].ServiceType);
        Assert.Equal(5, snapshot.Wlans[1].ClientCount);
        Assert.Equal(5, snapshot.Hosts.Count);
        Assert.NotNull(snapshot.Mesh);
    }

    [Fact]
    public async Task WhenAnUpdateIsAvailable_ThenItsVersionIsReported()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        fake.Answers[$"{Tr64Services.UserInterface}#GetInfo"] = "<NewUpgradeAvailable>1</NewUpgradeAvailable><NewX_AVM-DE_Version>154.08.00</NewX_AVM-DE_Version>";
        using var connection = await OpenAsync(fake);

        // Act
        var snapshot = await FritzBoxPoller.PollAsync(connection, CancellationToken.None);

        // Assert
        Assert.Equal("154.08.00", snapshot.AvailableSoftwareUpdate);
    }

    [Fact]
    public async Task WhenTheHostListIsNotOffered_ThenThePollFailsAsFirmwareTooOld()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        fake.Faults["X_AVM-DE_GetHostListPath"] = 401;
        using var connection = await OpenAsync(fake);

        // Act
        var exception = await Assert.ThrowsAsync<FritzBoxFirmwareException>(() => FritzBoxPoller.PollAsync(connection, CancellationToken.None));

        // Assert
        Assert.Contains("FRITZ!OS", exception.Message);
    }

    [Fact]
    public async Task WhenTheMeshListIsNotOffered_ThenTheSnapshotHasNoMesh()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        fake.Faults["X_AVM-DE_GetMeshListPath"] = 401;
        using var connection = await OpenAsync(fake);

        // Act
        var snapshot = await FritzBoxPoller.PollAsync(connection, CancellationToken.None);

        // Assert
        Assert.Null(snapshot.Mesh);
        Assert.Equal(5, snapshot.Hosts.Count);
    }

    [Fact]
    public async Task WhenTheMeshListIsMalformed_ThenTheSnapshotHasNoMesh()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        fake.MeshListJson = "{\"nodes\":[";
        using var connection = await OpenAsync(fake);

        // Act
        var snapshot = await FritzBoxPoller.PollAsync(connection, CancellationToken.None);

        // Assert
        Assert.Null(snapshot.Mesh);
    }

    [Theory]
    [InlineData("5000,4000,3000", 5000L)]
    [InlineData(" 7 ", 7L)]
    [InlineData("x,1", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void WhenAMonitorSeriesIsRead_ThenItsMostRecentSampleIsTaken(string? samples, long? expected)
    {
        // Act
        var sample = FritzBoxPoller.GetMostRecentSample(samples);

        // Assert
        Assert.Equal(expected, sample);
    }

    [Theory]
    [InlineData(0, 3, "2400", FritzBoxWlanRoles.Band24, FritzBoxWlanRoles.Band24)]
    [InlineData(1, 3, null, FritzBoxWlanRoles.Band5, FritzBoxWlanRoles.Band5)]
    [InlineData(2, 3, "2400", FritzBoxWlanRoles.Guest, FritzBoxWlanRoles.Band24)]
    [InlineData(2, 3, null, FritzBoxWlanRoles.Guest, null)]
    [InlineData(0, 1, null, FritzBoxWlanRoles.Band24, FritzBoxWlanRoles.Band24)]
    [InlineData(2, 4, "6000", FritzBoxWlanRoles.Band6, FritzBoxWlanRoles.Band6)]
    public void WhenAWlanIsClassified_ThenTheLastOfSeveralIsTheGuest(int index, int count, string? frequencyBand, string expectedRole, string? expectedBand)
    {
        // Act
        var (role, band) = FritzBoxPoller.ClassifyWlan(index, count, frequencyBand);

        // Assert
        Assert.Equal(expectedRole, role);
        Assert.Equal(expectedBand, band);
    }

    private static Task<FritzBoxConnection> OpenAsync(FakeFritzBox fake) => OpenAndResolveAsync(fake);

    private static async Task<FritzBoxConnection> OpenAndResolveAsync(FakeFritzBox fake)
    {
        var connection = await FritzBoxConnection.OpenAsync(
            new FritzBoxConnectionOptions("127.0.0.1", fake.Username, fake.Password, null) { DescriptionPort = fake.HttpPort },
            NullLogger.Instance,
            CancellationToken.None);
        await connection.ResolveInternetServiceAsync(CancellationToken.None);
        return connection;
    }
}
