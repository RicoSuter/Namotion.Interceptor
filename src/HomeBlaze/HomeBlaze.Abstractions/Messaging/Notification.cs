namespace HomeBlaze.Abstractions.Messaging;

/// <summary>
/// A message for a <see cref="INotificationPublisher"/>. A channel that cannot show a member ignores it, and one with
/// a shorter limit than a member's length shortens it.
/// </summary>
public sealed record Notification
{
    /// <summary>
    /// The message text.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// A short heading, or null for the channel's default.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// How urgently the notification must reach its recipient.
    /// </summary>
    public NotificationPriority Priority { get; init; } = NotificationPriority.Normal;

    /// <summary>
    /// An address the recipient can open, for example the page of the subject that raised the notification.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// The text shown for <see cref="Url"/>, or null to show the address itself.
    /// </summary>
    public string? UrlTitle { get; init; }
}
