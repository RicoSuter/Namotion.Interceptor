using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Sonos.Client;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosAudioClipClientTests
{
    private static readonly Uri SoundUri = new("http://host/a b.mp3?x=1&y=2");

    [Fact]
    public void WhenCreatingACommand_ThenItNamesThePlayerTheEscapedSoundAddressAndTheVolume()
    {
        // Act
        var command = SonosAudioClipClient.CreateCommand("RINCON_1", SoundUri, 30);

        // Assert
        using var document = JsonDocument.Parse(command);
        var header = document.RootElement[0];
        var clip = document.RootElement[1];
        Assert.Equal(2, document.RootElement.GetArrayLength());
        Assert.Equal("audioClip:1", header.GetProperty("namespace").GetString());
        Assert.Equal("loadAudioClip", header.GetProperty("command").GetString());
        Assert.Equal("RINCON_1", header.GetProperty("playerId").GetString());
        Assert.Equal("http://host/a%20b.mp3?x=1&y=2", clip.GetProperty("streamUrl").GetString());
        Assert.Equal(30, clip.GetProperty("volume").GetInt32());
        Assert.False(string.IsNullOrEmpty(clip.GetProperty("name").GetString()));
        Assert.False(string.IsNullOrEmpty(clip.GetProperty("appId").GetString()));
    }

    [Fact]
    public void WhenGettingTheEndpointOfAHost_ThenItIsTheSecureWebsocketOfTheSpeaker()
    {
        // Act
        var endpoint = SonosAudioClipClient.GetEndpoint("192.168.1.20");

        // Assert
        Assert.Equal("wss://192.168.1.20:1443/websocket/api", endpoint.AbsoluteUri);
    }

    [Fact]
    public async Task WhenQueuingAClip_ThenTheSpeakerWithItsOwnCertificateGetsTheKeyTheSubProtocolAndTheCommandAsOneBinaryMessage()
    {
        // Arrange
        await using var speaker = new FakeAudioClipSpeaker();

        // Act
        await SonosAudioClipClient.QueueAsync(
            speaker.Endpoint, "RINCON_1", SoundUri, 30, ConnectedSystem.WaitTimeout, NullLogger.Instance, CancellationToken.None);

        // Assert
        var received = await speaker.Received.WaitAsync(ConnectedSystem.WaitTimeout);
        Assert.Equal("GET /websocket/api HTTP/1.1", received.RequestLine);
        Assert.False(string.IsNullOrEmpty(received.ApiKey));
        Assert.Equal("v1.api.smartspeaker.audio", received.SubProtocol);
        Assert.Equal(WebSocketMessageType.Binary, received.MessageType);
        Assert.Equal(
            JsonSerializer.Serialize(JsonDocument.Parse(SonosAudioClipClient.CreateCommand("RINCON_1", SoundUri, 30)).RootElement),
            JsonSerializer.Serialize(JsonDocument.Parse(received.Message).RootElement));
    }

    [Fact]
    public async Task WhenTheSpeakerDoesNotAnswerTheClose_ThenQueuingReturnsAfterTheCloseBudgetWithTheClipSent()
    {
        // Arrange
        await using var speaker = new FakeAudioClipSpeaker(answersClose: false);

        // Act
        var exception = await Record.ExceptionAsync(() => SonosAudioClipClient.QueueAsync(
            speaker.Endpoint, "RINCON_1", SoundUri, 30, TimeSpan.FromMilliseconds(50), NullLogger.Instance, CancellationToken.None));

        // Assert
        Assert.Null(exception);
        Assert.Contains("loadAudioClip", (await speaker.Received.WaitAsync(ConnectedSystem.WaitTimeout)).Message);
    }
}
