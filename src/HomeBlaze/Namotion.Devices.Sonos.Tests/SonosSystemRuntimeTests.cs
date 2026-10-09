using System.Net;
using HomeBlaze.Abstractions;
using Namotion.Devices.Sonos.Tests.Testing;
using Namotion.Interceptor.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosSystemRuntimeTests
{
    private const string AvTransportEventPath = "/MediaRenderer/AVTransport/Event";

    [Fact]
    public async Task WhenSeedHostAnswers_ThenSystemConnectsAndReadsThePlayer()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();

        // Act
        await using var connected = await ConnectedSystem.StartAsync(speaker);

        // Assert
        var system = connected.System;
        var player = connected.Player;
        Assert.Equal(ServiceStatus.Running, system.Status);
        Assert.Equal("127.0.0.1", system.ActiveEventCallbackHost);
        Assert.Equal("Küche", player.RoomName);
        Assert.Equal("Sonos Ray", player.Model);
        Assert.Equal("S36", player.ProductCode);
        Assert.Equal("00-00-00-00-00-06:D", player.SerialNumber);
        Assert.Equal("18.8", player.SoftwareVersion);
        Assert.Equal(0.44m, player.Volume);
        Assert.Equal(SonosTransportState.Paused, player.TransportState);
        Assert.Equal(SonosSource.SpotifyConnect, player.Source);
        Assert.Equal(new[] { "Radio FM1", "SRF 3" }, system.Favorites);
        Assert.Equal(0.44m, Assert.Single(system.Groups).Value.Volume);
        Assert.NotNull(system.LastUpdated);
    }

    [Fact]
    public async Task WhenSpeakerSendsAvTransportEvent_ThenPlayerUpdatesWithoutWaitingForThePoll()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);
        var callback = speaker.GetCallback(AvTransportEventPath)!;
        using var httpClient = new HttpClient();
        using var request = new HttpRequestMessage(new HttpMethod("NOTIFY"), callback)
        {
            Content = new StringContent(SonosEventBodies.AvTransport(("TransportState", "PLAYING")))
        };
        request.Headers.TryAddWithoutValidation("SID", FakeSonosSpeaker.SidFor(AvTransportEventPath));

        // Act
        using var response = await httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AsyncTestHelpers.WaitUntilAsync(
            () => connected.Player.TransportState == SonosTransportState.Playing,
            ConnectedSystem.WaitTimeout,
            message: "The event should reach the player.");
    }

    [Fact]
    public async Task WhenSeedHostIsUnreachable_ThenStatusIsError()
    {
        // Arrange
        var system = ConnectedSystem.CreateSystem($"127.0.0.1:{LoopbackHttpServer.GetFreePort()}");

        // Act
        await system.StartAsync(CancellationToken.None);

        // Assert
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => system.Status == ServiceStatus.Error,
                ConnectedSystem.WaitTimeout,
                message: "An unreachable seed should report an error.");
            Assert.False(system.IsConnected);
            Assert.NotNull(system.StatusMessage);
        }
        finally
        {
            await system.StopAsync(CancellationToken.None);
            system.Dispose();
        }
    }

    [Fact]
    public async Task WhenSeedHostIsInvalid_ThenStatusIsErrorUntilConfigurationFixesIt()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        speaker.RespondAsIdlePlayer(TestFixtures.KitchenUuid, "Küche");
        var system = ConnectedSystem.CreateSystem("http://127.0.0.1:1400");

        try
        {
            await system.StartAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(
                () => system.Status == ServiceStatus.Error && system.StatusMessage?.Contains("SeedHost") == true,
                ConnectedSystem.WaitTimeout,
                message: "An invalid seed host should report an error.");

            // Act
            system.SeedHost = speaker.Host;
            await system.ApplyConfigurationAsync(CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => system.IsConnected && system.Status == ServiceStatus.Running,
                ConnectedSystem.WaitTimeout,
                message: "Fixing the seed host should connect the system.");
        }
        finally
        {
            await system.StopAsync(CancellationToken.None);
            system.Dispose();
        }
    }

    [Fact]
    public async Task WhenEventPortIsTaken_ThenSystemConnectsWithPollingOnly()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        speaker.RespondAsIdlePlayer(TestFixtures.KitchenUuid, "Küche");
        await using var portBlocker = new LoopbackHttpServer(_ => Task.CompletedTask);
        var system = ConnectedSystem.CreateSystem(speaker.Host, eventPort: portBlocker.Port);

        try
        {
            // Act
            await system.StartAsync(CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => system.IsConnected && system.Players.TryGetValue(TestFixtures.KitchenUuid, out var player) && player.Model is not null,
                ConnectedSystem.WaitTimeout,
                message: "The system should connect without events.");
            Assert.False(system.AreEventsActive);
            Assert.Null(system.ActiveEventCallbackHost);
            Assert.Equal(0.44m, system.Players[TestFixtures.KitchenUuid].Volume);
            Assert.Null(speaker.GetCallback(AvTransportEventPath));
        }
        finally
        {
            await system.StopAsync(CancellationToken.None);
            system.Dispose();
        }
    }

    [Fact]
    public async Task WhenStopped_ThenSubscriptionsAreCancelledAndStatusIsStopped()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        var connected = await ConnectedSystem.StartAsync(speaker);

        // Act
        await connected.System.StopAsync(CancellationToken.None);

        // Assert
        Assert.Contains(AvTransportEventPath, speaker.Unsubscribed);
        Assert.Equal(ServiceStatus.Stopped, connected.System.Status);
        Assert.False(connected.System.IsConnected);
        Assert.False(connected.Player.IsConnected);
        connected.System.Dispose();
    }

    [Fact]
    public async Task WhenConfigurationChanges_ThenSystemReconnects()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);
        var initialTopologyReads = speaker.Calls.Count(call => call.Action == "GetZoneGroupState");

        // Act
        await connected.System.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => speaker.Calls.Count(call => call.Action == "GetZoneGroupState") > initialTopologyReads && connected.System.IsConnected,
            ConnectedSystem.WaitTimeout,
            message: "A configuration change should rebuild the connection.");
    }
}
