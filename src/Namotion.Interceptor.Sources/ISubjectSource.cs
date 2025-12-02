using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources;

/// <summary>
/// Represents a source that can provide and synchronize data for an <see cref="IInterceptorSubject"/>.
/// </summary>
public interface ISubjectSource
{
    /// <summary>
    /// Checks whether the specified property is included in the source.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <returns>The result.</returns>
    bool IsPropertyIncluded(RegisteredSubjectProperty property);

    /// <summary>
    /// Initializes the source and starts listening for external changes.
    /// </summary>
    /// <param name="propertyWriter">The writer to use for applying inbound property updates to the subject.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// A disposable that can be used to stop listening for changes. Returns <c>null</c> if there is no active listener or nothing needs to be disposed.
    /// </returns>
    Task<IDisposable?> StartListeningAsync(SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the maximum number of property changes that can be applied in a single batch (0 = no limit).
    /// </summary>
    public int WriteBatchSize { get; }

    /// <summary>
    /// Applies a set of property changes to the source with all-or-nothing (transactional) semantics.
    /// If any change fails, the entire batch should throw an exception and will be retried.
    /// This method is designed to be called sequentially (not concurrently).
    /// Concurrent calls are not supported and will result in undefined behavior.
    /// </summary>
    /// <param name="changes">The collection of subject property changes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    ValueTask WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken);

    /// <summary>
    /// Loads the initial state from the external authoritative system and returns a delegate that applies it to the associated subject.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// A delegate that applies the initial state to the subject. Returns <c>null</c> if there is no state to apply.
    /// </returns>
    Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken);
}
