using Namotion.Interceptor.Registry;
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
    /// Initializes the source and starts listening for changes.
    /// </summary>
    /// <param name="dispatcher">The dispatcher to enqueue subject mutation actions.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// A disposable that can be used to stop listening for changes. Returns <c>null</c> if there is no active listener or nothing needs to be disposed.
    /// </returns>
    Task<IDisposable?> StartListeningAsync(ISubjectMutationDispatcher dispatcher, CancellationToken cancellationToken);

    /// <summary>
    /// Loads the complete state of the source and returns a delegate that applies the loaded state to the associated subject.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// A delegate that applies the loaded state to the subject. Returns <c>null</c> if there is no state to apply.
    /// </returns>
    Task<Action?> LoadCompleteSourceStateAsync(CancellationToken cancellationToken);
    
    /// <summary>
    /// Applies a set of property changes to the source.
    /// </summary>
    /// <param name="changes">The collection of subject property changes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    ValueTask WriteToSourceAsync(IReadOnlyCollection<SubjectPropertyChange> changes, CancellationToken cancellationToken);
    
    // TODO(perf): Use readonly span here for WriteToSourceAsync changes
}