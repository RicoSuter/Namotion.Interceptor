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
    /// <param name="updateBuffer">The buffer to apply subject property updates to.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// A disposable that can be used to stop listening for changes. Returns <c>null</c> if there is no active listener or nothing needs to be disposed.
    /// </returns>
    Task<IDisposable?> StartListeningAsync(SourceUpdateBuffer updateBuffer, CancellationToken cancellationToken);

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
    /// IMPORTANT: This method is designed to be called sequentially (not concurrently) by the SubjectSourceBackgroundService.
    /// Concurrent calls are not supported and will result in undefined behavior.
    /// </summary>
    /// <param name="changes">The collection of subject property changes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// Returns <see cref="SourceWriteResult.Success"/> if all writes succeeded.
    /// Returns a result with failed changes for transient errors that should be retried.
    /// Throws an exception for complete failures (e.g., network disconnect) to retry all changes.
    /// </returns>
    ValueTask<SourceWriteResult> WriteToSourceAsync(IReadOnlyList<SubjectPropertyChange> changes, CancellationToken cancellationToken);
}