using System.Diagnostics;

namespace Namotion.Interceptor.Interceptors;

internal enum AttachmentPhase
{
    Stable,
    Attaching,
    Detaching
}

internal sealed class AttachmentRouteChangedException : InvalidOperationException;

/// <summary>
/// Reports that an intercepted property write and an attachment publication raced. The losing
/// operation can be retried after the competing operation completes; it has not reached its terminal.
/// </summary>
public sealed class LifecycleConflictException : InvalidOperationException
{
    private LifecycleConflictException(IInterceptorSubject subject, bool isTransientCapture)
        : base($"An intercepted property write conflicts with attachment publication on '{subject.GetType().FullName}'. Retry the operation.")
    {
        IsTransientCapture = isTransientCapture;
    }

    internal bool IsTransientCapture { get; }

    internal static LifecycleConflictException Retryable(IInterceptorSubject subject) => new(subject, false);

    internal static LifecycleConflictException TransientCapture(IInterceptorSubject subject) => new(subject, true);
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
                ReportCompletionFailure(exception);
            }
        }
        catch (Exception exception)
        {
            ReportCompletionFailure(exception);
        }
    }

    private static void ReportCompletionFailure(Exception exception)
    {
        try
        {
            Trace.TraceError($"Completing a structural write lease failed: {exception}");
        }
        catch
        {
            // Dispose is the no-throw fallback even when diagnostics are misconfigured.
        }
    }
}
