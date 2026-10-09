using Namotion.Devices.Sonos.Client;
using Namotion.Devices.Sonos.Tests.Testing;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosConnectionTests
{
    private const string Uuid = TestFixtures.KitchenUuid;

    private static SonosConnection CreateConnection(FakeSonosSpeaker speaker, HttpClient httpClient) =>
        new(speaker.BaseUri, Uuid, httpClient, new SonosClientProvider(httpClient));

    [Fact]
    public async Task WhenReadingPlayer_ThenSoapResponsesAreMapped()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        speaker.RespondAsIdlePlayer(Uuid, "Küche");
        using var httpClient = new HttpClient();
        using var connection = CreateConnection(speaker, httpClient);

        // Act
        var reading = await connection.ReadPlayerAsync(isHomeTheater: true, CancellationToken.None);

        // Assert
        Assert.Equal("PAUSED_PLAYBACK", reading.AvTransport.TransportState);
        Assert.Equal("NORMAL", reading.AvTransport.PlayMode);
        Assert.StartsWith("x-sonos-vli:", reading.AvTransport.MediaUri);
        Assert.Equal("NOT_IMPLEMENTED", reading.AvTransport.TrackMetaData);
        Assert.Null(reading.Position);
        Assert.Null(reading.SleepTimerRemaining);
        Assert.Equal(44, reading.RenderingControl.Volume);
        Assert.False(reading.RenderingControl.Mute);
        Assert.True(reading.RenderingControl.Loudness);
        Assert.True(reading.RenderingControl.NightMode);
        Assert.Contains(speaker.Calls, call => call.Action == "GetEQ" && call.Body.Contains("<EQType>DialogLevel</EQType>"));
    }

    [Fact]
    public async Task WhenReadingPlayerWithoutHomeTheater_ThenEqualizerIsNotQueried()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        speaker.RespondAsIdlePlayer(Uuid, "Küche");
        using var httpClient = new HttpClient();
        using var connection = CreateConnection(speaker, httpClient);

        // Act
        var reading = await connection.ReadPlayerAsync(isHomeTheater: false, CancellationToken.None);

        // Assert
        Assert.Null(reading.RenderingControl.NightMode);
        Assert.DoesNotContain(speaker.Calls, call => call.Action == "GetEQ");
    }

    [Fact]
    public async Task WhenReadingTopologyFavoritesAndDescription_ThenTheyAreParsed()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        speaker.RespondAsIdlePlayer(Uuid, "Küche");
        using var httpClient = new HttpClient();
        using var connection = CreateConnection(speaker, httpClient);

        // Act
        var topology = await connection.ReadTopologyAsync(CancellationToken.None);
        var favorites = await connection.ReadFavoritesAsync(CancellationToken.None);
        var description = await connection.ReadDescriptionAsync(CancellationToken.None);
        var zoneInfo = await connection.ReadZoneInfoAsync(CancellationToken.None);

        // Assert
        Assert.Equal("Küche", Assert.Single(Assert.Single(topology.Groups).Players).RoomName);
        Assert.Equal(2, favorites.Count);
        Assert.Equal("Sonos Ray", description.ModelName);
        Assert.Equal("18.8", zoneInfo.DisplayVersion);
        Assert.Equal("00:00:00:00:00:06", zoneInfo.MacAddress);
    }

    [Fact]
    public async Task WhenSettingVolume_ThenSendsDesiredVolumeOnMasterChannel()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        using var httpClient = new HttpClient();
        using var connection = CreateConnection(speaker, httpClient);

        // Act
        await connection.SetVolumeAsync(50, CancellationToken.None);

        // Assert
        var call = Assert.Single(speaker.Calls);
        Assert.Equal("RenderingControl", call.Service);
        Assert.Equal("SetVolume", call.Action);
        Assert.Contains("<DesiredVolume>50</DesiredVolume>", call.Body);
        Assert.Contains("<Channel>Master</Channel>", call.Body);
    }

    [Fact]
    public async Task WhenSetVolumeIsRejected_ThenThrows()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        using var httpClient = new HttpClient();
        using var connection = new SonosConnection(
            new Uri($"http://127.0.0.1:{LoopbackHttpServer.GetFreePort()}/"), Uuid, httpClient, new SonosClientProvider(httpClient));

        // Act & Assert
        await Assert.ThrowsAsync<HttpRequestException>(() => connection.SetVolumeAsync(50, CancellationToken.None));
    }
}
