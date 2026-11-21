using System.Text.Json.Serialization;

namespace Namotion.Devices.Tesla.Client.Dto;

internal sealed class TeslaMediaInfoDto
{
    [JsonPropertyName("audio_volume")] public decimal? AudioVolume { get; init; }
    [JsonPropertyName("audio_volume_increment")] public decimal? AudioVolumeIncrement { get; init; }
    [JsonPropertyName("audio_volume_max")] public decimal? AudioVolumeMax { get; init; }
    [JsonPropertyName("media_playback_status")] public string? MediaPlaybackStatus { get; init; }
    [JsonPropertyName("now_playing_album")] public string? NowPlayingAlbum { get; init; }
    [JsonPropertyName("now_playing_artist")] public string? NowPlayingArtist { get; init; }
    [JsonPropertyName("now_playing_duration")] public int? NowPlayingDuration { get; init; }
    [JsonPropertyName("now_playing_elapsed")] public int? NowPlayingElapsed { get; init; }
    [JsonPropertyName("now_playing_source")] public string? NowPlayingSource { get; init; }
    [JsonPropertyName("now_playing_station")] public string? NowPlayingStation { get; init; }
    [JsonPropertyName("now_playing_title")] public string? NowPlayingTitle { get; init; }
}
