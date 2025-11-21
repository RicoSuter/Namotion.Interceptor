using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Represents a connector that can provide and synchronize data for an <see cref="IInterceptorSubject"/>.
/// </summary>
public interface ISubjectConnector
{
    /// <summary>
    /// Checks whether the specified property is included in the connector.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <returns>The result.</returns>
    bool IsPropertyIncluded(RegisteredSubjectProperty property);

    /// <summary>
    /// Initializes the connector and starts listening for changes.
    /// </summary>
    /// <param name="updateBuffer">The buffer to apply subject property updates to.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// A disposable that can be used to stop listening for changes. Returns <c>null</c> if there is no active listener or nothing needs to be disposed.
    /// </returns>
    Task<IDisposable?> StartListeningAsync(ConnectorUpdateBuffer updateBuffer, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the maximum number of property changes that can be applied in a single batch (0 = no limit).
    /// </summary>
    public int WriteBatchSize { get; }

    /// <summary>
    /// Applies a set of property changes to the connector with all-or-nothing (transactional) semantics.
    /// If any change fails, the entire batch should throw an exception and will be retried.
    /// This method is designed to be called sequentially (not concurrently).
    /// Concurrent calls are not supported and will result in undefined behavior.
    /// </summary>
    /// <param name="changes">The collection of subject property changes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    ValueTask WriteToSourceAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken);
}
