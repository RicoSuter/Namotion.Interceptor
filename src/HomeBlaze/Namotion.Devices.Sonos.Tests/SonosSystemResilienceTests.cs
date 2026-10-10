using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Media;
using Namotion.Devices.Sonos.Tests.Testing;
using Namotion.Interceptor.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

/// <summary>
/// The periodic poll of a running system and how it copes with speakers that fail, move or come back.
/// </summary>
public class SonosSystemResilienceTests
{
    private const string AvTransportEventPath = "/MediaRenderer/AVTransport/Event";
    private const string RenderingControlEventPath = "/MediaRenderer/RenderingControl/Event";
    private const string ReconciliationFailed = "Sonos reconciliation failed";

    [Fact]
    public async Task WhenEventsAreUnavailable_ThenThePeriodicPollPicksUpAChange()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker { FailSubscriptions = true };
        speaker.RespondAsIdlePlayer(TestFixtures.KitchenUuid, "Küche");
        var system = ConnectedSystem.CreateSystem(speaker.Host);
        system.PollingInterval = TimeSpan.FromSeconds(1);
        await using var owner = ConnectedSystem.Own(system);
        await system.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(
            () => system.IsConnected && system.Players.TryGetValue(TestFixtures.KitchenUuid, out var player) && player.Volume == 0.44m,
            ConnectedSystem.WaitTimeout,
            message: "The system should connect without events.");

        // Act
        speaker.Respond("GetVolume", ("CurrentVolume", "12"));

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => system.Players[TestFixtures.KitchenUuid].Volume == 0.12m,
            ConnectedSystem.WaitTimeout,
            message: "The periodic poll should pick up the new volume without events.");
        Assert.False(system.AreEventsActive);
        Assert.True(system.IsConnected);
        Assert.Equal(ServiceStatus.Running, system.Status);
    }

    [Fact]
    public async Task WhenAPollSucceedsBetweenFailures_ThenTheFailureCountStartsOver()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        var logger = new RecordingLogger<SonosSystem>();
        await using var connected = await ConnectedSystem.StartAsync(speaker, logger: logger, configure: system =>
            system.PollingInterval = TimeSpan.FromSeconds(1));
        speaker.RespondWithFault("GetZoneGroupState", 501);
        await AsyncTestHelpers.WaitUntilAsync(
            () => logger.Warnings.Any(message => message.Contains(ReconciliationFailed)),
            ConnectedSystem.WaitTimeout,
            message: "The periodic poll should fail.");
        speaker.ClearFault("GetZoneGroupState");

        // The failed poll's read is already recorded, so a later read belongs to a poll that started after the clear.
        var topologyReads = speaker.Calls.Count(call => call.Action == "GetZoneGroupState");
        await AsyncTestHelpers.WaitUntilAsync(
            () => speaker.Calls.Count(call => call.Action == "GetZoneGroupState") > topologyReads &&
                  connected.System.IsConnected && connected.System.StatusMessage is null,
            ConnectedSystem.WaitTimeout,
            message: "The next periodic poll should succeed.");
        var failuresBefore = logger.Warnings.Count(message => message.Contains(ReconciliationFailed));

        // Act
        speaker.RespondWithFault("GetZoneGroupState", 501);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => logger.Warnings.Count(message => message.Contains(ReconciliationFailed)) > failuresBefore,
            ConnectedSystem.WaitTimeout,
            message: "The periodic poll should fail again.");

        // Whatever happened before the success, the first failure after it counts from one again.
        var firstFailureAfterSuccess = logger.Warnings.Where(message => message.Contains(ReconciliationFailed)).ElementAt(failuresBefore);
        Assert.Contains("(1 of 3)", firstFailureAfterSuccess);
    }

    [Fact]
    public async Task WhenAPlayerStopsAnswering_ThenOnlyItGoesOfflineWithOneWarningAndRecovers()
    {
        // Arrange
        var logger = new RecordingLogger<SonosSystem>();
        await using var household = await ConnectedHousehold.StartAsync(logger: logger, configure: system =>
            system.PollingInterval = TimeSpan.FromHours(1));
        household.Office.RespondWithServerError("GetTransportInfo");

        // Act
        await household.System.RefreshAsync(CancellationToken.None);
        await household.System.RefreshAsync(CancellationToken.None);
        var isOfficeConnectedWhileFailing = household.OfficePlayer.IsConnected;
        var officeStatusWhileFailing = household.OfficePlayer.StatusMessage;
        household.Office.ClearServerError("GetTransportInfo");
        await household.System.RefreshAsync(CancellationToken.None);

        // Assert
        Assert.False(isOfficeConnectedWhileFailing);
        Assert.NotNull(officeStatusWhileFailing);
        Assert.Single(logger.Warnings, message => message.Contains("Polling the Sonos player in Büro failed"));
        Assert.True(household.KitchenPlayer.IsConnected);
        Assert.True(household.System.IsConnected);
        Assert.True(household.OfficePlayer.IsConnected);
        Assert.Null(household.OfficePlayer.StatusMessage);
    }

    [Fact]
    public async Task WhenAPlayingPlayerMissesOnePoll_ThenItStaysConnectedAndKeepsReportingPlaying()
    {
        // Arrange
        var logger = new RecordingLogger<SonosSystem>();
        await using var household = await ConnectedHousehold.StartAsync(logger: logger, configure: system =>
            system.PollingInterval = TimeSpan.FromHours(1));
        household.Office.Respond("GetTransportInfo", ("CurrentTransportState", "PLAYING"), ("CurrentTransportStatus", "OK"), ("CurrentSpeed", "1"));
        await household.System.RefreshAsync(CancellationToken.None);
        var player = household.OfficePlayer;
        Assert.Equal(MediaPlaybackState.Playing, player.PlaybackState);

        // Act
        household.Office.RespondWithServerError("GetTransportInfo");
        await household.System.RefreshAsync(CancellationToken.None);
        var isConnectedAfterTheFailure = player.IsConnected;
        var playbackStateAfterTheFailure = player.PlaybackState;
        var isPlayEnabledAfterTheFailure = player.Play_IsEnabled;
        household.Office.ClearServerError("GetTransportInfo");
        await household.System.RefreshAsync(CancellationToken.None);
        household.Office.RespondWithServerError("GetTransportInfo");
        await household.System.RefreshAsync(CancellationToken.None);

        // Assert
        Assert.True(isConnectedAfterTheFailure);
        Assert.Equal(MediaPlaybackState.Playing, playbackStateAfterTheFailure);
        Assert.True(isPlayEnabledAfterTheFailure);
        Assert.True(player.IsConnected);
        Assert.Equal(MediaPlaybackState.Playing, player.PlaybackState);
        Assert.DoesNotContain(logger.Warnings, message => message.Contains("Polling the Sonos player in Büro failed"));
    }

    [Fact]
    public async Task WhenAPlayingPlayerMissesTwoPollsInARow_ThenItsPlaybackIsUnknownUntilItAnswers()
    {
        // Arrange
        await using var household = await ConnectedHousehold.StartAsync(configure: system =>
            system.PollingInterval = TimeSpan.FromHours(1));
        household.Office.Respond("GetTransportInfo", ("CurrentTransportState", "PLAYING"), ("CurrentTransportStatus", "OK"), ("CurrentSpeed", "1"));
        await household.System.RefreshAsync(CancellationToken.None);
        var player = household.OfficePlayer;
        var group = household.System.Groups[TestFixtures.OfficeUuid];
        Assert.Equal(MediaPlaybackState.Playing, player.PlaybackState);
        Assert.Equal(MediaPlaybackState.Playing, group.PlaybackState);

        // Act
        household.Office.RespondWithServerError("GetTransportInfo");
        await household.System.RefreshAsync(CancellationToken.None);
        await household.System.RefreshAsync(CancellationToken.None);
        var playbackStateWhileFailing = player.PlaybackState;
        var groupPlaybackStateWhileFailing = group.PlaybackState;
        var sourceWhileFailing = player.Source;
        household.Office.ClearServerError("GetTransportInfo");
        await household.System.RefreshAsync(CancellationToken.None);

        // Assert
        Assert.Null(playbackStateWhileFailing);
        Assert.Null(groupPlaybackStateWhileFailing);
        Assert.Equal(SonosSource.SpotifyConnect, sourceWhileFailing);
        Assert.Equal(MediaPlaybackState.Playing, player.PlaybackState);
        Assert.Equal(MediaPlaybackState.Playing, group.PlaybackState);
    }

    [Fact]
    public async Task WhenAPlayerReturnsFromAnOutageWithATransportFault_ThenThePreOutageStateDoesNotResurface()
    {
        // Arrange
        await using var household = await ConnectedHousehold.StartAsync(configure: system =>
            system.PollingInterval = TimeSpan.FromHours(1));
        household.Office.Respond("GetTransportInfo", ("CurrentTransportState", "PLAYING"), ("CurrentTransportStatus", "OK"), ("CurrentSpeed", "1"));
        await household.System.RefreshAsync(CancellationToken.None);
        var player = household.OfficePlayer;
        Assert.Equal(MediaPlaybackState.Playing, player.PlaybackState);
        household.Office.RespondWithServerError("GetTransportInfo");
        await household.System.RefreshAsync(CancellationToken.None);
        await household.System.RefreshAsync(CancellationToken.None);
        Assert.False(player.IsConnected);

        // Act
        household.Office.ClearServerError("GetTransportInfo");
        household.Office.RespondWithFault("GetTransportInfo", 701);
        await household.System.RefreshAsync(CancellationToken.None);

        // Assert
        Assert.True(player.IsConnected);
        Assert.Null(player.PlaybackState);
    }

    [Fact]
    public async Task WhenASatelliteDoesNotAnswer_ThenItIsOfflineWithOneWarningUntilItAnswers()
    {
        // Arrange
        const string subwooferUuid = "RINCON_A0000000000201400";
        await using var kitchen = new FakeSonosSpeaker();
        await using var subwoofer = new FakeSonosSpeaker();
        kitchen.RespondAsIdlePlayer(TestFixtures.KitchenUuid, "Küche");
        subwoofer.RespondAsIdlePlayer(subwooferUuid, "Küche");
        kitchen.RespondWithHomeTheater(kitchen.BaseUri, subwooferUuid, subwoofer.BaseUri);
        subwoofer.RespondWithServerError("GetZoneInfo");
        var logger = new RecordingLogger<SonosSystem>();
        var system = ConnectedSystem.CreateSystem(kitchen.Host, logger: logger);
        system.PollingInterval = TimeSpan.FromHours(1);
        await using var owner = ConnectedSystem.Own(system);
        await system.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(
            () => system.IsConnected && system.Players.TryGetValue(TestFixtures.KitchenUuid, out var player) && player.Satellites.ContainsKey(subwooferUuid),
            ConnectedSystem.WaitTimeout,
            message: "The system should connect to the home theater.");
        var satellite = system.Players[TestFixtures.KitchenUuid].Satellites[subwooferUuid];

        // Act
        await system.RefreshAsync(CancellationToken.None);
        var isConnectedWhileFailing = satellite.IsConnected;
        var statusWhileFailing = satellite.StatusMessage;
        subwoofer.ClearServerError("GetZoneInfo");
        await system.RefreshAsync(CancellationToken.None);

        // Assert
        Assert.False(isConnectedWhileFailing);
        Assert.NotNull(statusWhileFailing);
        Assert.Single(logger.Warnings, message => message.Contains($"Reading the Sonos satellite {subwooferUuid} failed"));
        Assert.True(system.Players[TestFixtures.KitchenUuid].IsConnected);
        Assert.True(satellite.IsConnected);
        Assert.Null(satellite.StatusMessage);
        Assert.Equal("00-00-00-00-00-06:D", satellite.SerialNumber);
    }

    [Fact]
    public async Task WhenAPlayerMovesToANewAddress_ThenItsConnectionAndSubscriptionsMoveWithIt()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker, configure: system => system.PollingInterval = TimeSpan.FromHours(1));
        await using var moved = new FakeSonosSpeaker();
        moved.RespondAsIdlePlayer(TestFixtures.KitchenUuid, "Küche");
        foreach (var answering in new[] { speaker, moved })
        {
            answering.RespondWithTopology((TestFixtures.KitchenUuid, "Küche", moved.BaseUri));
        }

        // Act
        await connected.System.RefreshAsync(CancellationToken.None);
        await connected.Player.SetVolumeAsync(0.3m, CancellationToken.None);

        // Assert
        Assert.Equal(moved.BaseUri.Host, connected.Player.IpAddress);
        Assert.Contains(moved.Calls, call => call.Action == "SetVolume" && call.GetArgument("DesiredVolume") == "30");
        Assert.DoesNotContain(speaker.Calls, call => call.Action == "SetVolume");
        Assert.Contains(moved.Calls, call => call.Action == "GetZoneInfo");
        Assert.Contains(AvTransportEventPath, speaker.Unsubscribed);
        Assert.Contains(RenderingControlEventPath, speaker.Unsubscribed);
        Assert.Contains(AvTransportEventPath, moved.Subscribed);
        Assert.Contains(RenderingControlEventPath, moved.Subscribed);
    }

    [Fact]
    public async Task WhenTheSeedFailsWhileConnected_ThenAnotherConnectedPlayerBecomesTheSeed()
    {
        // Arrange
        var logger = new RecordingLogger<SonosSystem>();
        await using var household = await ConnectedHousehold.StartAsync(logger: logger, configure: system =>
            system.PollingInterval = TimeSpan.FromSeconds(1));
        var officeTopologyReads = household.Office.Calls.Count(call => call.Action == "GetZoneGroupState");

        // Act
        household.Kitchen.RespondWithFault("GetZoneGroupState", 501);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => household.Office.Calls.Count(call => call.Action == "GetZoneGroupState") > officeTopologyReads &&
                  household.System.StatusMessage is null,
            ConnectedSystem.WaitTimeout,
            message: "After a failed poll, the next one should read the topology from the office.");
        Assert.Single(logger.Warnings, message => message.Contains(ReconciliationFailed));
        Assert.DoesNotContain(logger.Warnings, message => message.Contains("connection failed"));
        Assert.True(household.System.IsConnected);
        Assert.Equal(ServiceStatus.Running, household.System.Status);
    }
}
