using HomeBlaze.Abstractions.Messaging;
using Microsoft.Extensions.Logging;

namespace Namotion.Devices.Pushover.Client;

internal static class PushoverMessageMapping
{
    internal const int MaximumMessageLength = 1024;
    internal const int MaximumTitleLength = 250;
    internal const int MaximumUrlLength = 512;
    internal const int MaximumUrlTitleLength = 100;

    public static PushoverMessageRequest Create(
        string token, string userKey, Notification notification, TimeSpan emergencyRetry, TimeSpan emergencyExpire, ILogger logger)
    {
        var priority = ToPushoverPriority(notification.Priority);
        var isEmergency = notification.Priority == NotificationPriority.Emergency;
        return new PushoverMessageRequest
        {
            Token = token,
            User = userKey,
            Message = Shorten(notification.Message, MaximumMessageLength, "message", logger)!,
            Title = Shorten(notification.Title, MaximumTitleLength, "title", logger),
            Url = Shorten(notification.Url, MaximumUrlLength, "url", logger),
            UrlTitle = Shorten(notification.UrlTitle, MaximumUrlTitleLength, "url title", logger),
            Priority = priority == 0 ? null : priority,
            Retry = isEmergency ? (int)emergencyRetry.TotalSeconds : null,
            Expire = isEmergency ? (int)emergencyExpire.TotalSeconds : null
        };
    }

    public static int ToPushoverPriority(NotificationPriority priority) => priority switch
    {
        NotificationPriority.Low => -1,
        NotificationPriority.High => 1,
        NotificationPriority.Emergency => 2,
        _ => 0
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
}
