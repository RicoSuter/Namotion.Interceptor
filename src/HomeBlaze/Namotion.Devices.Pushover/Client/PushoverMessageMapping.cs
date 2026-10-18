using System.Globalization;
using HomeBlaze.Abstractions.Messaging;
using Microsoft.Extensions.Logging;

namespace Namotion.Devices.Pushover.Client;

internal static class PushoverMessageMapping
{
    internal const int MaximumMessageLength = 1024;
    internal const int MaximumTitleLength = 250;
    internal const int MaximumUrlLength = 512;
    internal const int MaximumUrlTitleLength = 100;

    private const int EmergencyPriority = 2;
    private const string EmergencyTagPrefix = "target-";

    /// <summary>
    /// The tag every emergency notification of the target with <paramref name="targetKey"/> is sent with: the key in
    /// lowercase after <c>target-</c>, with every character other than a letter, a digit or a dash replaced by a dash.
    /// </summary>
    public static string GetEmergencyTag(string targetKey) =>
        string.Create(EmergencyTagPrefix.Length + targetKey.Length, targetKey, static (tag, key) =>
        {
            EmergencyTagPrefix.CopyTo(tag);
            for (var index = 0; index < key.Length; index++)
            {
                var character = char.ToLowerInvariant(key[index]);
                tag[EmergencyTagPrefix.Length + index] = char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) ? character : '-';
            }
        });

    /// <summary>
    /// Creates the form fields of the messages endpoint, leaving out each value that is not set. An emergency also
    /// gets its retry, expire and the tag of the target with <paramref name="targetKey"/>.
    /// </summary>
    public static Dictionary<string, string> Create(
        string token,
        string userKey,
        Notification notification,
        TimeSpan emergencyRetry,
        TimeSpan emergencyExpire,
        string targetKey,
        ILogger logger)
    {
        var priority = ToPushoverPriority(notification.Priority);
        var fields = new Dictionary<string, string>
        {
            ["token"] = token,
            ["user"] = userKey,
            ["message"] = Shorten(notification.Message, MaximumMessageLength, "message", logger)!
        };

        AddIfSet(fields, "title", Shorten(notification.Title, MaximumTitleLength, "title", logger));
        var (url, urlTitle) = CreateUrl(notification, logger);
        AddIfSet(fields, "url", url);
        AddIfSet(fields, "url_title", urlTitle);
        if (priority != 0)
        {
            fields["priority"] = priority.ToString(CultureInfo.InvariantCulture);
        }

        if (priority == EmergencyPriority)
        {
            fields["retry"] = ((int)emergencyRetry.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            fields["expire"] = ((int)emergencyExpire.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            fields["tags"] = GetEmergencyTag(targetKey);
        }

        return fields;
    }

    private static void AddIfSet(Dictionary<string, string> fields, string name, string? value)
    {
        if (value is not null)
        {
            fields[name] = value;
        }
    }

    /// <summary>
    /// Throws an <see cref="ArgumentException"/> for a notification that cannot be sent: one without message text or
    /// with a priority that is no defined member.
    /// </summary>
    public static void Validate(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Message);
        _ = ToPushoverPriority(notification.Priority);
    }

    /// <summary>
    /// Maps <paramref name="priority"/> to the Pushover priority level; throws for a value that is no defined member,
    /// for example one cast from an integer.
    /// </summary>
    public static int ToPushoverPriority(NotificationPriority priority) => priority switch
    {
        NotificationPriority.Low => -1,
        NotificationPriority.Normal => 0,
        NotificationPriority.High => 1,
        NotificationPriority.Emergency => EmergencyPriority,
        _ => throw new ArgumentOutOfRangeException(nameof(priority), priority, "The notification priority is not a defined NotificationPriority member.")
    };

    internal static string? Shorten(string? value, int maximumLength, string field, ILogger logger)
    {
        if (value is null || value.Length <= maximumLength)
        {
            return value;
        }

        logger.LogWarning("The Pushover {Field} has {Length} characters and is shortened to {MaximumLength}.", field, value.Length, maximumLength);

        var length = maximumLength - 1;
        if (char.IsHighSurrogate(value[length - 1]))
        {
            // Cutting between the two halves of a surrogate pair would leave an invalid character.
            length--;
        }

        return string.Concat(value.AsSpan(0, length), "…");
    }

    // Shortening a url would break the link it points to, so a url over the limit is omitted instead, along with its
    // now-meaningless title; a url within the limit keeps its title shortened on its own.
    private static (string? Url, string? UrlTitle) CreateUrl(Notification notification, ILogger logger)
    {
        var url = notification.Url;
        if (url is { Length: > MaximumUrlLength })
        {
            logger.LogWarning(
                "The Pushover url has {Length} characters, longer than the {MaximumLength} limit, and is omitted instead of being shortened.",
                url.Length, MaximumUrlLength);
            return (null, null);
        }

        if (!string.IsNullOrEmpty(url) && !IsHttpUrl(url))
        {
            logger.LogWarning("The Pushover url is not an absolute http or https address and is omitted.");
            return (null, null);
        }

        return (url, Shorten(notification.UrlTitle, MaximumUrlTitleLength, "url title", logger));
    }

    private static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
