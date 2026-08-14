using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Hosting;

/// <summary>
/// Extension methods for attaching and detaching hosted services to and from interceptor subjects.
/// </summary>
public static class InterceptorHostingExtensions
{
    private const string AttachmentsKey = "Namotion.Hosting.HostedServiceAttachments";
    private const string SubjectTargetKey = "Namotion.Hosting.SubjectTarget";

    /// <summary>
    /// Gets an immutable snapshot of the hosted service attachments on the subject.
    /// </summary>
    public static ImmutableArray<IHostedServiceAttachment> GetHostedServiceAttachments(this IInterceptorSubject subject)
    {
        // TryGetValue, not GetOrAdd: this runs on every context detach, under the lifecycle lock, and
        // GetOrAdd inserts a null entry into every subject's data bag just to read it.
        return subject.Data.TryGetValue((null, AttachmentsKey), out var value)
            && value is ImmutableArray<IHostedServiceAttachment> attachments
            ? attachments
            : [];
    }

    /// <summary>
    /// Attaches a hosted service factory to the subject. The handler invokes the factory when the
    /// subject enters the graph, stops the instance when it leaves, and disposes it as well when it is
    /// disposable, so a re-attach yields a fresh instance. The factory must construct: a factory that
    /// returns the instance it returned last time has that start refused and recorded as a fault on the
    /// attachment rather than run, whether or not that instance was disposable.
    /// </summary>
    public static IHostedServiceAttachment<T> AttachHostedService<T>(
        this IInterceptorSubject subject, Func<T> factory)
        where T : class, IHostedService
    {
        var attachment = AddAttachment(subject, factory);
        var handler = subject.Context.TryGetService<HostedServiceHandler>();

        // Liveness before the take, because the take reads it: the attach path records only subjects
        // that host something, so a subject that hosted nothing when it entered the graph has no entry
        // and this is the moment it earns one.
        handler?.MarkLiveIfAttached(subject);

        // The handler decides whether it may take the target: the liveness read, the ownership take
        // and the append have to be one step, and a caller cannot compose them without reopening the
        // window a concurrent context detach slips through.
        handler?.TryTakeOwnershipAndStart(subject, attachment.Target);

        return attachment;
    }

    /// <summary>
    /// Attaches a hosted service factory and waits for the instance to start. Transactional: when the
    /// start faults, the attachment is removed before the exception propagates.
    /// </summary>
    public static async Task<IHostedServiceAttachment<T>> AttachHostedServiceAsync<T>(
        this IInterceptorSubject subject, Func<T> factory, CancellationToken cancellationToken)
        where T : class, IHostedService
    {
        var attachment = AddAttachment(subject, factory);

        var handler = subject.Context.TryGetService<HostedServiceHandler>();
        if (handler is null)
        {
            // No handler means no context to bound the lifetime, so the factory is stored and nothing runs.
            return attachment;
        }

        handler.EnsureStarted();

        // Liveness before the take, for the reason on the synchronous overload.
        handler.MarkLiveIfAttached(subject);

        if (handler.TryTakeOwnershipAndStart(subject, attachment.Target) is { } start)
        {
            // The token bounds this wait only. The transition itself runs to completion, so a caller
            // that gives up waiting still ends with a started instance rather than a half started one.
            await start.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (attachment.Fault is { } fault)
        {
            RemoveAttachment(subject, attachment);

            // Captured rather than rethrown: the fault was raised on the transition thread, and a
            // plain throw overwrites its stack trace with this one, which is the stack a user reads
            // when a failing subject aborts host startup. Two callers can also reach the same
            // instance concurrently, and only one of them would keep a usable trace.
            ExceptionDispatchInfo.Capture(fault).Throw();
        }

        return attachment;
    }

    /// <summary>
    /// Detaches a hosted service attachment. The instance is stopped, disposed and forgotten, and the
    /// factory is removed, so a later context attach starts nothing.
    /// </summary>
    public static bool DetachHostedService(this IInterceptorSubject subject, IHostedServiceAttachment attachment)
    {
        if (!RemoveAttachment(subject, attachment))
        {
            return false;
        }

        var target = ((IHostedServiceAttachmentTarget)attachment).Target;

        // Marked before the stop is appended, and that order is the whole guard: an attach that has
        // published this attachment but not yet appended its start either reads the mark and appends
        // nothing, or appends ahead of the stop below, which then stops and disposes what it created.
        target.MarkDetached();

        var handler = subject.Context.TryGetService<HostedServiceHandler>();
        handler?.AppendStop(subject, target, signal: null, waitFor: null, CancellationToken.None);
        return true;
    }

    /// <summary>
    /// Detaches a hosted service attachment and waits for the instance to stop and be disposed.
    /// </summary>
    public static async Task<bool> DetachHostedServiceAsync(
        this IInterceptorSubject subject, IHostedServiceAttachment attachment, CancellationToken cancellationToken)
    {
        if (!RemoveAttachment(subject, attachment))
        {
            return false;
        }

        var target = ((IHostedServiceAttachmentTarget)attachment).Target;

        // Marked before the stop is appended, for the reason on the synchronous overload.
        target.MarkDetached();

        var handler = subject.Context.TryGetService<HostedServiceHandler>();
        if (handler is null)
        {
            return true;
        }

        handler.EnsureStarted();
        await handler
            .AppendStop(subject, target, signal: null, waitFor: null, cancellationToken)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    private static HostedServiceAttachment<T> AddAttachment<T>(IInterceptorSubject subject, Func<T> factory)
        where T : class, IHostedService
    {
        // Built outside the update delegate: ConcurrentDictionary may invoke that delegate more than
        // once and does not roll back its side effects, so constructing the record inside it could
        // register a target that loses the compare-and-swap and is never seen again.
        var attachment = new HostedServiceAttachment<T>(new HostedServiceTarget(factory, subject: null));

        subject.Data.AddOrUpdate((null, AttachmentsKey),
            _ => ImmutableArray.Create<IHostedServiceAttachment>(attachment),
            (_, value) => value is ImmutableArray<IHostedServiceAttachment> attachments
                ? attachments.Add(attachment)
                : ImmutableArray.Create<IHostedServiceAttachment>(attachment));

        return attachment;
    }

    private static bool RemoveAttachment(IInterceptorSubject subject, IHostedServiceAttachment attachment)
    {
        var removed = false;

        subject.Data.AddOrUpdate((null, AttachmentsKey),
            _ => null,
            (_, value) =>
            {
                if (value is not ImmutableArray<IHostedServiceAttachment> attachments || !attachments.Contains(attachment))
                {
                    return value;
                }

                removed = true;
                var updated = attachments.Remove(attachment);
                return updated.Length > 0 ? updated : null;
            });

        // Deliberately does not touch liveness. A start already appended re-reads liveness in its body,
        // so clearing it here retroactively cancels a start that was ordered ahead of this detach,
        // which is the ordering HostedServiceHandlerRaceTests pins. A subject that loses its last
        // attachment keeps its entry until it gains another one, and MarkLiveIfAttached is where a
        // stale entry is caught.
        return removed;
    }

    internal static HostedServiceTarget GetOrAddSubjectTarget(this IInterceptorSubject subject, IHostedService hostedService)
    {
        // Read first: every re-attach of a hosted subject goes through here, and constructing the
        // target and its chain lock ahead of the GetOrAdd throws both away again on all of them.
        if (subject.Data.TryGetValue((null, SubjectTargetKey), out var existing) && existing is HostedServiceTarget found)
        {
            return found;
        }

        // The value overload, not the factory one: a factory closing over the target is a display
        // class the compiler allocates at the top of this method, so the fast path above would still
        // allocate on every call. The target is already built here, so there is nothing to defer.
        var target = new HostedServiceTarget(factory: null, subject: hostedService);
        var stored = subject.Data.GetOrAdd((null, SubjectTargetKey), target);
        return stored as HostedServiceTarget ?? target;
    }

    internal static HostedServiceTarget? TryGetSubjectTarget(this IInterceptorSubject subject)
        => subject.Data.TryGetValue((null, SubjectTargetKey), out var value) ? value as HostedServiceTarget : null;
}
