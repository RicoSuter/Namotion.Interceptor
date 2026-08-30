namespace Namotion.Interceptor.Interceptors;

internal enum AttachmentPhase
{
    Stable,
    Attaching,
    Detaching
}

/// <summary>
/// Reports that a structural write and an attachment transition raced. The losing operation can be
/// retried after the competing operation completes; it has not reached its terminal.
/// </summary>
public sealed class LifecycleConflictException : InvalidOperationException
{
    private LifecycleConflictException(IInterceptorSubject subject)
        : base($"A structural write conflicts with an attachment transition on '{subject.GetType().FullName}'. Retry the operation.")
    {
    }

    internal static LifecycleConflictException Retryable(IInterceptorSubject subject) => new(subject);
}

internal sealed class StructuralWriteLease : IDisposable
{
    private InterceptorExecutor? _executor;

    internal StructuralWriteLease(
        InterceptorExecutor executor,
        InterceptorSubjectContext? context,
        long attachmentRevision)
    {
        _executor = executor;
        Context = context;
        AttachmentRevision = attachmentRevision;
    }

    internal InterceptorSubjectContext? Context { get; }

    internal long AttachmentRevision { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _executor, null)?.ReleaseStructuralWriteLease(this);
    }
}
