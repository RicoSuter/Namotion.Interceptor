namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Connector where the local subject is a CLIENT of an external authoritative system.
/// The external system is the source of truth. Writes may fail and need retry queuing.
/// Examples: OPC UA client, database client, REST API consumer, MQTT subscriber.
/// </summary>
public interface ISubjectClientConnector : ISubjectConnector
{
    /// <summary>
    /// Loads the initial state from the external authoritative system and returns a delegate that applies it to the associated subject.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// A delegate that applies the initial state to the subject. Returns <c>null</c> if there is no state to apply.
    /// </returns>
    Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken);
}
