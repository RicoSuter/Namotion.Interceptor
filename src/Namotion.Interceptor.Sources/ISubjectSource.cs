namespace Namotion.Interceptor.Sources;

public interface ISubjectSource
{
    IInterceptorSubject Subject { get; }
    
    Task<IDisposable?> InitializeAsync(Action<SubjectUpdate> applySourceChangeAction, CancellationToken cancellationToken);

    Task<SubjectUpdate> ReadFromSourceAsync(CancellationToken cancellationToken);
    
    Task WriteToSourceAsync(SubjectUpdate update, CancellationToken cancellationToken);
}
