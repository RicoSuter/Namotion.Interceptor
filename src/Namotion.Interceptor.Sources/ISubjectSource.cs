using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources;

public interface ISubjectSource
{
    IInterceptorSubject Subject { get; }
    
    Task<IDisposable?> InitializeAsync(ISubjectSourceDispatcher dispatcher, CancellationToken cancellationToken);

    /// <summary>
    /// Loads the complete state of the source and applies it to the subject in the returned callback.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The callback which applies the loaded state to the subject.</returns>
    Task<Action?> LoadCompleteSourceStateAsync(CancellationToken cancellationToken);
    
    Task WriteToSourceAsync(IEnumerable<SubjectPropertyChange> changes, CancellationToken cancellationToken);
}
