namespace HomeBlaze.Abstractions.Messaging;

/// <summary>
/// Marker interface for events - something that happened (notification, past tense).
/// </summary>
public interface IEvent : IMessage
{
    /// <summary>
    /// The timestamp when the event occurred.
    /// </summary>
    DateTimeOffset Timestamp { get; }
}
