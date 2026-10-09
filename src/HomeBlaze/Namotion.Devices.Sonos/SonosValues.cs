using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security;
using Namotion.Devices.Sonos.Parsing;

namespace Namotion.Devices.Sonos;

/// <summary>
/// Conversions between Sonos UPnP wire values and subject values.
/// </summary>
internal static class SonosValues
{
    internal const string NotImplemented = "NOT_IMPLEMENTED";

    internal const int DevicePort = 1400;

    /// <summary>
    /// The longest sleep timer Sonos accepts.
    /// </summary>
    internal static readonly TimeSpan MaximumSleepTimer = new(23, 59, 59);

    /// <summary>
    /// A value Sonos actually reported. Null means the field was absent and <c>NOT_IMPLEMENTED</c> means the
    /// source cannot tell (Spotify Connect, TV), so both keep the current value rather than clearing it.
    /// </summary>
    internal static bool IsKnown([NotNullWhen(true)] string? value) =>
        value is not null && value != NotImplemented;

    /// <summary>
    /// The longest polling or retry interval, so a hand-edited value cannot overflow the loop's waits.
    /// </summary>
    internal static readonly TimeSpan MaximumInterval = TimeSpan.FromSeconds(SonosSystem.MaximumIntervalSeconds);

    /// <summary>
    /// Returns the configured interval clamped to <paramref name="minimum"/> through <see cref="MaximumInterval"/>,
    /// or <paramref name="fallback"/> when it is zero or negative.
    /// </summary>
    internal static TimeSpan GetEffectiveInterval(TimeSpan configured, TimeSpan fallback, TimeSpan minimum)
    {
        if (configured <= TimeSpan.Zero)
        {
            return fallback;
        }

        if (configured < minimum)
        {
            return minimum;
        }

        return configured > MaximumInterval ? MaximumInterval : configured;
    }

    internal static string? NullIfEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    /// <summary>
    /// Converts a Sonos percent, such as a volume or battery level, to a fraction from 0 to 1.
    /// </summary>
    internal static decimal ToFraction(int percent) =>
        Math.Clamp(percent, 0, 100) / 100m;

    /// <summary>
    /// Throws <see cref="ArgumentOutOfRangeException"/> unless the value is a fraction from <paramref name="minimum"/>
    /// to 1: 0 for a volume, -1 for a volume change.
    /// </summary>
    internal static void ThrowIfFractionOutOfRange(decimal value, decimal minimum, [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, minimum, parameterName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 1m, parameterName);
    }

    /// <summary>
    /// Converts a fraction, clamped to <paramref name="minimum"/> through 1, to a Sonos percent.
    /// </summary>
    internal static int ToSonosPercent(decimal value, decimal minimum) =>
        (int)Math.Round(Math.Clamp(value, minimum, 1m) * 100m, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Parses an <c>H:MM:SS</c> duration of at most 9999 hours, null for an absent, unknown or unparsable value.
    /// </summary>
    internal static TimeSpan? ParseDuration(string? value)
    {
        if (!IsKnown(value) || value.Length == 0)
        {
            return null;
        }

        // One range more than needed, so a fourth part is counted rather than folded into the third.
        var text = value.AsSpan();
        Span<Range> parts = stackalloc Range[4];
        if (text.Split(parts, ':') != 3 ||
            !TryParseDurationPart(text[parts[0]], 9999, out var hours) ||
            !TryParseDurationPart(text[parts[1]], 59, out var minutes) ||
            !TryParseSeconds(text[parts[2]], out var seconds))
        {
            return null;
        }

        return TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
    }

    private static bool TryParseDurationPart(ReadOnlySpan<char> text, int maximum, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value <= maximum;

    private static bool TryParseSeconds(ReadOnlySpan<char> text, out double seconds) =>
        double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out seconds) && seconds is >= 0 and < 60;

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

    private static readonly string[] RadioUriPrefixes =
        ["x-rincon-mp3radio:", "x-sonosapi-stream:", "x-sonosapi-radio:", "x-sonosapi-hls:", "aac:", "hls-radio:"];

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

        return IsRadioUri(uri) ? SonosSource.Radio : SonosSource.Other;
    }

    private static bool IsRadioUri(string uri)
    {
        foreach (var prefix in RadioUriPrefixes)
        {
            if (uri.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
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
                level = ToFraction(percent);
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
        StreamMetadataPrefix + SecurityElement.Escape(title) + StreamMetadataSuffix;

    private static readonly string StreamMetadataPrefix =
        $"<DIDL-Lite xmlns:dc=\"{SonosXml.DcNamespace.NamespaceName}\" xmlns:upnp=\"{SonosXml.UpnpNamespace.NamespaceName}\" " +
        $"xmlns:r=\"{SonosXml.RinconNamespace.NamespaceName}\" xmlns=\"{SonosXml.DidlNamespace.NamespaceName}\">" +
        "<item id=\"R:0/0/0\" parentID=\"R:0/0\" restricted=\"true\">" +
        "<dc:title>";

    private const string StreamMetadataSuffix =
        "</dc:title>" +
        "<upnp:class>object.item.audioItem.audioBroadcast</upnp:class>" +
        "</item></DIDL-Lite>";

    /// <summary>
    /// Whether an equalizer level is on. Any level above zero is: the Arc Ultra reports its speech enhancement level
    /// 1 to 4 as DialogLevel.
    /// </summary>
    internal static bool IsLevelOn(int level) => level != 0;
}
