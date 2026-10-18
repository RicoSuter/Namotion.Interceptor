using System.Net;
using HomeBlaze.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
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
        Assert.True(test.Bridge.IsServerConnected);
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
    public async Task WhenInfoAnswersButTheListFails_ThenTheBridgeIsNotConnected()
    {
        // Arrange
        await using var test = new TestBridge();
        test.Fake.StatusOverride = path => path == "list" ? HttpStatusCode.InternalServerError : null;
        // Long, so that no retry resets the status while it is read.
        test.Bridge.RetryInterval = TimeSpan.FromHours(1);

        // Act
        await test.Bridge.StartAsync(CancellationToken.None);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => test.Bridge.Status == ServiceStatus.Error, message: "The connection did not fail.");
        Assert.False(test.Bridge.IsConnected);
        Assert.Equal(1, test.Fake.CountRequests("info"));
        Assert.Equal(1, test.Fake.CountRequests("list"));
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
    public async Task WhenConfigurationChangesDuringAnAction_ThenTheActionCompletesAndTheBridgeReconnects()
    {
        // Arrange
        var fake = new FakeNukiBridge { ListJson = NukiFixtures.SmartLock(state: 3) };
        await using var test = new TestBridge(fake);
        await test.StartAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.ActionGate = gate;
        var action = test.Lock.LockAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => fake.CountRequests("lockAction") == 1, message: "The action was not sent.");
        var infoRequests = fake.CountRequests("info");

        // Act
        await test.Bridge.ApplyConfigurationAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => !test.Bridge.IsConnected, message: "The bridge did not disconnect.");
        gate.SetResult();
        await action;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => test.Bridge.IsConnected && fake.CountRequests("info") > infoRequests,
            message: "The bridge did not reconnect.");
    }

    [Fact]
    public async Task WhenBridgeStopsDuringAnAction_ThenItStopsWithoutWaitingForTheAction()
    {
        // Arrange
        var fake = new FakeNukiBridge { ListJson = NukiFixtures.SmartLock(state: 3) };
        await using var test = new TestBridge(fake);
        await test.StartAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.ActionGate = gate;
        var action = test.Lock.LockAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => fake.CountRequests("lockAction") == 1, message: "The action was not sent.");

        // Act
        var stop = test.Bridge.StopAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => stop.IsCompleted, message: "The bridge waited for the action to stop.");

        // Assert
        Assert.Equal(ServiceStatus.Stopped, test.Bridge.Status);
        gate.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action);
        Assert.Null(test.Lock.LockState);
    }

    [Fact]
    public async Task WhenBridgeStopsWhileAReconnectWaitsForAnAction_ThenItStopsWithoutWaitingForTheAction()
    {
        // Arrange
        var fake = new FakeNukiBridge { ListJson = NukiFixtures.SmartLock(state: 3) };
        await using var test = new TestBridge(fake);
        await test.StartAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.ActionGate = gate;
        var action = test.Lock.LockAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => fake.CountRequests("lockAction") == 1, message: "The action was not sent.");
        await test.Bridge.ApplyConfigurationAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => !test.Bridge.IsConnected, message: "The bridge did not disconnect.");

        // Act
        var stop = test.Bridge.StopAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => stop.IsCompleted, message: "The bridge waited for the action to stop.");

        // Assert
        Assert.Equal(ServiceStatus.Stopped, test.Bridge.Status);
        gate.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action);
        Assert.Null(test.Lock.LockState);
    }

    [Theory]
    [InlineData("192.168.1.50", "192.168.1.50")]
    [InlineData("192.168.1.50:8081", "192.168.1.50")]
    [InlineData("https://192.168.1.50", "192.168.1.50")]
    [InlineData("HTTP://nuki.local:8081/", "nuki.local")]
    [InlineData("[fe80::1]", "fe80::1")]
    [InlineData("[fe80::1]:8081", "fe80::1")]
    [InlineData("fe80::1", "fe80::1")]
    public void WhenHostAddressIsSet_ThenIpAddressIsTheHostWithoutSchemeBracketsAndPort(string hostAddress, string expectedIpAddress)
    {
        // Arrange
        using var bridge = new NukiBridge(new FakeNukiBridge(), NullLogger<NukiBridge>.Instance);

        // Act
        bridge.HostAddress = hostAddress;

        // Assert
        Assert.Equal(expectedIpAddress, bridge.IpAddress);
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
