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
}
