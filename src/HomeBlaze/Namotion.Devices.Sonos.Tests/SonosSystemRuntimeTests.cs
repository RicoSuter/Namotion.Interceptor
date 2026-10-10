using System.Net;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Media;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Sonos.Tests.Testing;
using Namotion.Interceptor.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosSystemRuntimeTests
{
    private const string AvTransportEventPath = "/MediaRenderer/AVTransport/Event";
    private const string TopologyEventPath = "/ZoneGroupTopology/Event";

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
        Assert.Equal(MediaPlaybackState.Paused, player.PlaybackState);
        Assert.Equal(SonosSource.SpotifyConnect, player.Source);
        Assert.Equal(new[] { "Radio FM1", "SRF 3" }, system.Favorites.Select(favorite => favorite.Title));
        Assert.Equal(0.44m, Assert.Single(system.Groups).Value.Volume);
        Assert.NotNull(system.LastUpdated);
    }

    [Fact]
    public async Task WhenSpeakerSendsAvTransportEvent_ThenPlayerUpdatesWithoutWaitingForThePoll()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);

        // Act
        var status = await speaker.NotifyAsync(AvTransportEventPath, SonosEventBodies.AvTransport(("TransportState", "PLAYING")));

        // Assert
        Assert.Equal(HttpStatusCode.OK, status);
        await AsyncTestHelpers.WaitUntilAsync(
            () => connected.Player.PlaybackState == MediaPlaybackState.Playing,
            ConnectedSystem.WaitTimeout,
            message: "The event should reach the player.");
    }

    [Fact]
    public async Task WhenSpeakersAcceptSubscriptionsButSendNoEvent_ThenEventsAreInactiveAndAWarningNamesTheCallback()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker { SendInitialEvents = false };
        speaker.RespondAsIdlePlayer(TestFixtures.KitchenUuid, "Küche");
        RecordingLogger<SonosSystem> logger = null!;

        // Act
        var system = await StartWithListenerAsync(() =>
        {
            logger = new RecordingLogger<SonosSystem>();
            var system = ConnectedSystem.CreateSystem(speaker.Host, logger: logger);
            system.PollingInterval = TimeSpan.FromHours(1);
            system.InitialEventTimeout = TimeSpan.FromMilliseconds(500);
            return system;
        });
        await using var owner = ConnectedSystem.Own(system);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => logger.Warnings.Any(message => message.Contains("EventCallbackHost")),
            ConnectedSystem.WaitTimeout,
            message: "A missing initial event should be reported although the next poll is an hour away.");
        Assert.True(system.IsConnected);
        Assert.NotNull(speaker.GetCallback(AvTransportEventPath));
        Assert.False(system.AreEventsActive);
        Assert.Single(logger.Warnings, message => message.Contains("EventCallbackHost"));
    }

    /// <summary>
    /// Starts a system from <paramref name="create"/> once its event listener runs, retrying on another port when the
    /// chosen one was taken in between, since these tests depend on the listener.
    /// </summary>
    private static Task<SonosSystem> StartWithListenerAsync(Func<SonosSystem> create) =>
        ConnectedSystem.StartWithEventsAsync(
            create,
            system => system.IsConnected && system.ActiveEventCallbackHost is not null,
            "The system should connect with its event listener running.");

    [Fact]
    public async Task WhenTheFirstEventArrives_ThenEventsBecomeActive()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker { SendInitialEvents = false };
        speaker.RespondAsIdlePlayer(TestFixtures.KitchenUuid, "Küche");
        var system = await StartWithListenerAsync(() =>
        {
            var system = ConnectedSystem.CreateSystem(speaker.Host);
            system.PollingInterval = TimeSpan.FromHours(1);
            return system;
        });
        await using var owner = ConnectedSystem.Own(system);
        await AsyncTestHelpers.WaitUntilAsync(
            () => speaker.GetCallback(AvTransportEventPath) is not null && system.LastUpdated is not null,
            ConnectedSystem.WaitTimeout,
            message: "The system should subscribe.");
        Assert.False(system.AreEventsActive);

        // Act
        var status = await speaker.NotifyAsync(AvTransportEventPath, SonosEventBodies.AvTransport(("TransportState", "PLAYING")));

        // Assert
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(system.AreEventsActive);
    }

    [Fact]
    public async Task WhenSeedHostIsUnreachable_ThenStatusIsError()
    {
        // Arrange
        var system = ConnectedSystem.CreateSystem($"127.0.0.1:{LoopbackPorts.GetFreePort()}");
        await using var owner = ConnectedSystem.Own(system);

        // Act
        await system.StartAsync(CancellationToken.None);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => system.Status == ServiceStatus.Error && system.StatusMessage is not null,
            ConnectedSystem.WaitTimeout,
            message: "An unreachable seed should report an error.");
        Assert.False(system.IsConnected);
    }

    [Fact]
    public async Task WhenSeedHostStopsAnswering_ThenAKnownPlayerBecomesTheSeed()
    {
        // Arrange
        var logger = new RecordingLogger<SonosSystem>();
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker, logger: logger);
        var deadHost = $"127.0.0.1:{LoopbackPorts.GetFreePort()}";

        // Act
        connected.System.SeedHost = deadHost;
        await connected.System.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => logger.Warnings.Any(message => message.Contains(deadHost)) &&
                  connected.System.IsConnected && connected.System.Status == ServiceStatus.Running,
            ConnectedSystem.WaitTimeout,
            message: "An unreachable SeedHost should fall back to the known speakers.");
    }

    [Fact]
    public async Task WhenSeedHostDoesNotAnswerAndNoPlayerIsKnown_ThenDiscoveryFindsTheSeed()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        speaker.RespondAsIdlePlayer(TestFixtures.KitchenUuid, "Küche");
        var system = ConnectedSystem.CreateSystem($"127.0.0.1:{LoopbackPorts.GetFreePort()}");
        system.DiscoverSpeakerAsync = _ => Task.FromResult<Uri?>(speaker.BaseUri);
        await using var owner = ConnectedSystem.Own(system);

        // Act
        await system.StartAsync(CancellationToken.None);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => system.IsConnected && system.Players.ContainsKey(TestFixtures.KitchenUuid),
            ConnectedSystem.WaitTimeout,
            message: "An unreachable SeedHost should fall back to discovery.");
    }

    [Fact]
    public async Task WhenTheConnectionKeepsFailing_ThenOnlyTheFirstFailureIsAWarning()
    {
        // Arrange
        var logger = new RecordingLogger<SonosSystem>();
        var system = ConnectedSystem.CreateSystem($"127.0.0.1:{LoopbackPorts.GetFreePort()}", logger: logger);
        await using var owner = ConnectedSystem.Own(system);

        // Act
        await system.StartAsync(CancellationToken.None);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => logger.Entries.Count(entry => entry.Level == LogLevel.Debug && entry.Message.Contains("connection failed")) >= 2,
            ConnectedSystem.WaitTimeout,
            message: "The repeated failures should be logged at Debug.");
        Assert.Single(logger.Warnings, message => message.Contains("connection failed"));
        Assert.Single(logger.Warnings, message => message.Contains("did not answer"));
    }

    [Fact]
    public async Task WhenSeedHostIsInvalid_ThenStatusIsErrorUntilConfigurationFixesIt()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        speaker.RespondAsIdlePlayer(TestFixtures.KitchenUuid, "Küche");
        var system = ConnectedSystem.CreateSystem("http://127.0.0.1:1400");
        await using var owner = ConnectedSystem.Own(system);

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

    [Fact]
    public async Task WhenEventPortIsTaken_ThenSystemConnectsWithPollingOnly()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        speaker.RespondAsIdlePlayer(TestFixtures.KitchenUuid, "Küche");
        await using var portBlocker = new LoopbackHttpServer(_ => Task.CompletedTask);
        var system = ConnectedSystem.CreateSystem(speaker.Host, eventPort: portBlocker.Port);
        await using var owner = ConnectedSystem.Own(system);

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
        {
            system.PollingInterval = TimeSpan.FromHours(1);
            system.MinimumSubscriptionLifetime = TimeSpan.FromSeconds(2);
        });
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
    public async Task WhenTheWallClockStepsBack_ThenSubscriptionsAreStillRenewedOnTime()
    {
        // Arrange
        var clock = new SteppableClock();
        await using var speaker = new FakeSonosSpeaker { SubscriptionTimeoutSeconds = 4 };
        await using var connected = await ConnectedSystem.StartAsync(speaker, clock: clock, configure: system =>
        {
            system.PollingInterval = TimeSpan.FromHours(1);
            system.MinimumSubscriptionLifetime = TimeSpan.FromSeconds(4);
        });

        // Act
        clock.WallClockOffset = TimeSpan.FromHours(-1);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => speaker.Renewed.Contains(AvTransportEventPath),
            ConnectedSystem.WaitTimeout,
            message: "A backward wall-clock step must not postpone the renewal, or the subscription lapses.");
    }

    [Fact]
    public async Task WhenARefreshSubscribesWhileTheLoopSleeps_ThenTheNewSubscriptionsAreRenewedBeforeTheNextPoll()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker { FailSubscriptions = true, SubscriptionTimeoutSeconds = 2 };
        speaker.RespondAsIdlePlayer(TestFixtures.KitchenUuid, "Küche");
        var system = await StartWithListenerAsync(() =>
        {
            var system = ConnectedSystem.CreateSystem(speaker.Host);
            system.PollingInterval = TimeSpan.FromHours(1);
            system.MinimumSubscriptionLifetime = TimeSpan.FromSeconds(2);
            return system;
        });
        await using var owner = ConnectedSystem.Own(system);
        Assert.False(system.AreEventsActive);
        speaker.FailSubscriptions = false;

        // Act
        await system.RefreshAsync(CancellationToken.None);
        var topologyReads = speaker.Calls.Count(call => call.Action == "GetZoneGroupState");

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => system.AreEventsActive,
            ConnectedSystem.WaitTimeout,
            message: "The initial events of the subscriptions made by the refresh should activate events.");
        await AsyncTestHelpers.WaitUntilAsync(
            () => speaker.Renewed.Contains(AvTransportEventPath),
            ConnectedSystem.WaitTimeout,
            message: "The subscriptions made by the refresh should be renewed although the next poll is an hour away.");
        Assert.Equal(topologyReads, speaker.Calls.Count(call => call.Action == "GetZoneGroupState"));
    }

    [Fact]
    public async Task WhenARefreshIsCancelledAfterSubscribing_ThenTheSubscriptionMadeIsStillRenewed()
    {
        // Arrange
        const string renderingControlEventPath = "/MediaRenderer/RenderingControl/Event";
        await using var speaker = new FakeSonosSpeaker { FailSubscriptions = true, SubscriptionTimeoutSeconds = 2 };
        speaker.RespondAsIdlePlayer(TestFixtures.KitchenUuid, "Küche");
        var hold = speaker.HoldSubscribe(renderingControlEventPath);
        var system = await StartWithListenerAsync(() =>
        {
            var system = ConnectedSystem.CreateSystem(speaker.Host);
            system.PollingInterval = TimeSpan.FromHours(1);
            system.MinimumSubscriptionLifetime = TimeSpan.FromSeconds(2);
            return system;
        });
        await using var owner = ConnectedSystem.Own(system);

        try
        {
            speaker.FailSubscriptions = false;
            using var cancellation = new CancellationTokenSource();

            // AVTransport is subscribed first and succeeds; the refresh then waits for the held RenderingControl subscription.
            var refresh = system.RefreshAsync(cancellation.Token);
            await AsyncTestHelpers.WaitUntilAsync(
                () => speaker.GetCallback(renderingControlEventPath) is not null,
                ConnectedSystem.WaitTimeout,
                message: "The refresh should subscribe to RenderingControl after AVTransport.");

            // Act
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(
                () => speaker.Renewed.Contains(AvTransportEventPath),
                ConnectedSystem.WaitTimeout,
                message: "The subscription made before the cancellation should be renewed although the next poll is an hour away.");
        }
        finally
        {
            hold.TrySetResult();
        }
    }

    [Fact]
    public async Task WhenRenewalsFail_ThenTheNextAttemptWaitsForTheRetryDelay()
    {
        // Arrange
        var retryDelay = TimeSpan.FromSeconds(3);
        await using var speaker = new FakeSonosSpeaker { SubscriptionTimeoutSeconds = 2 };
        await using var connected = await ConnectedSystem.StartAsync(speaker, configure: system =>
        {
            system.PollingInterval = TimeSpan.FromHours(1);
            system.MinimumSubscriptionLifetime = TimeSpan.FromSeconds(2);
            system.FailedRenewalRetryDelay = retryDelay;
        });

        // Act
        speaker.AbortRenewals = true;
        var failingSince = DateTimeOffset.UtcNow;

        // Assert
        DateTimeOffset[] attempts = [];
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                attempts = speaker.RenewalAttempts
                    .Where(attempt => attempt.Path == AvTransportEventPath && attempt.At >= failingSince)
                    .Select(attempt => attempt.At)
                    .ToArray();
                return attempts.Length >= 2;
            },
            ConnectedSystem.WaitTimeout,
            message: "The failed renewal should be tried again.");

        // The delay counts from the failure, after the first attempt arrived, so the gap is at least the delay; the
        // tolerance only covers clock granularity. Without the delay the retry would come at the loop's one second floor.
        // No upper bound, since a loaded machine may retry late.
        Assert.True(attempts[1] - attempts[0] >= retryDelay - TimeSpan.FromMilliseconds(100), $"The retry came after {attempts[1] - attempts[0]}.");
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
    public async Task WhenTheSeedIsASatellite_ThenFavoritesAreReadFromAPlayer()
    {
        // Arrange
        const string subwooferUuid = "RINCON_A0000000000201400";
        await using var subwoofer = new FakeSonosSpeaker();
        await using var kitchen = new FakeSonosSpeaker();
        subwoofer.RespondAsIdlePlayer(subwooferUuid, "Küche");
        kitchen.RespondAsIdlePlayer(TestFixtures.KitchenUuid, "Küche");
        foreach (var speaker in new[] { subwoofer, kitchen })
        {
            speaker.RespondWithHomeTheater(kitchen.BaseUri, subwooferUuid, subwoofer.BaseUri);
        }

        // Satellites answer a favorites Browse with a bare HTTP 500.
        subwoofer.RespondWithServerError("Browse");
        var system = ConnectedSystem.CreateSystem(subwoofer.Host);
        await using var owner = ConnectedSystem.Own(system);

        // Act
        await system.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(
            () => system.IsConnected,
            ConnectedSystem.WaitTimeout,
            message: "The system should connect through the subwoofer.");

        // Assert
        Assert.True(system.Players[TestFixtures.KitchenUuid].Satellites.ContainsKey(subwooferUuid));
        Assert.Equal(new[] { "Radio FM1", "SRF 3" }, system.Favorites.Select(favorite => favorite.Title));
        Assert.DoesNotContain(subwoofer.Calls, call => call.Action == "Browse");
    }

    [Fact]
    public async Task WhenTheFirstPlayerFailsToReadFavorites_ThenTheyAreReadFromTheNextPlayer()
    {
        // Arrange
        await using var household = await ConnectedHousehold.StartAsync();
        household.System.PollingInterval = TimeSpan.FromHours(1);
        household.System.SetFavorites([]);
        household.Kitchen.RespondWithServerError("Browse");
        household.Office.RespondWithServerError("Browse");

        // Act
        household.Office.ClearServerError("Browse");
        await household.System.RefreshAsync(CancellationToken.None);
        var favoritesWithOfficeAnswering = household.System.Favorites.Select(favorite => favorite.Title).ToArray();
        household.System.SetFavorites([]);
        household.Office.RespondWithServerError("Browse");
        household.Kitchen.ClearServerError("Browse");
        await household.System.RefreshAsync(CancellationToken.None);

        // Assert
        Assert.Equal(new[] { "Radio FM1", "SRF 3" }, favoritesWithOfficeAnswering);
        Assert.Equal(new[] { "Radio FM1", "SRF 3" }, household.System.Favorites.Select(favorite => favorite.Title));
    }

    [Fact]
    public async Task WhenTheConnectionIsTornDownDuringARefresh_ThenTheRefreshStopsAndTeardownDoesNotWaitForIt()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        var logger = new RecordingLogger<SonosSystem>();
        await using var connected = await ConnectedSystem.StartAsync(speaker, logger: logger, configure: system =>
            system.PollingInterval = TimeSpan.FromHours(1));
        var readsBefore = speaker.Calls.Count(call => call.Action == "GetTransportInfo");
        var hold = speaker.HoldAction("GetTransportInfo");

        try
        {
            var refresh = connected.System.RefreshAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(
                () => speaker.Calls.Count(call => call.Action == "GetTransportInfo") > readsBefore,
                ConnectedSystem.WaitTimeout,
                message: "The refresh should poll the player.");

            // Act
            await connected.System.ApplyConfigurationAsync(CancellationToken.None);

            // Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => refresh.WaitAsync(ConnectedSystem.WaitTimeout));
            Assert.Contains("disconnected", exception.Message);
            Assert.DoesNotContain(logger.Warnings, message => message.Contains("teardown budget"));
        }
        finally
        {
            hold.TrySetResult();
        }
    }

    [Fact]
    public async Task WhenATopologyEventMovesAPlayerToANewAddress_ThenCommandsGoToTheNewAddress()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker, configure: system => system.PollingInterval = TimeSpan.FromHours(1));
        await using var moved = new FakeSonosSpeaker();
        moved.RespondAsIdlePlayer(TestFixtures.KitchenUuid, "Küche");
        var topology = SonosEventBodies.CreateStandaloneTopology((TestFixtures.KitchenUuid, "Küche", moved.BaseUri));

        // Act
        Assert.Equal(HttpStatusCode.OK, await speaker.NotifyAsync(TopologyEventPath, SonosEventBodies.Properties(("ZoneGroupState", topology))));
        await connected.Player.PlayAsync(CancellationToken.None);

        // Assert
        Assert.Contains(moved.Calls, call => call.Action == "Play");
        Assert.DoesNotContain(speaker.Calls, call => call.Action == "Play");
    }

    [Fact]
    public async Task WhenATopologyEventArrivesWhileTheTopologyIsRead_ThenTheOlderReadDoesNotRollItBack()
    {
        // Arrange
        await using var household = await ConnectedHousehold.StartAsync();
        household.System.PollingInterval = TimeSpan.FromHours(1);
        var readsBefore = household.Kitchen.Calls.Count(call => call.Action == "GetZoneGroupState");
        var hold = household.Kitchen.HoldAction("GetZoneGroupState");
        var grouped = SonosEventBodies.CreateGroupTopology(
            (TestFixtures.OfficeUuid, "Büro", household.Office.BaseUri),
            (TestFixtures.KitchenUuid, "Küche", household.Kitchen.BaseUri));

        try
        {
            var refresh = household.System.RefreshAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(
                () => household.Kitchen.Calls.Count(call => call.Action == "GetZoneGroupState") > readsBefore,
                ConnectedSystem.WaitTimeout,
                message: "The refresh should read the topology.");

            // Act
            await household.Kitchen.NotifyAsync(TopologyEventPath, SonosEventBodies.Properties(("ZoneGroupState", grouped)));
            Assert.Equal(TestFixtures.OfficeUuid, household.KitchenPlayer.GroupCoordinatorUuid);
            hold.TrySetResult();
            await refresh;

            // Assert
            Assert.Equal(TestFixtures.OfficeUuid, household.KitchenPlayer.GroupCoordinatorUuid);
            Assert.Equal(2, household.System.Groups[TestFixtures.OfficeUuid].Members.Length);
        }
        finally
        {
            hold.TrySetResult();
        }
    }

    [Fact]
    public async Task WhenAPollReadAnswersWithAFault_ThenThePlayerStaysConnectedAndKeepsTheValue()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        var logger = new RecordingLogger<SonosSystem>();
        await using var connected = await ConnectedSystem.StartAsync(speaker, logger: logger, configure: system =>
            system.PollingInterval = TimeSpan.FromHours(1));
        speaker.RespondWithFault("GetVolume", 701);

        // Act
        await connected.System.RefreshAsync(CancellationToken.None);
        await connected.System.RefreshAsync(CancellationToken.None);

        // Assert
        Assert.True(connected.Player.IsConnected);
        Assert.Null(connected.Player.StatusMessage);
        Assert.Equal(0.44m, connected.Player.Volume);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Debug && entry.Message.Contains("GetVolume"));
        Assert.DoesNotContain(logger.Warnings, message => message.Contains("Polling"));
    }

    [Fact]
    public async Task WhenTheZoneInfoAnswersWithAFault_ThenThePlayerConnectsAndTheZoneInfoIsReadLater()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        speaker.RespondAsIdlePlayer(TestFixtures.KitchenUuid, "Küche");
        speaker.RespondWithFault("GetZoneInfo", 701);
        var system = ConnectedSystem.CreateSystem(speaker.Host);
        system.PollingInterval = TimeSpan.FromHours(1);
        await using var owner = ConnectedSystem.Own(system);

        await system.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(
            () => system.IsConnected && system.Players.TryGetValue(TestFixtures.KitchenUuid, out var player) && player.IsConnected,
            ConnectedSystem.WaitTimeout,
            message: "A fault in the zone info should not keep the player offline.");
        var player = system.Players[TestFixtures.KitchenUuid];
        Assert.Equal("Sonos Ray", player.Model);
        Assert.Null(player.SerialNumber);

        // Act
        speaker.ClearFault("GetZoneInfo");
        await system.RefreshAsync(CancellationToken.None);

        // Assert
        Assert.Equal("00-00-00-00-00-06:D", player.SerialNumber);
    }

    [Fact]
    public async Task WhenAPlayerIsOfflineAfterMissedPolls_ThenItsSubscriptionsAreKept()
    {
        // Arrange
        await using var household = await ConnectedHousehold.StartAsync();
        household.System.PollingInterval = TimeSpan.FromHours(1);
        var officeSubscribes = household.Office.Subscribed.Count;
        household.Office.RespondWithServerError("GetTransportInfo");

        // Act
        await household.System.RefreshAsync(CancellationToken.None);
        await household.System.RefreshAsync(CancellationToken.None);
        var isOfficeConnectedAfterTheMisses = household.OfficePlayer.IsConnected;
        household.Office.ClearServerError("GetTransportInfo");
        await household.System.RefreshAsync(CancellationToken.None);

        // Assert
        Assert.False(isOfficeConnectedAfterTheMisses);
        Assert.True(household.OfficePlayer.IsConnected);
        Assert.Empty(household.Office.Unsubscribed);
        Assert.Equal(officeSubscribes, household.Office.Subscribed.Count);
    }

    [Fact]
    public async Task WhenAGroupCoordinatorIsConnected_ThenFavoritesAreReadFromItBeforeOtherPlayers()
    {
        // Arrange
        await using var household = await ConnectedHousehold.StartAsync();
        household.System.PollingInterval = TimeSpan.FromHours(1);

        // The kitchen stays first among the players, but the office becomes the coordinator.
        foreach (var speaker in new[] { household.Kitchen, household.Office })
        {
            speaker.RespondWithGroup(
                (TestFixtures.OfficeUuid, "Büro", household.Office.BaseUri),
                (TestFixtures.KitchenUuid, "Küche", household.Kitchen.BaseUri));
        }

        var kitchenBrowses = household.Kitchen.Calls.Count(call => call.Action == "Browse");
        var officeBrowses = household.Office.Calls.Count(call => call.Action == "Browse");

        // Act
        await household.System.RefreshAsync(CancellationToken.None);

        // Assert
        Assert.Equal(TestFixtures.KitchenUuid, household.System.Players.Keys.First());
        Assert.Equal(TestFixtures.OfficeUuid, household.KitchenPlayer.GroupCoordinatorUuid);
        Assert.Equal(kitchenBrowses, household.Kitchen.Calls.Count(call => call.Action == "Browse"));
        Assert.Equal(officeBrowses + 1, household.Office.Calls.Count(call => call.Action == "Browse"));
    }

    [Fact]
    public async Task WhenSubscribingKeepsFailing_ThenEachSubscriptionWarnsOnce()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker { FailSubscriptions = true };
        speaker.RespondAsIdlePlayer(TestFixtures.KitchenUuid, "Küche");
        RecordingLogger<SonosSystem> logger = null!;
        var system = await StartWithListenerAsync(() =>
        {
            logger = new RecordingLogger<SonosSystem>();
            var system = ConnectedSystem.CreateSystem(speaker.Host, logger: logger);
            system.PollingInterval = TimeSpan.FromHours(1);
            return system;
        });
        await using var owner = ConnectedSystem.Own(system);

        // Act
        await system.RefreshAsync(CancellationToken.None);
        await system.RefreshAsync(CancellationToken.None);

        // Assert
        Assert.Equal(4, logger.Warnings.Count(message => message.Contains("subscription")));
        Assert.Equal(8, logger.Entries.Count(entry => entry.Level == LogLevel.Debug && entry.Message.Contains("subscription")));
        Assert.False(system.AreEventsActive);
    }

    [Fact]
    public async Task WhenAPollFailsAfterTeardownReleasedItsConnection_ThenThePlayerKeepsTheDisconnectedMessage()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        var connected = await ConnectedSystem.StartAsync(speaker, configure: system =>
            system.PollingInterval = TimeSpan.FromHours(1));
        var transportReads = speaker.Calls.Count(call => call.Action == "GetTransportInfo");
        var hold = speaker.HoldAction("GetTransportInfo");

        try
        {
            // The refresh after the command polls the player and waits for the held answer.
            var play = connected.Player.PlayAsync(CancellationToken.None);
            await AsyncTestHelpers.WaitUntilAsync(
                () => speaker.Calls.Count(call => call.Action == "GetTransportInfo") > transportReads,
                ConnectedSystem.WaitTimeout,
                message: "The refresh after the command should poll the player.");

            // Act
            await connected.System.StopAsync(CancellationToken.None);
            await play.WaitAsync(ConnectedSystem.WaitTimeout);

            // Assert
            Assert.False(connected.Player.IsConnected);
            Assert.Equal("The Sonos system is disconnected.", connected.Player.StatusMessage);
        }
        finally
        {
            hold.TrySetResult();
            connected.System.Dispose();
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
        await using var owner = ConnectedSystem.Own(system);

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
        var executeTask = connected.System.ExecuteTask;
        Assert.NotNull(executeTask);

        // Act
        connected.System.Dispose();
        await executeTask.WaitAsync(ConnectedSystem.WaitTimeout);
        var exception = await Record.ExceptionAsync(() => connected.System.ApplyConfigurationAsync(CancellationToken.None));

        // Assert
        Assert.True(executeTask.IsCompletedSuccessfully);
        Assert.Equal(ServiceStatus.Stopped, connected.System.Status);
        Assert.Null(exception);
    }

    [Fact]
    public async Task WhenConfigurationIsAppliedAgainBeforeTheLoopReacts_ThenNoCallThrows()
    {
        // Arrange
        using var system = ConnectedSystem.CreateSystem("127.0.0.1:1");
        await system.ApplyConfigurationAsync(CancellationToken.None);

        // Act
        var exception = await Record.ExceptionAsync(() => system.ApplyConfigurationAsync(CancellationToken.None));

        // Assert
        Assert.Null(exception);
    }
}
