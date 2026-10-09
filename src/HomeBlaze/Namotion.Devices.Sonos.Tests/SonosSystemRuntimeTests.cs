using System.Net;
using HomeBlaze.Abstractions;
using Microsoft.Extensions.Logging;
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
                () => system.Status == ServiceStatus.Error && system.StatusMessage is not null,
                ConnectedSystem.WaitTimeout,
                message: "An unreachable seed should report an error.");
            Assert.False(system.IsConnected);
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
        Assert.Equal(
            new[] { AvTransportEventPath, "/MediaRenderer/GroupRenderingControl/Event", "/MediaRenderer/RenderingControl/Event", "/ZoneGroupTopology/Event" },
            speaker.Unsubscribed.Order(StringComparer.Ordinal));
        Assert.Equal(ServiceStatus.Stopped, connected.System.Status);
        Assert.False(connected.System.IsConnected);
        Assert.False(connected.Player.IsConnected);
        connected.System.Dispose();
    }

    [Fact]
    public async Task WhenSubscriptionsExpireBeforeTheNextPoll_ThenTheyAreRenewedWithoutPolling()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker { SubscriptionTimeoutSeconds = 2 };
        await using var connected = await ConnectedSystem.StartAsync(speaker, configure: system =>
            system.PollingInterval = TimeSpan.FromHours(1));
        var topologyReads = speaker.Calls.Count(call => call.Action == "GetZoneGroupState");

        // Act
        await AsyncTestHelpers.WaitUntilAsync(
            () => speaker.Renewed.Contains(AvTransportEventPath),
            ConnectedSystem.WaitTimeout,
            message: "The subscription should be renewed before it expires although the next poll is an hour away.");

        // Assert
        Assert.Equal(topologyReads, speaker.Calls.Count(call => call.Action == "GetZoneGroupState"));
        Assert.True(connected.System.AreEventsActive);
    }

    [Fact]
    public async Task WhenReadingFavoritesKeepsFailing_ThenOnlyEachNewFailureIsAWarning()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        var logger = new RecordingLogger<SonosSystem>();
        await using var connected = await ConnectedSystem.StartAsync(speaker, logger: logger, configure: system =>
            system.PollingInterval = TimeSpan.FromHours(1));
        speaker.RespondWithFault("Browse", 501);

        // Act
        await connected.System.RefreshAsync(CancellationToken.None);
        await connected.System.RefreshAsync(CancellationToken.None);
        speaker.ClearFault("Browse");
        await connected.System.RefreshAsync(CancellationToken.None);
        speaker.RespondWithFault("Browse", 501);
        await connected.System.RefreshAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, logger.Warnings.Count(message => message.Contains("favorites")));
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Debug && entry.Message.Contains("favorites"));
    }

    [Fact]
    public async Task WhenSubscribingKeepsFailing_ThenEachSubscriptionWarnsOnce()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker { FailSubscriptions = true };
        speaker.RespondAsIdlePlayer(TestFixtures.KitchenUuid, "Küche");
        var logger = new RecordingLogger<SonosSystem>();
        var system = ConnectedSystem.CreateSystem(speaker.Host, logger: logger);
        system.PollingInterval = TimeSpan.FromHours(1);

        try
        {
            await system.StartAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(() => system.IsConnected, ConnectedSystem.WaitTimeout, message: "The system should connect.");

            // Act
            await system.RefreshAsync(CancellationToken.None);
            await system.RefreshAsync(CancellationToken.None);

            // Assert
            Assert.Equal(4, logger.Warnings.Count(message => message.Contains("subscription")));
            Assert.Equal(8, logger.Entries.Count(entry => entry.Level == LogLevel.Debug && entry.Message.Contains("subscription")));
            Assert.False(system.AreEventsActive);
        }
        finally
        {
            await system.StopAsync(CancellationToken.None);
            system.Dispose();
        }
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
        Assert.Contains(AvTransportEventPath, speaker.Unsubscribed);
    }

    [Fact]
    public async Task WhenFirstKnownSpeakerDoesNotAnswer_ThenSystemReconnectsThroughAnotherKnownSpeaker()
    {
        // Arrange
        await using var kitchen = new FakeSonosSpeaker();
        await using var office = new FakeSonosSpeaker();
        kitchen.RespondAsIdlePlayer(TestFixtures.KitchenUuid, "Küche");
        office.RespondAsIdlePlayer(TestFixtures.OfficeUuid, "Büro");
        var household = new[] { (TestFixtures.KitchenUuid, "Küche", kitchen.BaseUri), (TestFixtures.OfficeUuid, "Büro", office.BaseUri) };
        kitchen.RespondWithTopology(household);
        office.RespondWithTopology(household);

        var discoveryCalls = 0;
        var system = ConnectedSystem.CreateSystem(kitchen.Host);
        system.DiscoverSpeakerAsync = _ =>
        {
            Interlocked.Increment(ref discoveryCalls);
            return Task.FromResult<Uri?>(null);
        };

        try
        {
            await system.StartAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(
                () => system.IsConnected && system.Players.Count == 2 && system.Players.Values.All(player => player.IsConnected),
                ConnectedSystem.WaitTimeout,
                message: "The system should connect to both speakers.");
            var kitchenTopologyReads = kitchen.Calls.Count(call => call.Action == "GetZoneGroupState");
            var officeTopologyReads = office.Calls.Count(call => call.Action == "GetZoneGroupState");

            // Act
            kitchen.RespondWithFault("GetZoneGroupState", 501);
            system.SeedHost = null;
            await system.ApplyConfigurationAsync(CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => office.Calls.Count(call => call.Action == "GetZoneGroupState") > officeTopologyReads &&
                      system.IsConnected &&
                      system.Players[TestFixtures.OfficeUuid].IsConnected,
                ConnectedSystem.WaitTimeout,
                message: "The system should reconnect through the office speaker.");
            Assert.Equal(ServiceStatus.Running, system.Status);
            Assert.Equal(0, Volatile.Read(ref discoveryCalls));

            // The probe stops at the first speaker that answers, so a probe of the kitchen means it was tried first.
            Assert.True(kitchen.Calls.Count(call => call.Action == "GetZoneGroupState") > kitchenTopologyReads);
        }
        finally
        {
            await system.StopAsync(CancellationToken.None);
            system.Dispose();
        }
    }

    [Fact]
    public async Task WhenNoKnownSpeakerAnswers_ThenDiscoveryFindsTheSeed()
    {
        // Arrange
        var kitchen = new FakeSonosSpeaker();
        await using var office = new FakeSonosSpeaker();
        office.RespondAsIdlePlayer(TestFixtures.OfficeUuid, "Büro");
        var discoveryCalls = 0;

        try
        {
            await using var connected = await ConnectedSystem.StartAsync(kitchen, configure: system =>
                system.DiscoverSpeakerAsync = _ =>
                {
                    Interlocked.Increment(ref discoveryCalls);
                    return Task.FromResult<Uri?>(office.BaseUri);
                });

            // Act
            await kitchen.DisposeAsync();
            connected.System.SeedHost = null;
            await connected.System.ApplyConfigurationAsync(CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => connected.System.IsConnected &&
                      connected.System.Players.TryGetValue(TestFixtures.OfficeUuid, out var player) && player.IsConnected,
                ConnectedSystem.WaitTimeout,
                message: "The system should fall back to discovery and connect through the office speaker.");
            Assert.Equal(1, Volatile.Read(ref discoveryCalls));
        }
        finally
        {
            await kitchen.DisposeAsync();
        }
    }

    [Fact]
    public async Task WhenOneReconcileFails_ThenSystemStaysConnected()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker, configure: system =>
            system.PollingInterval = TimeSpan.FromSeconds(1));
        bool? isConnectedAfterFirstFailure = null;
        ServiceStatus? statusAfterFirstFailure = null;

        // Act
        speaker.RespondWithFault("GetZoneGroupState", 501);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                isConnectedAfterFirstFailure = connected.System.IsConnected;
                statusAfterFirstFailure = connected.System.Status;
                // Matching any count keeps a slow thread pool that misses "(1 of 3)" from timing out.
                return connected.System.StatusMessage?.Contains("of 3)") == true;
            },
            ConnectedSystem.WaitTimeout,
            pollInterval: TimeSpan.FromMilliseconds(10),
            message: "A failure below the limit should be reported without disconnecting.");
        Assert.True(isConnectedAfterFirstFailure);
        Assert.Equal(ServiceStatus.Running, statusAfterFirstFailure);
    }

    [Fact]
    public async Task WhenThreeReconcilesFail_ThenStatusIsErrorAndSystemReconnects()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker, configure: system =>
            system.PollingInterval = TimeSpan.FromMilliseconds(200));

        // Act
        speaker.RespondWithFault("GetZoneGroupState", 501);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => connected.System.Status == ServiceStatus.Error &&
                  !connected.System.IsConnected &&
                  speaker.Unsubscribed.Contains(AvTransportEventPath),
            ConnectedSystem.WaitTimeout,
            message: "Three failures in a row should end the connection and release its subscriptions.");
        speaker.ClearFault("GetZoneGroupState");
        await AsyncTestHelpers.WaitUntilAsync(
            () => connected.System.IsConnected && connected.System.Status == ServiceStatus.Running && connected.System.AreEventsActive,
            ConnectedSystem.WaitTimeout,
            message: "The system should reconnect once the speaker answers again.");
    }

    [Fact]
    public async Task WhenDisposedWithoutStop_ThenLoopEndsQuietly()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        var connected = await ConnectedSystem.StartAsync(speaker);

        // Act
        connected.System.Dispose();
        await connected.System.ExecuteTask!.WaitAsync(ConnectedSystem.WaitTimeout);
        var exception = await Record.ExceptionAsync(() => connected.System.ApplyConfigurationAsync(CancellationToken.None));

        // Assert
        Assert.True(connected.System.ExecuteTask.IsCompletedSuccessfully);
        Assert.Equal(ServiceStatus.Stopped, connected.System.Status);
        Assert.Null(exception);
    }

    [Fact]
    public async Task WhenConfigurationIsAppliedConcurrently_ThenNoCallThrows()
    {
        // Arrange
        var system = ConnectedSystem.CreateSystem("127.0.0.1:1");

        try
        {
            // Act
            var exception = await Record.ExceptionAsync(() => Task.WhenAll(Enumerable.Range(0, 64)
                .Select(_ => Task.Run(() => system.ApplyConfigurationAsync(CancellationToken.None)))));

            // Assert
            Assert.Null(exception);
        }
        finally
        {
            system.Dispose();
        }
    }
}
