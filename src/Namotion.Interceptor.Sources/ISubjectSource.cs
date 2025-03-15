namespace Namotion.Interceptor.Sources;

public interface ISubjectSource
{
    IInterceptorSubject Subject { get; }
    
    Task<IDisposable?> InitializeAsync(Action<SubjectUpdate> updateAction, CancellationToken cancellationToken);

    Task<SubjectUpdate> ReadAsync(CancellationToken cancellationToken);
    
    Task WriteAsync(SubjectUpdate update, CancellationToken cancellationToken);
}
