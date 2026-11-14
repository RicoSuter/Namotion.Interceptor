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
        Assert.Equal("FRITZ!Box 7590", snapshot.Router.Model);
        Assert.Equal("SERIAL0001", snapshot.Router.SerialNumber);
        Assert.Equal(TimeSpan.FromHours(1), snapshot.Router.Uptime);
        Assert.Null(snapshot.AvailableSoftwareUpdate);
        Assert.True(snapshot.Internet!.IsConnected);
        Assert.Equal("203.0.113.7", snapshot.Internet.ExternalIpAddress);
        Assert.Equal("2001:db8::7", snapshot.Internet.ExternalIpv6Address);
        Assert.Equal(TimeSpan.FromHours(2), snapshot.Internet.Uptime);
        Assert.Equal(5m, snapshot.Traffic!.UploadRate);
        Assert.Equal(120m, snapshot.Traffic.DownloadRate);
        Assert.Equal(40m, snapshot.Traffic.MaximumUploadRate);
        Assert.Equal(250m, snapshot.Traffic.MaximumDownloadRate);
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

    [Theory]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(606, true)]
    public async Task WhenTheHostListIsRefusedAfterLogin_ThenThePollFailsAsALoginFailure(int code, bool isUpnpFault)
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var refusals = isUpnpFault ? fake.Faults : fake.HttpErrors;
        refusals["X_AVM-DE_GetHostListPath"] = code;
        using var connection = await OpenAsync(fake);

        // Act & Assert
        await Assert.ThrowsAsync<FritzBoxLoginException>(() => FritzBoxPoller.PollAsync(connection, CancellationToken.None));
    }

    [Fact]
    public async Task WhenThePasswordChangedSinceTheLastPoll_ThenThePollFailsAfterOneLogin()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        using var connection = await OpenAsync(fake);
        var actionsBeforeThePoll = fake.Actions.Count;
        fake.Password = "changed";

        // Act
        await Assert.ThrowsAsync<FritzBoxLoginException>(() => FritzBoxPoller.PollAsync(connection, CancellationToken.None));

        // Assert
        Assert.Equal(1, fake.FailedLogins);
        Assert.Equal(actionsBeforeThePoll, fake.Actions.Count);
    }

    [Fact]
    public async Task WhenTheHostListPathIsMissing_ThenAnInvalidDataExceptionIsThrown()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        fake.Answers[$"{Tr64Services.Hosts}#X_AVM-DE_GetHostListPath"] = string.Empty;
        using var connection = await OpenAsync(fake);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => FritzBoxPoller.PollAsync(connection, CancellationToken.None));
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

    [Fact]
    public async Task WhenTheMeshDownloadFailsWithAnHttpError_ThenTheSnapshotHasNoMeshButHostsAreRead()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        fake.HttpErrors[FakeFritzBox.MeshListPath] = 500;
        using var connection = await OpenAsync(fake);

        // Act
        var snapshot = await FritzBoxPoller.PollAsync(connection, CancellationToken.None);

        // Assert
        Assert.Null(snapshot.Mesh);
        Assert.Equal(5, snapshot.Hosts.Count);
    }

    [Fact]
    public async Task WhenTheMeshDownloadConnectionFails_ThenThePollFails()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        fake.AbortedPaths[FakeFritzBox.MeshListPath] = true;
        using var connection = await OpenAsync(fake);

        // Act & Assert
        await Assert.ThrowsAsync<HttpRequestException>(() => FritzBoxPoller.PollAsync(connection, CancellationToken.None));
    }

    [Fact]
    public async Task WhenTheInternetIsDisconnected_ThenIsConnectedIsFalse()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        fake.Answers[$"{Tr64Services.WanPppConnection}#GetStatusInfo"] =
            "<NewConnectionStatus>Disconnected</NewConnectionStatus><NewLastConnectionError>ERROR_NONE</NewLastConnectionError><NewUptime>0</NewUptime>";
        using var connection = await OpenAsync(fake);

        // Act
        var snapshot = await FritzBoxPoller.PollAsync(connection, CancellationToken.None);

        // Assert
        Assert.False(snapshot.Internet!.IsConnected);
    }

    [Fact]
    public async Task WhenTheIpv6ActionFaults_ThenOnlyTheIpv6AddressIsNull()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        fake.Faults["GetAppRemoteInfo"] = 401;
        using var connection = await OpenAsync(fake);

        // Act
        var snapshot = await FritzBoxPoller.PollAsync(connection, CancellationToken.None);

        // Assert
        Assert.Null(snapshot.Internet!.ExternalIpv6Address);
        Assert.True(snapshot.Internet.IsConnected);
        Assert.Equal("203.0.113.7", snapshot.Internet.ExternalIpAddress);
    }

    [Fact]
    public async Task WhenTheAppSetupServiceIsNotOffered_ThenOnlyTheIpv6AddressIsNullAndItIsNotAskedFor()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        var appSetupService = fake.DescriptionXml.Split('\n').Single(line => line.Contains(Tr64Services.AppSetup, StringComparison.Ordinal));
        fake.DescriptionXml = fake.DescriptionXml.Replace(appSetupService, string.Empty, StringComparison.Ordinal);
        using var connection = await OpenAsync(fake);

        // Act
        var snapshot = await FritzBoxPoller.PollAsync(connection, CancellationToken.None);

        // Assert
        Assert.Null(snapshot.Internet!.ExternalIpv6Address);
        Assert.True(snapshot.Internet.IsConnected);
        Assert.Equal("203.0.113.7", snapshot.Internet.ExternalIpAddress);
        Assert.DoesNotContain("GetAppRemoteInfo", fake.Actions);
    }

    [Fact]
    public async Task WhenEveryInternetActionFaults_ThenInternetIsNull()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        fake.Faults["GetStatusInfo"] = 401;
        fake.Faults["GetExternalIPAddress"] = 401;
        fake.Faults["GetAppRemoteInfo"] = 401;
        using var connection = await OpenAsync(fake);

        // Act
        var snapshot = await FritzBoxPoller.PollAsync(connection, CancellationToken.None);

        // Assert
        Assert.Null(snapshot.Internet);
    }

    [Fact]
    public async Task WhenAnEthernetWanReportsItsLineSpeed_ThenTheMaximumRatesAreInMegabitsPerSecond()
    {
        // Arrange: the online monitor of a FRITZ!Box 4060 on a 2.5 Gbit/s port, in bytes per second.
        await using var fake = await FakeFritzBox.StartAsync();
        fake.Answers[$"{Tr64Services.WanCommonInterfaceConfig}#X_AVM-DE_GetOnlineMonitor"] =
            "<NewTotalNumberSyncGroups>1</NewTotalNumberSyncGroups><Newmax_ds>312500000</Newmax_ds><Newmax_us>312500000</Newmax_us>" +
            "<Newds_current_bps>108410,101566</Newds_current_bps><Newus_current_bps>87291,64364</Newus_current_bps>";
        using var connection = await OpenAsync(fake);

        // Act
        var snapshot = await FritzBoxPoller.PollAsync(connection, CancellationToken.None);

        // Assert
        Assert.Equal(2500m, snapshot.Traffic!.MaximumDownloadRate);
        Assert.Equal(2500m, snapshot.Traffic.MaximumUploadRate);
        Assert.Equal(108.41m, snapshot.Traffic.DownloadRate);
    }

    [Fact]
    public async Task WhenTheOnlineMonitorFaults_ThenTrafficIsNull()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        fake.Faults["X_AVM-DE_GetOnlineMonitor"] = 401;
        using var connection = await OpenAsync(fake);

        // Act
        var snapshot = await FritzBoxPoller.PollAsync(connection, CancellationToken.None);

        // Assert
        Assert.Null(snapshot.Traffic);
    }

    [Fact]
    public async Task WhenTwoRadiosReportTheSameBand_ThenTheSecondRoleGetsASuffix()
    {
        // Arrange
        await using var fake = await FakeFritzBox.StartAsync();
        fake.Answers[$"{Tr64Services.WlanConfigurationPrefix}2#GetInfo"] =
            "<NewEnable>1</NewEnable><NewSSID>Home5</NewSSID><NewChannel>6</NewChannel><NewX_AVM-DE_FrequencyBand>2400</NewX_AVM-DE_FrequencyBand>";
        using var connection = await OpenAsync(fake);

        // Act
        var snapshot = await FritzBoxPoller.PollAsync(connection, CancellationToken.None);

        // Assert
        Assert.Equal(FritzBoxWlanRoles.Band24, snapshot.Wlans[0].Role);
        Assert.Equal($"{FritzBoxWlanRoles.Band24}-2", snapshot.Wlans[1].Role);
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
    [InlineData(1, 3, "unknown", FritzBoxWlanRoles.Band5, FritzBoxWlanRoles.Band5)]
    [InlineData(4, 6, null, "WLAN5", null)]
    public void WhenAWlanIsClassified_ThenTheLastOfSeveralIsTheGuest(int index, int count, string? frequencyBand, string expectedRole, string? expectedBand)
    {
        // Act
        var (role, band) = FritzBoxPoller.ClassifyWlan(index, count, frequencyBand);

        // Assert
        Assert.Equal(expectedRole, role);
        Assert.Equal(expectedBand, band);
    }

    private static async Task<FritzBoxConnection> OpenAsync(FakeFritzBox fake)
    {
        var connection = await FritzBoxConnection.OpenAsync(
            new FritzBoxConnectionOptions("127.0.0.1", fake.Username, fake.Password, null) { DescriptionPort = fake.HttpPort },
            NullLogger.Instance,
            CancellationToken.None);
        await connection.ResolveInternetServiceAsync(CancellationToken.None);
        return connection;
    }
}
