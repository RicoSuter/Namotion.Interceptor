using Namotion.Devices.Sonos.Parsing;

namespace Namotion.Devices.Sonos;

public partial class SonosPlayer
{
    // Every event and poll repeats the metadata, so the last parse is reused while the raw string is unchanged, and
    // the album art URI while the track and the speaker address are. Guarded by _stateLock.
    private string? _lastTrackMetaData;
    private DidlTrack? _lastTrack;
    private string? _lastMediaMetaData;
    private string? _lastMediaTitle;
    private DidlTrack? _imageUriTrack;
    private Uri? _imageUriBaseUri;
    private string? _imageUri;
    private DidlTrack? _titleTrack;
    private string? _titleTrackUri;
    private string? _titleMediaUri;
    private string? _title;

    // Caller holds _stateLock. Polls often report the media metadata empty that events delivered, so only a parsable
    // title replaces it. The next track of the same queue or station keeps it.
    private void ApplyMediaTitle(string? mediaMetaData, bool isMediaChange)
    {
        if (mediaMetaData != _lastMediaMetaData || isMediaChange)
        {
            var title = DidlParser.ParseTitle(mediaMetaData);
            // Only the URL itself is dropped. A title that equals the end of the URL may be the caller's.
            _lastMediaTitle = title is not null && SonosValues.IsStreamUri(title, MediaUri) ? null : title;
            _lastMediaMetaData = mediaMetaData;
        }

        if (_lastMediaTitle is { } mediaTitle)
        {
            ReportedMediaTitle = mediaTitle;
        }
        else if (isMediaChange)
        {
            ReportedMediaTitle = null;
        }
    }

    // Caller holds _stateLock.
    private void ApplyTrackMetaData(string? trackMetaData, bool isTrackChange, bool isPoll)
    {
        if (SonosValues.IsKnown(trackMetaData))
        {
            if (trackMetaData != _lastTrackMetaData)
            {
                _lastTrack = DidlParser.ParseTrack(trackMetaData);
                _lastTrackMetaData = trackMetaData;
            }

            ApplyTrack(_lastTrack, isTrackChange, isPoll);
        }
        else if (isTrackChange)
        {
            _lastTrack = null;
            _lastTrackMetaData = null;
            ApplyTrack(null, isTrackChange, isPoll);
        }
    }

    // Caller holds _stateLock.
    private void ApplyTrack(DidlTrack? track, bool isTrackChange, bool isPoll)
    {
        ApplyTrackTitle(track, isTrackChange);
        CurrentTrackArtist = track?.Artist;
        CurrentTrackAlbum = track?.Album;
        ApplyTrackImage(track, isTrackChange, isPoll);
    }

    // A placeholder title (connecting, buffering) keeps the current one while the track stays the same, like
    // NOT_IMPLEMENTED.
    private void ApplyTrackTitle(DidlTrack? track, bool isTrackChange)
    {
        var trackUri = CurrentTrackUri;
        var mediaUri = MediaUri;
        if (!ReferenceEquals(track, _titleTrack) || trackUri != _titleTrackUri || mediaUri != _titleMediaUri)
        {
            var title = track?.Title;
            _title = title is not null && SonosValues.IsTitleOfTrackUri(title, trackUri, mediaUri) ? null : title;
            _titleTrack = track;
            _titleTrackUri = trackUri;
            _titleMediaUri = mediaUri;
        }

        if (_title is null || SonosValues.IsKnown(_title))
        {
            CurrentTrackTitle = _title;
        }
        else if (isTrackChange)
        {
            CurrentTrackTitle = null;
        }
    }

    // A poll keeps the current art when it reports none for the same track, since polls omit the art that events
    // delivered, and so does an event with a placeholder title, which a rebuffering stream sends without art. Any
    // other event without art clears it: a stream keeps its track URI from song to song.
    private void ApplyTrackImage(DidlTrack? track, bool isTrackChange, bool isPoll)
    {
        var baseUri = BaseUri;
        if (!ReferenceEquals(track, _imageUriTrack) || baseUri != _imageUriBaseUri)
        {
            _imageUri = SonosValues.ToAbsoluteUri(track?.AlbumArtUri, baseUri);
            _imageUriTrack = track;
            _imageUriBaseUri = baseUri;
        }

        var keepsMissingArt = isPoll || (track?.Title is { } title && !SonosValues.IsKnown(title));
        if (_imageUri is not null || isTrackChange || !keepsMissingArt)
        {
            CurrentTrackImageUri = _imageUri;
        }
    }
}
