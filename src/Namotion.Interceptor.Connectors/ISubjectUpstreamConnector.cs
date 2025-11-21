namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Represents a connector that can provide and synchronize data for an <see cref="IInterceptorSubject"/> and acts as a client of the data.
/// </summary>
public interface ISubjectUpstreamConnector : ISubjectConnector
{
    /// <summary>
    /// Loads the complete state of the connector and returns a delegate that applies the loaded state to the associated subject.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// A delegate that applies the loaded state to the subject. Returns <c>null</c> if there is no state to apply.
    /// </returns>
    Task<Action?> LoadCompleteSourceStateAsync(CancellationToken cancellationToken);
}
