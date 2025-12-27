namespace HomeBlaze.Abstractions.Messaging;

/// <summary>
/// Generic typed publisher for a specific message type.
/// </summary>
/// <typeparam name="TMessage">The type of message to publish.</typeparam>
public interface IMessagePublisher<in TMessage> where TMessage : IMessage
{
    /// <summary>
    /// Publishes a message of the specified type.
    /// </summary>
    void Publish(TMessage message);
}
