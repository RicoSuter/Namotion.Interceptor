using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

/// <summary>
/// Which speaker each operation reaches, with which SOAP action and arguments, in a household where the office
/// coordinates a group with the kitchen.
/// </summary>
public class SonosOperationRoutingTests
{
    private const string Coordinator = "coordinator";
    private const string Player = "player";
    private const string ContainerFavoriteUri = "x-rincon-cpcontainer:playlist";

    [Theory]
    [InlineData("Play", Coordinator, "Play", "Speed", "1")]
    [InlineData("Pause", Coordinator, "Pause", "InstanceID", "0")]
    [InlineData("Stop", Coordinator, "Stop", "InstanceID", "0")]
    [InlineData("Next", Coordinator, "Next", "InstanceID", "0")]
    [InlineData("Previous", Coordinator, "Previous", "InstanceID", "0")]
    [InlineData("TogglePlayback", Coordinator, "Play", "Speed", "1")]
    [InlineData("Seek", Coordinator, "Seek", "Target", "00:01:30")]
    [InlineData("SetVolume", Player, "SetVolume", "DesiredVolume", "50")]
    [InlineData("ChangeVolume", Player, "SetRelativeVolume", "Adjustment", "-10")]
    [InlineData("RampVolume", Player, "RampToVolume", "DesiredVolume", "20")]
    [InlineData("Mute", Player, "SetMute", "DesiredMute", "true")]
    [InlineData("Unmute", Player, "SetMute", "DesiredMute", "false")]
    [InlineData("PlayFavorite", Coordinator, "SetAVTransportURI", "CurrentURI", "x-sonosapi-stream:tunein%3a9557?sid=303&flags=8232&sn=1")]
    [InlineData("PlayContainerFavorite", Coordinator, "AddURIToQueue", "EnqueuedURI", ContainerFavoriteUri)]
    [InlineData("PlayUri", Coordinator, "SetAVTransportURI", "CurrentURI", "http://files.example.com/chime.mp3")]
    [InlineData("PlayStream", Coordinator, "SetAVTransportURI", "CurrentURI", "x-rincon-mp3radio://stream.example.com/live.mp3")]
    [InlineData("SwitchToTv", Player, "SetAVTransportURI", "CurrentURI", $"x-sonos-htastream:{TestFixtures.KitchenUuid}:spdif")]
    [InlineData("SetShuffle", Coordinator, "SetPlayMode", "NewPlayMode", "SHUFFLE_NOREPEAT")]
    [InlineData("SetRepeat", Coordinator, "SetPlayMode", "NewPlayMode", "REPEAT_ONE")]
    [InlineData("SetSleepTimer", Coordinator, "ConfigureSleepTimer", "NewSleepTimerDuration", "00:30:00")]
    [InlineData("SetBass", Player, "SetBass", "DesiredBass", "3")]
    [InlineData("SetTreble", Player, "SetTreble", "DesiredTreble", "-2")]
    [InlineData("SetLoudness", Player, "SetLoudness", "DesiredLoudness", "false")]
    [InlineData("SetNightMode", Player, "SetEQ", "EQType", "NightMode")]
    [InlineData("SetSpeechEnhancement", Player, "SetEQ", "EQType", "DialogLevel")]
    [InlineData("LeaveGroup", Player, "BecomeCoordinatorOfStandaloneGroup", "InstanceID", "0")]
    public async Task WhenAGroupMemberIsCommanded_ThenTheCommandReachesTheRoutedSpeaker(
        string operation, string target, string expectedAction, string argument, string expectedValue)
    {
        // Arrange
        await using var household = await ConnectedHousehold.StartAsync(isGrouped: true, configure: system =>
            system.PollingInterval = TimeSpan.FromHours(1));
        household.System.SetFavorites([
            .. household.System.Favorites,
            new SonosFavorite("Playlist", ContainerFavoriteUri, IsContainer: true, ImageUri: null) { Metadata = "<DIDL-Lite />" }
        ]);
        var (expected, other) = target == Coordinator ? (household.Office, household.Kitchen) : (household.Kitchen, household.Office);

        // Act
        await InvokePlayerOperationAsync(household.KitchenPlayer, operation);

        // Assert
        Assert.Contains(expected.Calls, call => call.Action == expectedAction && call.GetArgument(argument) == expectedValue);
        Assert.DoesNotContain(other.Calls, call => call.Action == expectedAction);
    }

    private static Task InvokePlayerOperationAsync(SonosPlayer player, string operation) => operation switch
    {
        "Play" => player.PlayAsync(CancellationToken.None),
        "Pause" => player.PauseAsync(CancellationToken.None),
        "Stop" => player.StopAsync(CancellationToken.None),
        "Next" => player.NextAsync(CancellationToken.None),
        "Previous" => player.PreviousAsync(CancellationToken.None),
        "TogglePlayback" => player.TogglePlaybackAsync(CancellationToken.None),
        "Seek" => player.SeekAsync(TimeSpan.FromSeconds(90), CancellationToken.None),
        "SetVolume" => player.SetVolumeAsync(0.5m, CancellationToken.None),
        "ChangeVolume" => player.ChangeVolumeAsync(-0.1m, CancellationToken.None),
        "RampVolume" => player.RampVolumeAsync(0.2m, CancellationToken.None),
        "Mute" => player.MuteAsync(CancellationToken.None),
        "Unmute" => player.UnmuteAsync(CancellationToken.None),
        "PlayFavorite" => player.PlayFavoriteAsync("radio fm1", CancellationToken.None),
        "PlayContainerFavorite" => player.PlayFavoriteAsync("Playlist", CancellationToken.None),
        "PlayUri" => player.PlayUriAsync("http://files.example.com/chime.mp3", CancellationToken.None),
        "PlayStream" => player.PlayStreamAsync("http://stream.example.com/live.mp3", "Live", CancellationToken.None),
        "SwitchToTv" => player.SwitchToTvAsync(CancellationToken.None),
        "SetShuffle" => player.SetShuffleAsync(true, CancellationToken.None),
        "SetRepeat" => player.SetRepeatAsync(SonosRepeatMode.One, CancellationToken.None),
        "SetSleepTimer" => player.SetSleepTimerAsync(TimeSpan.FromMinutes(30), CancellationToken.None),
        "SetBass" => player.SetBassAsync(3, CancellationToken.None),
        "SetTreble" => player.SetTrebleAsync(-2, CancellationToken.None),
        "SetLoudness" => player.SetLoudnessAsync(false, CancellationToken.None),
        "SetNightMode" => player.SetNightModeAsync(true, CancellationToken.None),
        "SetSpeechEnhancement" => player.SetSpeechEnhancementAsync(true, CancellationToken.None),
        "LeaveGroup" => player.LeaveGroupAsync(CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    [Theory]
    [InlineData("Play", "Play", "Speed", "1")]
    [InlineData("Pause", "Pause", "InstanceID", "0")]
    [InlineData("Stop", "Stop", "InstanceID", "0")]
    [InlineData("Next", "Next", "InstanceID", "0")]
    [InlineData("Previous", "Previous", "InstanceID", "0")]
    [InlineData("TogglePlayback", "Play", "Speed", "1")]
    [InlineData("Seek", "Seek", "Target", "00:01:30")]
    [InlineData("SetVolume", "SetGroupVolume", "DesiredVolume", "30")]
    [InlineData("ChangeVolume", "SetRelativeGroupVolume", "Adjustment", "5")]
    [InlineData("Mute", "SetGroupMute", "DesiredMute", "true")]
    [InlineData("Unmute", "SetGroupMute", "DesiredMute", "false")]
    [InlineData("PlayFavorite", "SetAVTransportURI", "CurrentURI", "x-sonosapi-stream:tunein%3a9557?sid=303&flags=8232&sn=1")]
    [InlineData("PlayUri", "SetAVTransportURI", "CurrentURI", "http://files.example.com/chime.mp3")]
    [InlineData("PlayStream", "SetAVTransportURI", "CurrentURI", "x-rincon-mp3radio://stream.example.com/live.mp3")]
    [InlineData("SetShuffle", "SetPlayMode", "NewPlayMode", "SHUFFLE_NOREPEAT")]
    [InlineData("SetRepeat", "SetPlayMode", "NewPlayMode", "REPEAT_ALL")]
    [InlineData("SetSleepTimer", "ConfigureSleepTimer", "NewSleepTimerDuration", "00:30:00")]
    public async Task WhenAGroupIsCommanded_ThenTheCommandReachesOnlyTheCoordinator(
        string operation, string expectedAction, string argument, string expectedValue)
    {
        // Arrange
        await using var household = await ConnectedHousehold.StartAsync(isGrouped: true, configure: system =>
            system.PollingInterval = TimeSpan.FromHours(1));
        var group = household.System.Groups[TestFixtures.OfficeUuid];

        // Act
        await (operation switch
        {
            "Play" => group.PlayAsync(CancellationToken.None),
            "Pause" => group.PauseAsync(CancellationToken.None),
            "Stop" => group.StopAsync(CancellationToken.None),
            "Next" => group.NextAsync(CancellationToken.None),
            "Previous" => group.PreviousAsync(CancellationToken.None),
            "TogglePlayback" => group.TogglePlaybackAsync(CancellationToken.None),
            "Seek" => group.SeekAsync(TimeSpan.FromSeconds(90), CancellationToken.None),
            "SetVolume" => group.SetVolumeAsync(0.3m, CancellationToken.None),
            "ChangeVolume" => group.ChangeVolumeAsync(0.05m, CancellationToken.None),
            "Mute" => group.MuteAsync(CancellationToken.None),
            "Unmute" => group.UnmuteAsync(CancellationToken.None),
            "PlayFavorite" => group.PlayFavoriteAsync("radio fm1", CancellationToken.None),
            "PlayUri" => group.PlayUriAsync("http://files.example.com/chime.mp3", CancellationToken.None),
            "PlayStream" => group.PlayStreamAsync("http://stream.example.com/live.mp3", "Live", CancellationToken.None),
            "SetShuffle" => group.SetShuffleAsync(true, CancellationToken.None),
            "SetRepeat" => group.SetRepeatAsync(SonosRepeatMode.All, CancellationToken.None),
            "SetSleepTimer" => group.SetSleepTimerAsync(TimeSpan.FromMinutes(30), CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        });

        // Assert
        Assert.Contains(household.Office.Calls, call => call.Action == expectedAction && call.GetArgument(argument) == expectedValue);
        Assert.DoesNotContain(household.Kitchen.Calls, call => call.Action == expectedAction);
    }

    [Fact]
    public async Task WhenPlayerWithLineInSwitchesToIt_ThenItsOwnTransportPlaysTheLineIn()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        speaker.DeviceDescription = speaker.DeviceDescription.Replace(
            "</serviceList>",
            "<service><serviceType>urn:schemas-upnp-org:service:AudioIn:1</serviceType><serviceId>urn:upnp-org:serviceId:AudioIn</serviceId></service></serviceList>",
            StringComparison.Ordinal);
        await using var connected = await ConnectedSystem.StartAsync(speaker);
        Assert.True(connected.Player.HasLineIn);

        // Act
        await connected.Player.SwitchToLineInAsync(CancellationToken.None);

        // Assert
        Assert.Contains(speaker.Calls, call => call.Action == "SetAVTransportURI" &&
                                               call.GetArgument("CurrentURI") == $"x-rincon-stream:{TestFixtures.KitchenUuid}");
    }

    [Theory]
    [InlineData("Büro")]
    [InlineData(TestFixtures.OfficeUuid)]
    public async Task WhenPlayerJoinsAnotherRoom_ThenItFollowsThatCoordinatorAndTheTopologyIsReadBack(string room)
    {
        // Arrange
        await using var household = await ConnectedHousehold.StartAsync(configure: system => system.PollingInterval = TimeSpan.FromHours(1));
        var topologyReads = household.Kitchen.Calls.Count(call => call.Action == "GetZoneGroupState");

        // Act
        await household.KitchenPlayer.JoinGroupAsync(room, CancellationToken.None);

        // Assert
        Assert.Contains(household.Kitchen.Calls, call => call.Action == "SetAVTransportURI" &&
                                                         call.GetArgument("CurrentURI") == $"x-rincon:{TestFixtures.OfficeUuid}");
        Assert.DoesNotContain(household.Office.Calls, call => call.Action == "SetAVTransportURI");
        Assert.True(household.Kitchen.Calls.Count(call => call.Action == "GetZoneGroupState") > topologyReads);
    }

    [Fact]
    public async Task WhenUngroupingAll_ThenEveryMemberLeavesAndTheTopologyIsReadBack()
    {
        // Arrange
        await using var household = await ConnectedHousehold.StartAsync(isGrouped: true, configure: system =>
            system.PollingInterval = TimeSpan.FromHours(1));
        var topologyReads = household.Kitchen.Calls.Count(call => call.Action == "GetZoneGroupState");

        // Act
        await household.System.UngroupAllAsync(CancellationToken.None);

        // Assert
        Assert.Contains(household.Kitchen.Calls, call => call.Action == "BecomeCoordinatorOfStandaloneGroup");
        Assert.DoesNotContain(household.Office.Calls, call => call.Action == "BecomeCoordinatorOfStandaloneGroup");
        Assert.True(household.Kitchen.Calls.Count(call => call.Action == "GetZoneGroupState") > topologyReads);
    }

    [Theory]
    [InlineData("Küche")]
    [InlineData(TestFixtures.KitchenUuid)]
    public async Task WhenGroupingAllIntoAGroupMember_ThenItLeavesItsGroupAndTheOthersJoinIt(string room)
    {
        // Arrange
        await using var household = await ConnectedHousehold.StartAsync(isGrouped: true, configure: system =>
            system.PollingInterval = TimeSpan.FromHours(1));

        // Act
        await household.System.GroupAllAsync(room, CancellationToken.None);

        // Assert
        Assert.Contains(household.Kitchen.Calls, call => call.Action == "BecomeCoordinatorOfStandaloneGroup");
        Assert.Contains(household.Office.Calls, call => call.Action == "SetAVTransportURI" &&
                                                        call.GetArgument("CurrentURI") == $"x-rincon:{TestFixtures.KitchenUuid}");
        Assert.DoesNotContain(household.Kitchen.Calls, call => call.Action == "SetAVTransportURI");
    }
}
