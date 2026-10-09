using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security;

namespace Namotion.Devices.Sonos;

/// <summary>
/// Conversions between Sonos UPnP wire values and subject values.
/// </summary>
internal static class SonosValues
{
    internal const string NotImplemented = "NOT_IMPLEMENTED";

    internal const int DevicePort = 1400;

    // How much earlier than the last applied poll a poll may start and still be a late completion, not a clock jump.
    private static readonly TimeSpan PollReorderWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A value Sonos actually reported. Null means the field was absent and <c>NOT_IMPLEMENTED</c> means the
    /// source cannot tell (Spotify Connect, TV), so both keep the current value rather than clearing it.
    /// </summary>
    internal static bool IsKnown([NotNullWhen(true)] string? value) =>
        value is not null && value != NotImplemented;

    /// <summary>
    /// Returns whether a poll started shortly before the last applied one, so applying it would roll newer state back.
    /// A start further back than the reorder window is a backward wall-clock jump and is accepted, so the gate recovers.
    /// </summary>
    internal static bool IsSupersededPoll(DateTimeOffset pollStartedAt, DateTimeOffset lastPollStartedAt) =>
        pollStartedAt < lastPollStartedAt && lastPollStartedAt - pollStartedAt < PollReorderWindow;

    internal static string? NullIfEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    internal static decimal ToVolume(int sonosVolume) =>
        Math.Clamp(sonosVolume, 0, 100) / 100m;

    internal static int ToSonosVolume(decimal volume) =>
        (int)Math.Round(Math.Clamp(volume, 0m, 1m) * 100m, MidpointRounding.AwayFromZero);

    internal static int ToSonosVolumeAdjustment(decimal delta) =>
        (int)Math.Round(Math.Clamp(delta, -1m, 1m) * 100m, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Parses an <c>H:MM:SS</c> duration of at most 9999 hours, null for an absent, unknown or unparsable value.
    /// </summary>
    internal static TimeSpan? ParseDuration(string? value)
    {
        if (!IsKnown(value) || value.Length == 0)
        {
            return null;
        }

        var parts = value.Split(':');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !double.TryParse(parts[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) ||
            hours > 9999 || minutes >= 60 || !double.IsFinite(seconds) || seconds is < 0 or >= 60)
        {
            return null;
        }

        return TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
    }

    internal static string FormatDuration(TimeSpan value) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}");

    internal static SonosTransportState ParseTransportState(string? value) => value switch
    {
        "PLAYING" => SonosTransportState.Playing,
        "PAUSED_PLAYBACK" => SonosTransportState.Paused,
        "STOPPED" => SonosTransportState.Stopped,
        "TRANSITIONING" => SonosTransportState.Transitioning,
        _ => SonosTransportState.Unknown
    };

    internal static (bool Shuffle, SonosRepeatMode Repeat)? ParsePlayMode(string? value) => value switch
    {
        "NORMAL" => (false, SonosRepeatMode.Off),
        "REPEAT_ALL" => (false, SonosRepeatMode.All),
        "REPEAT_ONE" => (false, SonosRepeatMode.One),
        "SHUFFLE_NOREPEAT" => (true, SonosRepeatMode.Off),
        "SHUFFLE" => (true, SonosRepeatMode.All),
        "SHUFFLE_REPEAT_ONE" => (true, SonosRepeatMode.One),
        _ => null
    };

    internal static string FormatPlayMode(bool shuffle, SonosRepeatMode repeat) => (shuffle, repeat) switch
    {
        (false, SonosRepeatMode.Off) => "NORMAL",
        (false, SonosRepeatMode.All) => "REPEAT_ALL",
        (false, SonosRepeatMode.One) => "REPEAT_ONE",
        (true, SonosRepeatMode.Off) => "SHUFFLE_NOREPEAT",
        (true, SonosRepeatMode.All) => "SHUFFLE",
        (true, SonosRepeatMode.One) => "SHUFFLE_REPEAT_ONE",
        _ => throw new ArgumentOutOfRangeException(nameof(repeat), repeat, "Unknown repeat mode.")
    };

    internal static SonosSource DetectSource(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return SonosSource.None;
        }

        if (uri.StartsWith("x-sonos-htastream:", StringComparison.Ordinal))
        {
            return SonosSource.Tv;
        }

        if (uri.StartsWith("x-rincon-stream:", StringComparison.Ordinal))
        {
            return SonosSource.LineIn;
        }

        if (uri.StartsWith("x-rincon-queue:", StringComparison.Ordinal))
        {
            return SonosSource.Queue;
        }

        if (uri.StartsWith("x-sonos-vli:", StringComparison.Ordinal))
        {
            if (uri.Contains(",spotify:", StringComparison.Ordinal))
            {
                return SonosSource.SpotifyConnect;
            }

            return uri.Contains(",airplay:", StringComparison.Ordinal) ? SonosSource.AirPlay : SonosSource.Other;
        }

        if (uri.StartsWith("x-rincon-mp3radio:", StringComparison.Ordinal) ||
            uri.StartsWith("x-sonosapi-stream:", StringComparison.Ordinal) ||
            uri.StartsWith("x-sonosapi-radio:", StringComparison.Ordinal) ||
            uri.StartsWith("x-sonosapi-hls:", StringComparison.Ordinal) ||
            uri.StartsWith("aac:", StringComparison.Ordinal) ||
            uri.StartsWith("hls-radio:", StringComparison.Ordinal) ||
            uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return SonosSource.Radio;
        }

        return SonosSource.Other;
    }

    /// <summary>
    /// Reads the battery from a topology <c>MoreInfo</c> value such as <c>BattPct:87,BattChg:CHARGING</c>.
    /// </summary>
    internal static (decimal? Level, bool? IsCharging) ParseBattery(string? moreInfo)
    {
        decimal? level = null;
        bool? isCharging = null;
        if (string.IsNullOrEmpty(moreInfo))
        {
            return (level, isCharging);
        }

        foreach (var entry in moreInfo.Split(','))
        {
            var separator = entry.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = entry[..separator];
            var value = entry[(separator + 1)..];
            if (key == "BattPct" && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var percent))
            {
                level = Math.Clamp(percent, 0, 100) / 100m;
            }
            else if (key == "BattChg")
            {
                isCharging = value == "CHARGING";
            }
        }

        return (level, isCharging);
    }

    /// <summary>
    /// Resolves the speaker-relative paths Sonos uses for album art (<c>/getaa?...</c>) against the speaker.
    /// </summary>
    internal static string? ToAbsoluteUri(string? uri, Uri? baseUri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return null;
        }

        return baseUri is not null && uri.StartsWith('/') ? new Uri(baseUri, uri).AbsoluteUri : uri;
    }

    /// <summary>
    /// Returns the URI with its scheme replaced by <c>x-rincon-mp3radio</c>.
    /// </summary>
    internal static string ToStreamUri(string uri) =>
        // Sonos renders a plain http(s) stream as radio, with its title and without a seek bar, only behind this scheme.
        "x-rincon-mp3radio" + uri[uri.IndexOf(':')..];

    internal static string CreateStreamMetadata(string title) =>
        "<DIDL-Lite xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\" " +
        "xmlns:r=\"urn:schemas-rinconnetworks-com:metadata-1-0/\" xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\">" +
        "<item id=\"R:0/0/0\" parentID=\"R:0/0\" restricted=\"true\">" +
        "<dc:title>" + SecurityElement.Escape(title) + "</dc:title>" +
        "<upnp:class>object.item.audioItem.audioBroadcast</upnp:class>" +
        "</item></DIDL-Lite>";
}
