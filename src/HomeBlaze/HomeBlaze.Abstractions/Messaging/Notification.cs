using System.ComponentModel;

namespace HomeBlaze.Abstractions.Messaging;

/// <summary>
/// A message for a <see cref="INotificationPublisher"/>. A channel that cannot show a member ignores it, and one with
/// a shorter limit than a member's length shortens it.
/// </summary>
public sealed record Notification
{
    /// <summary>
    /// The message text, which must not be empty.
    /// </summary>
    [Description("The message text, required and not empty.")]
    public required string Message { get; init; }

    /// <summary>
    /// A short heading, or null for the channel's default.
    /// </summary>
    [Description("A short heading, or null for the channel's default.")]
    public string? Title { get; init; }

    /// <summary>
    /// How urgently the notification must reach its recipient, see <see cref="NotificationPriority"/>.
    /// </summary>
    [Description(
        "Low: no sound or vibration. Normal (default): the recipient's default alert. High: alerts also during quiet hours. " +
        "Emergency: repeats until the recipient acknowledges it; use it only for safety-critical events. " +
        "A channel without repeats delivers Emergency as High.")]
    public NotificationPriority Priority { get; init; } = NotificationPriority.Normal;

    /// <summary>
    /// An absolute http or https address the recipient can open, for example the page of the subject that raised the
    /// notification. A channel leaves out any other value, together with <see cref="UrlTitle"/>.
    /// </summary>
    [Description("An absolute http or https address the recipient can open; any other value is left out.")]
    public string? Url { get; init; }

    /// <summary>
    /// The text shown for <see cref="Url"/>, or null to show the address itself.
    /// </summary>
    [Description("The text shown for the url, or null to show the address itself.")]
    public string? UrlTitle { get; init; }
}
