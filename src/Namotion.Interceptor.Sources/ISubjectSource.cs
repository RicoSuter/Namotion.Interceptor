using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources;

public interface ISubjectSource
{
    IInterceptorSubject Subject { get; }
    
    Task<IDisposable?> InitializeAsync(ISubjectSourceManager manager, CancellationToken cancellationToken);

    Task<SubjectUpdate> ReadFromSourceAsync(CancellationToken cancellationToken);
    
    Task WriteToSourceAsync(IEnumerable<PropertyChangedContext> updates, CancellationToken cancellationToken);
}
