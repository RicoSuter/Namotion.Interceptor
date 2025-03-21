using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources;

public interface ISubjectSource
{
    IInterceptorSubject Subject { get; }
    
    Task<IDisposable?> InitializeAsync(ISubjectSourceManager manager, CancellationToken cancellationToken);

    public Task<Action> LoadFullSourceStateAsync(CancellationToken cancellationToken) => Task.FromResult(() => { });
    
    Task WriteToSourceAsync(IEnumerable<PropertyChangedContext> updates, CancellationToken cancellationToken);
}
