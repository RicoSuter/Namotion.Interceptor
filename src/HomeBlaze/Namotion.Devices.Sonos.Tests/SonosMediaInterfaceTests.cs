using HomeBlaze.Abstractions.Media;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosMediaInterfaceTests
{
    [Theory]
    [InlineData(typeof(IMediaPlaybackState))]
    [InlineData(typeof(IMediaPlaybackController))]
    public void WhenPlaybackInterfaceIsInspected_ThenItDoesNotInheritVolume(Type playbackInterface)
    {
        // Act
        var inherited = playbackInterface.GetInterfaces();

        // Assert
        Assert.DoesNotContain(typeof(IVolumeState), inherited);
        Assert.DoesNotContain(typeof(IVolumeController), inherited);
    }

    [Fact]
    public void WhenAudioPlayerIsInspected_ThenItComposesPlaybackVolumeAndTrack()
    {
        // Act
        var inherited = typeof(IAudioPlayer).GetInterfaces();

        // Assert
        Assert.Equal(
            new[]
            {
                typeof(IMediaPlaybackController),
                typeof(IMediaPlaybackState),
                typeof(IMediaTrackState),
                typeof(IVolumeController),
                typeof(IVolumeState)
            },
            inherited.OrderBy(type => type.Name, StringComparer.Ordinal));
    }

    [Fact]
    public void WhenPlaybackStateIsInspected_ThenUnknownIsNullAndNotAnEnumMember()
    {
        // Act
        var playbackState = typeof(IMediaPlaybackState).GetProperty(nameof(IMediaPlaybackState.PlaybackState))!;
        var isPlaying = typeof(IMediaPlaybackState).GetProperty(nameof(IMediaPlaybackState.IsPlaying))!;

        // Assert
        Assert.Equal(typeof(MediaPlaybackState?), playbackState.PropertyType);
        Assert.Equal(typeof(bool?), isPlaying.PropertyType);
        Assert.Equal(["Stopped", "Playing", "Paused", "Buffering"], Enum.GetNames<MediaPlaybackState>());
    }

    [Theory]
    [InlineData(MediaPlaybackState.Playing, true)]
    [InlineData(MediaPlaybackState.Buffering, true)]
    [InlineData(MediaPlaybackState.Paused, false)]
    [InlineData(MediaPlaybackState.Stopped, false)]
    [InlineData(null, null)]
    public void WhenPlaybackStateIsGiven_ThenIsPlayingDerivesFromIt(MediaPlaybackState? playbackState, bool? expected)
    {
        // Arrange
        IMediaPlaybackState state = new PlaybackStateStub(playbackState);

        // Act
        var isPlaying = state.IsPlaying;

        // Assert
        Assert.Equal(expected, isPlaying);
    }

    [Theory]
    [InlineData(typeof(SonosPlayer))]
    [InlineData(typeof(SonosGroup))]
    public void WhenPlaybackSubjectIsInspected_ThenItsIsPlayingCanBeUnknown(Type subjectType)
    {
        // Act
        var isPlaying = subjectType.GetProperty(nameof(IMediaPlaybackState.IsPlaying))!;

        // Assert
        Assert.Equal(typeof(bool?), isPlaying.PropertyType);
    }

    [Theory]
    [InlineData(typeof(SonosPlayer))]
    [InlineData(typeof(SonosGroup))]
    public void WhenPlaybackSubjectIsInspected_ThenItIsAnAudioPlayer(Type subjectType)
    {
        // Act
        var isAudioPlayer = typeof(IAudioPlayer).IsAssignableFrom(subjectType);

        // Assert
        Assert.True(isAudioPlayer);
    }

    private sealed class PlaybackStateStub(MediaPlaybackState? playbackState) : IMediaPlaybackState
    {
        public MediaPlaybackState? PlaybackState { get; } = playbackState;
    }
}
