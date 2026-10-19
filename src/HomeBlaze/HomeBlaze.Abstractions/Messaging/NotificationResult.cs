namespace HomeBlaze.Abstractions.Messaging;

/// <summary>The outcome of sending a <see cref="Notification"/>.</summary>
public sealed record NotificationResult
{
    /// <summary>The channel's identifier of the delivered notification, or null when the channel has none.</summary>
    public string? Id { get; init; }
}
