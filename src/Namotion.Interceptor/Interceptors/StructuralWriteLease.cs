using System.Diagnostics;

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
        long attachmentRevision,
        ITopologyAdmissionCoordinator? coordinator = null)
    {
        _executor = executor;
        Context = context;
        AttachmentRevision = attachmentRevision;
        Coordinator = coordinator;
    }

    internal InterceptorSubjectContext? Context { get; }

    internal long AttachmentRevision { get; }

    internal ITopologyAdmissionCoordinator? Coordinator { get; }

    internal Exception? Complete(Exception? primaryException)
    {
        var executor = Interlocked.Exchange(ref _executor, null);
        if (executor is null)
        {
            return primaryException;
        }

        if (Coordinator is null)
        {
            executor.ReleaseStructuralWriteLease(this);
            return primaryException;
        }

        return Coordinator.CompleteStructuralWrite(executor, this, primaryException);
    }

    public void Dispose()
    {
        try
        {
            if (Complete(null) is { } exception)
            {
                Trace.TraceError($"Completing a structural write lease failed: {exception}");
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError($"Completing a structural write lease failed: {exception}");
        }
    }
}
