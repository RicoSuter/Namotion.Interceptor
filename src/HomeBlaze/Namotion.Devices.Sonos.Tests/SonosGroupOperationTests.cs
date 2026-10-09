using HomeBlaze.Abstractions.Media;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;
using static Namotion.Devices.Sonos.Tests.Testing.TestFixtures;

namespace Namotion.Devices.Sonos.Tests;

public class SonosGroupOperationTests
{
    [Fact]
    public async Task WhenSettingGroupVolume_ThenSnapshotPrecedesSetGroupVolume()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);
        var group = Assert.Single(connected.System.Groups).Value;

        // Act
        await group.SetVolumeAsync(0.3m, CancellationToken.None);

        // Assert
        var actions = speaker.Calls.Select(call => call.Action).ToList();
        var snapshot = actions.IndexOf("SnapshotGroupVolume");
        var setVolume = actions.IndexOf("SetGroupVolume");
        Assert.True(snapshot >= 0 && setVolume > snapshot);
        Assert.Contains(speaker.Calls, call => call.Action == "SetGroupVolume" && call.GetArgument("DesiredVolume") == "30");
    }

    [Fact]
    public async Task WhenGroupIsMutedThroughTheVolumeController_ThenGroupMuteIsSentToTheCoordinator()
    {
        // Arrange
        await using var household = await ConnectedHousehold.StartAsync(isGrouped: true);
        IVolumeController group = household.System.Groups[TestFixtures.OfficeUuid];

        // Act
        await group.MuteAsync(CancellationToken.None);

        // Assert
        Assert.Contains(household.Office.Calls, call => call.Action == "SetGroupMute" && call.GetArgument("DesiredMute") == "true");
        Assert.DoesNotContain(household.Kitchen.Calls, call => call.Action == "SetGroupMute");
    }

    [Fact]
    public async Task WhenGroupPlaysUnknownFavorite_ThenThrowsListingTheFavorites()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);
        var group = Assert.Single(connected.System.Groups).Value;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => group.PlayFavoriteAsync("Nothing", CancellationToken.None));
        Assert.Equal("title", exception.ParamName);
        Assert.Contains("Radio FM1", exception.Message);
    }

    [Fact]
    public async Task WhenGroupSystemIsNotConnected_ThenPauseThrows()
    {
        // Arrange
        var system = CreateHousehold();
        var group = system.Groups[TestFixtures.LivingRoomUuid];

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => group.PauseAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(false, -0.01)]
    [InlineData(false, 1.01)]
    [InlineData(false, 20)]
    [InlineData(true, -1.01)]
    [InlineData(true, 1.01)]
    [InlineData(true, 20)]
    public async Task WhenGroupVolumeIsOutOfRange_ThenThrowsBeforeConnecting(bool isChange, double value)
    {
        // Arrange
        var group = CreateHousehold().Groups[LivingRoomUuid];

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => isChange
            ? group.ChangeVolumeAsync((decimal)value, CancellationToken.None)
            : group.SetVolumeAsync((decimal)value, CancellationToken.None));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, -1)]
    [InlineData(true, 1)]
    public async Task WhenGroupVolumeIsAtTheLimit_ThenItIsAccepted(bool isChange, double value)
    {
        // Arrange
        var group = CreateHousehold().Groups[LivingRoomUuid];

        // Act & Assert
        // Not connected, so an accepted value fails at the connection instead of the argument check.
        await Assert.ThrowsAsync<InvalidOperationException>(() => isChange
            ? group.ChangeVolumeAsync((decimal)value, CancellationToken.None)
            : group.SetVolumeAsync((decimal)value, CancellationToken.None));
    }

    [Fact]
    public async Task WhenGroupHasMembers_ThenVolumeIsSentToTheCoordinatorOnly()
    {
        // Arrange
        await using var household = await ConnectedHousehold.StartAsync(isGrouped: true);
        var group = household.System.Groups[TestFixtures.OfficeUuid];
        Assert.Equal(2, group.Members.Length);

        // Act
        await group.SetVolumeAsync(0.3m, CancellationToken.None);

        // Assert
        Assert.Contains(household.Office.Calls, call => call.Action == "SetGroupVolume");
        Assert.DoesNotContain(household.Kitchen.Calls, call => call.Action == "SetGroupVolume");
    }

    [Fact]
    public void WhenGroupSystemIsNotConnected_ThenOperationsAreDisabled()
    {
        // Arrange
        var system = CreateHousehold();
        var group = system.Groups[TestFixtures.LivingRoomUuid];

        // Assert
        Assert.False(group.Stop_IsEnabled);
        Assert.False(group.Next_IsEnabled);
        Assert.False(group.Previous_IsEnabled);
        Assert.False(group.TogglePlayback_IsEnabled);
        Assert.False(group.SetVolume_IsEnabled);
        Assert.False(group.ChangeVolume_IsEnabled);
        Assert.False(group.Mute_IsEnabled);
        Assert.False(group.Unmute_IsEnabled);
        Assert.False(group.PlayFavorite_IsEnabled);
        Assert.False(group.PlayUri_IsEnabled);
        Assert.False(group.PlayStream_IsEnabled);
        Assert.False(group.SetShuffle_IsEnabled);
        Assert.False(group.SetRepeat_IsEnabled);
        Assert.False(group.SetSleepTimer_IsEnabled);
    }

    [Fact]
    public async Task WhenGroupIsConnected_ThenOperationsAreEnabled()
    {
        // Act
        await using var speaker = new FakeSonosSpeaker();
        await using var connected = await ConnectedSystem.StartAsync(speaker);
        var group = Assert.Single(connected.System.Groups).Value;

        // Assert
        Assert.True(group.Stop_IsEnabled);
        Assert.True(group.Next_IsEnabled);
        Assert.True(group.Previous_IsEnabled);
        Assert.True(group.TogglePlayback_IsEnabled);
        Assert.True(group.SetVolume_IsEnabled);
        Assert.True(group.ChangeVolume_IsEnabled);
        Assert.True(group.Mute_IsEnabled);
        Assert.True(group.Unmute_IsEnabled);
        Assert.True(group.PlayFavorite_IsEnabled);
        Assert.True(group.PlayUri_IsEnabled);
        Assert.True(group.PlayStream_IsEnabled);
        Assert.True(group.SetShuffle_IsEnabled);
        Assert.True(group.SetRepeat_IsEnabled);
        Assert.True(group.SetSleepTimer_IsEnabled);
    }
}
