using System.Collections.Immutable;
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
    /// subject enters the graph and disposes the instance when it leaves, so a re-attach yields a
    /// fresh instance. The factory must construct: returning an existing instance breaks the design,
    /// because a re-attach would start an instance the handler has already disposed.
    /// </summary>
    public static IHostedServiceAttachment<T> AttachHostedService<T>(
        this IInterceptorSubject subject, Func<T> factory)
        where T : class, IHostedService
    {
        var attachment = AddAttachment(subject, factory);

        // The handler decides whether it may take the target: the liveness read, the ownership take
        // and the append have to be one step, and a caller cannot compose them without reopening the
        // window a concurrent context detach slips through.
        subject.Context
            .TryGetService<HostedServiceHandler>()
            ?.TryTakeOwnershipAndStart(subject, attachment.Target);

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

        if (handler.TryTakeOwnershipAndStart(subject, attachment.Target) is { } start)
        {
            // The token bounds this wait only. The transition itself runs to completion, so a caller
            // that gives up waiting still ends with a started instance rather than a half started one.
            await start.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (attachment.Fault is { } fault)
        {
            RemoveAttachment(subject, attachment);
            throw fault;
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

        return removed;
    }

    internal static HostedServiceTarget GetOrAddSubjectTarget(this IInterceptorSubject subject, IHostedService hostedService)
    {
        var target = new HostedServiceTarget(factory: null, subject: hostedService);
        var stored = subject.Data.GetOrAdd((null, SubjectTargetKey), _ => target);
        return stored as HostedServiceTarget ?? target;
    }

    internal static HostedServiceTarget? TryGetSubjectTarget(this IInterceptorSubject subject)
        => subject.Data.TryGetValue((null, SubjectTargetKey), out var value) ? value as HostedServiceTarget : null;
}
