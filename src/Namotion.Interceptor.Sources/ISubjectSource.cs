using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources;

public interface ISubjectSource
{
    IInterceptorSubject Subject { get; }
    
    Task<IDisposable?> InitializeAsync(ISubjectSourceManager manager, CancellationToken cancellationToken);

    /// <summary>
    /// Loads the complete state of the source and applies it to the subject in the returned callback.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The callback which applies the loaded state to the subject.</returns>
    public Task<Action> LoadCompleteSourceStateAsync(CancellationToken cancellationToken) => Task.FromResult(() => { });
    
    Task WriteToSourceAsync(IEnumerable<PropertyChangedContext> updates, CancellationToken cancellationToken);
}
