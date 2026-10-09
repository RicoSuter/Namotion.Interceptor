using System.Xml.Linq;
using Namotion.Devices.Sonos.Client;
using Namotion.Devices.Sonos.Parsing;
using Namotion.Devices.Sonos.Tests.Testing;
using Sonos.Base.Services;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosConnectionTests
{
    private const string Uuid = TestFixtures.KitchenUuid;

    private static SonosConnection CreateConnection(FakeSonosSpeaker speaker, HttpClient httpClient) =>
        new(speaker.BaseUri, Uuid, httpClient, new SonosClientProvider(httpClient));

    private static string? GetArgument(SoapCall call, string name) =>
        XDocument.Parse(call.Body).Descendants().FirstOrDefault(element => element.Name.LocalName == name)?.Value;

    [Fact]
    public async Task WhenReadingPlayer_ThenSoapResponsesAreMapped()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        speaker.RespondAsIdlePlayer(Uuid, "Küche");
        using var httpClient = new HttpClient();
        using var connection = CreateConnection(speaker, httpClient);

        // Act
        var reading = await connection.ReadPlayerAsync(isHomeTheater: true, [], CancellationToken.None);

        // Assert
        Assert.Equal("PAUSED_PLAYBACK", reading.AvTransport.TransportState);
        Assert.Equal("NORMAL", reading.AvTransport.PlayMode);
        Assert.StartsWith("x-sonos-vli:", reading.AvTransport.MediaUri);
        Assert.Equal("NOT_IMPLEMENTED", reading.AvTransport.TrackMetaData);
        Assert.Equal(string.Empty, reading.AvTransport.MediaMetaData);
        Assert.Null(reading.Position);
        Assert.Null(reading.SleepTimerRemaining);
        Assert.Equal(44, reading.RenderingControl.Volume);
        Assert.False(reading.RenderingControl.Mute);
        Assert.True(reading.RenderingControl.Loudness);
        Assert.True(reading.RenderingControl.NightMode);
        Assert.False(reading.RenderingControl.SpeechEnhancement);
        Assert.Contains(speaker.Calls, call => call.Action == "GetEQ" && call.Body.Contains("<EQType>DialogLevel</EQType>"));
    }

    [Fact]
    public async Task WhenDialogLevelIsAboveOne_ThenSpeechEnhancementIsOn()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        speaker.RespondAsIdlePlayer(Uuid, "Küche");
        speaker.RespondToEqualizer("DialogLevel", "3");
        using var httpClient = new HttpClient();
        using var connection = CreateConnection(speaker, httpClient);

        // Act
        var reading = await connection.ReadPlayerAsync(isHomeTheater: true, [], CancellationToken.None);

        // Assert
        Assert.True(reading.RenderingControl.SpeechEnhancement);
    }

    [Fact]
    public async Task WhenFavoritesSpanSeveralPages_ThenEveryPageIsRead()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        speaker.RespondToBrowsePage(0, TestFixtures.Read("favorites.xml"), numberReturned: 4, totalMatches: 5);
        speaker.RespondToBrowsePage(4,
            "<DIDL-Lite xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:r=\"urn:schemas-rinconnetworks-com:metadata-1-0/\" xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\">" +
            "<item id=\"FV:2/5\" parentID=\"FV:2\" restricted=\"false\"><dc:title>Radio Extra</dc:title><res>x-rincon-mp3radio://radio.example/stream</res></item></DIDL-Lite>",
            numberReturned: 1, totalMatches: 5);
        using var httpClient = new HttpClient();
        using var connection = CreateConnection(speaker, httpClient);

        // Act
        var favorites = await connection.ReadFavoritesAsync(CancellationToken.None);

        // Assert
        Assert.Equal(["Radio FM1", "SRF 3", "Radio Extra"], favorites.Select(favorite => favorite.Title));
        Assert.Equal(["0", "4"], speaker.Calls.Where(call => call.Action == "Browse").Select(call => GetArgument(call, "StartingIndex")));
    }

    [Fact]
    public async Task WhenOneReadAnswersWithAFault_ThenOnlyItsValuesAreUnknownAndTheFaultIsReportedOnce()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        speaker.RespondAsIdlePlayer(Uuid, "Küche");
        speaker.RespondWithFault("GetRemainingSleepTimerDuration", 701);
        speaker.RespondWithFault("GetVolume", 701);
        using var httpClient = new HttpClient();
        using var connection = CreateConnection(speaker, httpClient);
        List<SonosReadFault> firstFaults = [];
        List<SonosReadFault> secondFaults = [];

        // Act
        var reading = await connection.ReadPlayerAsync(isHomeTheater: false, firstFaults, CancellationToken.None);
        await connection.ReadPlayerAsync(isHomeTheater: false, secondFaults, CancellationToken.None);

        // Assert
        Assert.False(reading.HasSleepTimer);
        Assert.True(reading.HasPosition);
        Assert.Null(reading.RenderingControl.Volume);
        Assert.False(reading.RenderingControl.Mute);
        Assert.Equal("PAUSED_PLAYBACK", reading.AvTransport.TransportState);
        Assert.Equal(["GetRemainingSleepTimerDuration", "GetVolume"], firstFaults.Select(fault => fault.Action));
        Assert.Empty(secondFaults);
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
        var reading = await connection.ReadPlayerAsync(isHomeTheater: false, [], CancellationToken.None);

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
        var zoneInfo = await connection.ReadZoneInfoAsync([], CancellationToken.None);

        // Assert
        Assert.Equal("Küche", Assert.Single(Assert.Single(topology.Groups).Players).RoomName);
        Assert.Equal(2, favorites.Count);
        Assert.Equal("Sonos Ray", description.ModelName);
        Assert.NotNull(zoneInfo);
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
        Assert.Equal("/MediaRenderer/RenderingControl/Control", call.Path);
        Assert.Equal("RenderingControl", call.Service);
        Assert.Equal("SetVolume", call.Action);
        Assert.Contains("<DesiredVolume>50</DesiredVolume>", call.Body);
        Assert.Contains("<Channel>Master</Channel>", call.Body);
    }

    [Fact]
    public async Task WhenSpeakerAnswersWithFault_ThenCommandThrowsServiceException()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        speaker.RespondWithFault("SetVolume", 402);
        using var httpClient = new HttpClient();
        using var connection = CreateConnection(speaker, httpClient);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<SonosServiceException>(() => connection.SetVolumeAsync(50, CancellationToken.None));
        Assert.Equal(402, exception.UpnpErrorCode);
    }

    [Fact]
    public async Task WhenSeeking_ThenSendsRelativeTimeTarget()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        using var httpClient = new HttpClient();
        using var connection = CreateConnection(speaker, httpClient);

        // Act
        await connection.SeekAsync(new TimeSpan(1, 2, 3), CancellationToken.None);

        // Assert
        var call = Assert.Single(speaker.Calls);
        Assert.Equal("/MediaRenderer/AVTransport/Control", call.Path);
        Assert.Equal("Seek", call.Action);
        Assert.Equal("REL_TIME", GetArgument(call, "Unit"));
        Assert.Equal("01:02:03", GetArgument(call, "Target"));
    }

    [Fact]
    public async Task WhenSettingSleepTimer_ThenSendsFormattedDuration()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        using var httpClient = new HttpClient();
        using var connection = CreateConnection(speaker, httpClient);

        // Act
        await connection.SetSleepTimerAsync(TimeSpan.FromMinutes(90), CancellationToken.None);

        // Assert
        var call = Assert.Single(speaker.Calls);
        Assert.Equal("ConfigureSleepTimer", call.Action);
        Assert.Equal("01:30:00", GetArgument(call, "NewSleepTimerDuration"));
    }

    [Fact]
    public async Task WhenSettingSleepTimerToZero_ThenSendsEmptyDurationToCancel()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        using var httpClient = new HttpClient();
        using var connection = CreateConnection(speaker, httpClient);

        // Act
        await connection.SetSleepTimerAsync(TimeSpan.Zero, CancellationToken.None);

        // Assert
        var call = Assert.Single(speaker.Calls);
        Assert.Equal("ConfigureSleepTimer", call.Action);
        Assert.Equal(string.Empty, GetArgument(call, "NewSleepTimerDuration"));
    }

    [Fact]
    public async Task WhenSeekingOrSettingSleepTimerToNegativeDuration_ThenThrowsWithoutCalling()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        using var httpClient = new HttpClient();
        using var connection = CreateConnection(speaker, httpClient);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => connection.SeekAsync(TimeSpan.FromSeconds(-1), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => connection.SetSleepTimerAsync(TimeSpan.FromSeconds(-1), CancellationToken.None));
        Assert.Empty(speaker.Calls);
    }

    [Fact]
    public async Task WhenSettingSleepTimerToADayOrLonger_ThenThrowsWithoutCalling()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        using var httpClient = new HttpClient();
        using var connection = CreateConnection(speaker, httpClient);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => connection.SetSleepTimerAsync(TimeSpan.FromHours(24), CancellationToken.None));
        Assert.Empty(speaker.Calls);
    }

    [Fact]
    public async Task WhenJoining_ThenSetsCoordinatorRinconUri()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        using var httpClient = new HttpClient();
        using var connection = CreateConnection(speaker, httpClient);

        // Act
        await connection.JoinAsync(TestFixtures.LivingRoomUuid, CancellationToken.None);

        // Assert
        var call = Assert.Single(speaker.Calls);
        Assert.Equal("AVTransport", call.Service);
        Assert.Equal("SetAVTransportURI", call.Action);
        Assert.Equal($"x-rincon:{TestFixtures.LivingRoomUuid}", GetArgument(call, "CurrentURI"));
    }

    [Theory]
    [InlineData("NightMode", true, "1")]
    [InlineData("DialogLevel", false, "0")]
    public async Task WhenSettingEqualizer_ThenSendsTypeAndValue(string type, bool enabled, string expectedValue)
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        using var httpClient = new HttpClient();
        using var connection = CreateConnection(speaker, httpClient);

        // Act
        await connection.SetEqualizerAsync(type, enabled, CancellationToken.None);

        // Assert
        var call = Assert.Single(speaker.Calls);
        Assert.Equal("RenderingControl", call.Service);
        Assert.Equal("SetEQ", call.Action);
        Assert.Equal(type, GetArgument(call, "EQType"));
        Assert.Equal(expectedValue, GetArgument(call, "DesiredValue"));
    }

    [Fact]
    public async Task WhenPlayingFromQueue_ThenReplacesQueueSwitchesToItAndPlays()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        using var httpClient = new HttpClient();
        using var connection = CreateConnection(speaker, httpClient);

        // Act
        await connection.PlayFromQueueAsync("x-rincon-cpcontainer:playlist", "<DIDL-Lite />", CancellationToken.None);

        // Assert
        var calls = speaker.Calls.ToArray();
        Assert.Equal(["RemoveAllTracksFromQueue", "AddURIToQueue", "SetAVTransportURI", "Play"], calls.Select(call => call.Action));
        Assert.All(calls, call => Assert.Equal("AVTransport", call.Service));
        Assert.Equal("x-rincon-cpcontainer:playlist", GetArgument(calls[1], "EnqueuedURI"));
        Assert.Equal("<DIDL-Lite />", GetArgument(calls[1], "EnqueuedURIMetaData"));
        Assert.Equal($"x-rincon-queue:{Uuid}#0", GetArgument(calls[2], "CurrentURI"));
    }

    [Fact]
    public async Task WhenMetadataContainsAmpersand_ThenSpeakerReceivesTheMetadataUnchanged()
    {
        // Arrange
        const string metadata = "<DIDL-Lite><dc:title>Rock &amp; Roll</dc:title></DIDL-Lite>";
        await using var speaker = new FakeSonosSpeaker();
        using var httpClient = new HttpClient();
        using var connection = CreateConnection(speaker, httpClient);

        // Act
        await connection.SetTransportUriAsync("x-rincon-mp3radio://stream.example.com/live.mp3", metadata, CancellationToken.None);
        await connection.PlayFromQueueAsync("x-rincon-cpcontainer:playlist", metadata, CancellationToken.None);

        // Assert
        var calls = speaker.Calls.ToArray();
        Assert.Equal(metadata, GetArgument(calls[0], "CurrentURIMetaData"));
        Assert.Equal(metadata, GetArgument(Assert.Single(calls, call => call.Action == "AddURIToQueue"), "EnqueuedURIMetaData"));
    }

    [Fact]
    public async Task WhenSettingGroupVolume_ThenSnapshotsBeforeSetting()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        using var httpClient = new HttpClient();
        using var connection = CreateConnection(speaker, httpClient);

        // Act
        await connection.SetGroupVolumeAsync(30, CancellationToken.None);

        // Assert
        var calls = speaker.Calls.ToArray();
        Assert.Equal(["SnapshotGroupVolume", "SetGroupVolume"], calls.Select(call => call.Action));
        Assert.All(calls, call => Assert.Equal("/MediaRenderer/GroupRenderingControl/Control", call.Path));
        Assert.Equal("30", GetArgument(calls[1], "DesiredVolume"));
    }

    [Fact]
    public async Task WhenChangingGroupVolume_ThenSnapshotsBeforeChanging()
    {
        // Arrange
        await using var speaker = new FakeSonosSpeaker();
        using var httpClient = new HttpClient();
        using var connection = CreateConnection(speaker, httpClient);

        // Act
        await connection.ChangeGroupVolumeAsync(-5, CancellationToken.None);

        // Assert
        var calls = speaker.Calls.ToArray();
        Assert.Equal(["SnapshotGroupVolume", "SetRelativeGroupVolume"], calls.Select(call => call.Action));
        Assert.Equal("-5", GetArgument(calls[1], "Adjustment"));
    }

    [Fact]
    public async Task WhenSpeakerIsUnreachable_ThenSetVolumeThrows()
    {
        // Arrange
        using var httpClient = new HttpClient();
        using var connection = new SonosConnection(
            new Uri($"http://127.0.0.1:{LoopbackHttpServer.GetFreePort()}/"), Uuid, httpClient, new SonosClientProvider(httpClient));

        // Act & Assert
        await Assert.ThrowsAsync<HttpRequestException>(() => connection.SetVolumeAsync(50, CancellationToken.None));
    }
}
