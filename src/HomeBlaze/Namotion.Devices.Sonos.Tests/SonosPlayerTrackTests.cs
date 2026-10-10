using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosPlayerTrackTests
{
    [Theory]
    [InlineData("https://host.example/live.mp3", "x-rincon-mp3radio://host.example/live.mp3", true)]
    [InlineData("host.example/live.mp3", "x-rincon-mp3radio://host.example/live.mp3", true)]
    [InlineData("x-rincon-mp3radio://host.example/live.mp3", "x-rincon-mp3radio://host.example/live.mp3", true)]
    [InlineData("http://host.example/live", "aac://https://host.example/live", true)]
    [InlineData("host.example/beep.mp3", "https://host.example/beep.mp3", true)]
    // Sonos cuts a title off after 100 characters, so a long URL arrives without its end.
    [InlineData("https://host.example/download/audio.mp3?filename=long-", "x-rincon-mp3radio://host.example/download/audio.mp3?filename=long-name.mp3", true)]
    [InlineData("https://host.example/li", "https://host.example/live.mp3", true)]
    [InlineData("host.example/li", "x-rincon-mp3radio://host.example/live.mp3", false)]
    [InlineData("96", "aac://http://host.example/aac/96", false)]
    [InlineData("live.mp3", "x-rincon-mp3radio://host.example/live.mp3", false)]
    [InlineData("https://host.example/live.mp3", "x-rincon-mp3radio://other.example/live.mp3", false)]
    [InlineData("host.example", "x-rincon-mp3radio://host.example/live.mp3", false)]
    [InlineData("https://host.example/song.mp3", "x-file-cifs://host.example/song.mp3", false)]
    [InlineData("https://host.example/live.mp3", null, false)]
    public void WhenTitleIsTheStreamUri_ThenItIsNotATitle(string title, string? uri, bool expected)
    {
        // Act
        var isUri = SonosPlayer.IsStreamUri(title, uri);

        // Assert
        Assert.Equal(expected, isUri);
    }

    private const string StationUri = "x-sonosapi-stream:s1?sid=303";
    private const string QueueUri = "x-rincon-queue:RINCON_A0000000000601400#0";
    private const string AdUri = "https://ads.example/preroll.mp3";
    private const string AdWithQueryUri = "https://ads.example/preroll.mp3?player=sonos&language=en%2cde";
    private const string AdWithSlashInQueryUri = "https://ads.example/preroll.mp3?redirect=https://cdn.example/a";
    private const string FileUri = "https://files.example/beep.mp3";

    [Theory]
    // A radio track URI, whatever the media.
    [InlineData("96", "aac://http://host.example/aac/96", StationUri, true)]
    [InlineData("96", "aac://http://host.example/aac/96", null, true)]
    [InlineData("live.mp3", "x-rincon-mp3radio://host.example/live.mp3", "x-rincon-mp3radio://host.example/live.mp3", true)]
    [InlineData("host.example/live.mp3", "x-rincon-mp3radio://host.example/live.mp3", "x-rincon-mp3radio://host.example/live.mp3", true)]
    // A station's ad, an http(s) track while the media is the station.
    [InlineData("preroll.mp3", AdUri, StationUri, true)]
    [InlineData("preroll.mp3", AdUri, "x-rincon-mp3radio://host.example/live.mp3", true)]
    [InlineData("preroll.mp3", AdWithQueryUri, StationUri, true)]
    [InlineData("preroll.mp3?player=sonos&language=en%2cde", AdWithQueryUri, StationUri, true)]
    [InlineData("preroll.mp3?player=sonos&language=en,de", AdWithQueryUri, StationUri, true)]
    [InlineData("preroll.mp3", AdWithSlashInQueryUri, StationUri, true)]
    [InlineData("preroll.mp3?redirect=https://cdn.example/a", AdWithSlashInQueryUri, StationUri, true)]
    [InlineData("my ad.mp3", "https://ads.example/my%20ad.mp3", StationUri, true)]
    [InlineData("https://ads.example/preroll.mp3", AdUri, StationUri, true)]
    [InlineData("https://ads.example/pre", AdUri, StationUri, true)]
    [InlineData("a", AdWithSlashInQueryUri, StationUri, false)]
    [InlineData("Advertisement", AdUri, StationUri, false)]
    // An http(s) file played once names itself by its file name, but never by its URL.
    [InlineData("beep.mp3", FileUri, FileUri, false)]
    [InlineData("beep.mp3", FileUri, QueueUri, false)]
    [InlineData("beep.mp3", FileUri, null, false)]
    [InlineData("https://files.example/beep.mp3", FileUri, FileUri, true)]
    [InlineData("files.example/beep.mp3", FileUri, FileUri, true)]
    // Any other track.
    [InlineData("song.mp3", "x-file-cifs://nas/music/song.mp3", QueueUri, false)]
    [InlineData("song.mp3", "x-file-cifs://nas/music/song.mp3", StationUri, false)]
    [InlineData("Song", null, StationUri, false)]
    public void WhenTrackTitleRepeatsTheTrackUri_ThenItIsNotATitle(string title, string? trackUri, string? mediaUri, bool expected)
    {
        // Act
        var isUri = SonosPlayer.IsTitleOfTrackUri(title, trackUri, mediaUri);

        // Assert
        Assert.Equal(expected, isUri);
    }
}
