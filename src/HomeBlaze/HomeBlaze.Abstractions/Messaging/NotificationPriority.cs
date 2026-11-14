namespace HomeBlaze.Abstractions.Messaging;

/// <summary>
/// How urgently a notification must reach its recipient. A channel maps each level to its closest own level.
/// </summary>
public enum NotificationPriority
{
    /// <summary>
    /// Delivered without sound or vibration.
    /// </summary>
    Low,

    /// <summary>
    /// Delivered with the recipient's default alert.
    /// </summary>
    Normal,

    /// <summary>
    /// Delivered with an alert also during the recipient's quiet hours.
    /// </summary>
    High,

    /// <summary>
    /// Repeated until the recipient acknowledges it, for safety-critical events only. A channel without repeats
    /// delivers it as <see cref="High"/>.
    /// </summary>
    Emergency
}
