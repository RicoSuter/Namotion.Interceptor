using System.Text;
using Namotion.Devices.Sonos.Client;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosConnectionRequestTests
{
    private const string Metadata = """<DIDL-Lite xmlns:dc="http://purl.org/dc/elements/1.1/"><item id="1"><dc:title>Tom & Jerry's "best" <3</dc:title></item></DIDL-Lite>""";

    /// <summary>
    /// The fixture was recorded from the SOAP library this project used before, so it pins every request to what the
    /// speakers have been receiving: path, headers and body. A request may only change here on purpose.
    /// </summary>
    [Fact]
    public async Task WhenCallingEveryMethodOfAConnection_ThenTheRequestsAreTheRecordedOnes()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        speaker.RespondAsIdlePlayer(TestFixtures.KitchenUuid, "Küche");
        var recorder = new RecordingHandler();
        using var httpClient = new HttpClient(recorder);
        var connection = new SonosConnection(speaker.BaseUri, TestFixtures.KitchenUuid, httpClient);
        var none = CancellationToken.None;

        // Act
        await recorder.RunAsync("ReadZoneInfo", () => connection.ReadZoneInfoAsync([], none));
        await recorder.RunAsync("ReadZoneGroupState", () => connection.ReadZoneGroupStateAsync(none));
        await recorder.RunAsync("ReadFavorites", () => connection.ReadFavoritesAsync(none));
        await recorder.RunAsync("ReadPlayer", () => connection.ReadPlayerAsync(true, [], none));
        await recorder.RunAsync("ReadGroup", () => connection.ReadGroupAsync([], none));
        await recorder.RunAsync("Play", () => connection.PlayAsync(none));
        await recorder.RunAsync("Pause", () => connection.PauseAsync(none));
        await recorder.RunAsync("Stop", () => connection.StopAsync(none));
        await recorder.RunAsync("Next", () => connection.NextAsync(none));
        await recorder.RunAsync("Previous", () => connection.PreviousAsync(none));
        await recorder.RunAsync("TogglePlayback", () => connection.TogglePlaybackAsync(none));
        await recorder.RunAsync("Seek", () => connection.SeekAsync(TimeSpan.FromSeconds(3725), none));
        await recorder.RunAsync("SetVolume", () => connection.SetVolumeAsync(33, none));
        await recorder.RunAsync("ChangeVolume", () => connection.ChangeVolumeAsync(-5, none));
        await recorder.RunAsync("RampVolume", () => connection.RampVolumeAsync(20, none));
        await recorder.RunAsync("SetMute true", () => connection.SetMuteAsync(true, none));
        await recorder.RunAsync("SetMute false", () => connection.SetMuteAsync(false, none));
        await recorder.RunAsync("SetTransportUri plain", () => connection.SetTransportUriAsync("x-rincon-mp3radio://example.org/a?b=1&c=2", string.Empty, none));
        await recorder.RunAsync("SetTransportUri metadata", () => connection.SetTransportUriAsync("x-sonosapi-stream:s1?sid=254&flags=32", Metadata, none));
        await recorder.RunAsync("PlayFromQueue", () => connection.PlayFromQueueAsync("x-rincon-cpcontainer:1006206ccatalog%2fplaylists?sid=201&flags=8300", Metadata, none));
        await recorder.RunAsync("SwitchToTv", () => connection.SwitchToTvAsync(none));
        await recorder.RunAsync("SwitchToLineIn", () => connection.SwitchToLineInAsync(none));
        await recorder.RunAsync("SetPlayMode", () => connection.SetPlayModeAsync("SHUFFLE_NOREPEAT", none));
        await recorder.RunAsync("SetSleepTimer", () => connection.SetSleepTimerAsync(TimeSpan.FromMinutes(90), none));
        await recorder.RunAsync("SetSleepTimer off", () => connection.SetSleepTimerAsync(TimeSpan.Zero, none));
        await recorder.RunAsync("SetBass", () => connection.SetBassAsync(-3, none));
        await recorder.RunAsync("SetTreble", () => connection.SetTrebleAsync(4, none));
        await recorder.RunAsync("SetLoudness", () => connection.SetLoudnessAsync(true, none));
        await recorder.RunAsync("SetEqualizer on", () => connection.SetEqualizerAsync("NightMode", true, none));
        await recorder.RunAsync("SetEqualizer off", () => connection.SetEqualizerAsync("DialogLevel", false, none));
        await recorder.RunAsync("Join", () => connection.JoinAsync("RINCON_000000000001", none));
        await recorder.RunAsync("LeaveGroup", () => connection.LeaveGroupAsync(none));
        await recorder.RunAsync("SetGroupVolume", () => connection.SetGroupVolumeAsync(25, none));
        await recorder.RunAsync("ChangeGroupVolume", () => connection.ChangeGroupVolumeAsync(3, none));
        await recorder.RunAsync("SetGroupMute", () => connection.SetGroupMuteAsync(true, none));

        // Assert
        var expected = TestFixtures.Read("connection-requests.txt").ReplaceLineEndings("\n");
        var actual = recorder.Log.ToString().Replace(speaker.BaseUri.ToString(), "http://speaker/", StringComparison.Ordinal);
        Assert.Equal(expected, actual);
    }

    private sealed class RecordingHandler() : DelegatingHandler(new HttpClientHandler())
    {
        internal StringBuilder Log { get; } = new();

        internal async Task RunAsync(string name, Func<Task> call)
        {
            Log.Append("### ").Append(name).Append('\n');
            await call();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Log.Append(request.Method).Append(' ').Append(request.RequestUri).Append('\n');
            foreach (var header in request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()))
            {
                if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Append("  ").Append(header.Key).Append(": ").Append(string.Join(" | ", header.Value)).Append('\n');
                }
            }

            Log.Append("  ").Append(body).Append('\n');
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
