namespace HomeBlaze.Abstractions.Messaging;

/// <summary>
/// Pub/sub message bus for publishing and subscribing to messages.
/// </summary>
public interface IMessageBus : IObservable<IMessage>
{
    /// <summary>
    /// Publishes a message to all subscribers.
    /// </summary>
    void Publish(IMessage message);
}
