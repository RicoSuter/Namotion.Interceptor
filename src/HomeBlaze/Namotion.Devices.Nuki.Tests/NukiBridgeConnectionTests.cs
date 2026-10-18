using System.Net;
using HomeBlaze.Abstractions;
using Namotion.Devices.Nuki.Tests.Testing;
using Namotion.Interceptor.Testing;
using Xunit;

namespace Namotion.Devices.Nuki.Tests;

public class NukiBridgeConnectionTests
{
    [Fact]
    public async Task WhenHostAddressIsMissing_ThenStatusIsErrorAndNothingIsSent()
    {
        // Arrange
        await using var test = new TestBridge();
        test.Bridge.HostAddress = null;

        // Act
        await test.Bridge.StartAsync(CancellationToken.None);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => test.Bridge.Status == ServiceStatus.Error);
        Assert.Equal("Host address and token are required.", test.Bridge.StatusMessage);
        Assert.Empty(test.Fake.Requests);
    }

    [Fact]
    public async Task WhenBridgeConnects_ThenInfoAndDevicesAreApplied()
    {
        // Arrange
        await using var test = new TestBridge();

        // Act
        await test.StartAsync();

        // Assert
        Assert.Equal(ServiceStatus.Running, test.Bridge.Status);
        Assert.Equal(NukiBridgeType.Hardware, test.Bridge.BridgeType);
        Assert.Equal(12345678L, test.Bridge.HardwareId);
        Assert.Equal(87654321L, test.Bridge.ServerId);
        Assert.Equal("2.14.0", test.Bridge.SoftwareVersion);
        Assert.Equal("2.2.0", test.Bridge.WifiFirmwareVersion);
        Assert.Equal(TimeSpan.FromSeconds(120), test.Bridge.Uptime);
        Assert.True(test.Bridge.IsServerConnected == true);
        Assert.Equal("Nuki Bridge (12345678)", test.Bridge.Title);
        Assert.Equal("192.168.1.50", test.Bridge.IpAddress);
        Assert.Equal(1m, test.Bridge.Power);
        Assert.True(test.Lock.IsConnected);
        Assert.True(test.Opener.IsConnected);
    }

    [Fact]
    public async Task WhenBridgeIsASoftwareBridge_ThenTheAppVersionIsTheSoftwareVersion()
    {
        // Arrange
        await using var test = new TestBridge();
        test.Fake.InfoJson = NukiFixtures.SoftwareInfo;

        // Act
        await test.StartAsync();

        // Assert
        Assert.Equal(NukiBridgeType.Software, test.Bridge.BridgeType);
        Assert.Equal("1.4.2", test.Bridge.SoftwareVersion);
        Assert.Null(test.Bridge.HardwareId);
    }

    [Fact]
    public async Task WhenTokenIsRejected_ThenStatusMessageNamesTheLikelyCauses()
    {
        // Arrange
        await using var test = new TestBridge();
        test.Fake.StatusOverride = _ => HttpStatusCode.Unauthorized;
        // Long, so that no retry resets the status while it is read.
        test.Bridge.RetryInterval = TimeSpan.FromHours(1);

        // Act
        await test.Bridge.StartAsync(CancellationToken.None);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => test.Bridge.Status == ServiceStatus.Error);
        Assert.Contains("Token rejected", test.Bridge.StatusMessage);
        Assert.False(test.Bridge.IsConnected);
    }

    [Fact]
    public async Task WhenPollFailsThreeTimes_ThenBridgeDisconnectsAndReconnectsOnceItAnswers()
    {
        // Arrange
        await using var test = new TestBridge();
        test.Bridge.PollingInterval = TimeSpan.FromSeconds(NukiBridge.MinimumIntervalSeconds);
        // Long enough for the disconnected state to be observed before the reconnect.
        test.Bridge.RetryInterval = TimeSpan.FromSeconds(1);
        await test.StartAsync();
        var smartLock = test.Lock;

        // Act
        test.Fake.StatusOverride = path => path == "list" ? HttpStatusCode.InternalServerError : null;
        await AsyncTestHelpers.WaitUntilAsync(
            () => !test.Bridge.IsConnected && !smartLock.IsConnected,
            message: "The bridge and its lock did not disconnect.");
        test.Fake.StatusOverride = null;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => test.Bridge.IsConnected && smartLock.IsConnected, message: "The bridge did not reconnect.");
    }

    [Fact]
    public async Task WhenConfigurationChanges_ThenBridgeReconnects()
    {
        // Arrange
        await using var test = new TestBridge();
        await test.StartAsync();
        var infoRequests = test.Fake.CountRequests("info");

        // Act
        await test.Bridge.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => test.Fake.CountRequests("info") > infoRequests);
    }

    [Fact]
    public async Task WhenBridgeStops_ThenStatusIsStopped()
    {
        // Arrange
        await using var test = new TestBridge();
        await test.StartAsync();

        // Act
        await test.Bridge.StopAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Stopped, test.Bridge.Status);
        Assert.False(test.Bridge.IsConnected);
    }
}
