using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Hosting;

/// <summary>Extension methods for attaching and detaching hosted services to and from interceptor subjects.</summary>
public static class InterceptorHostingExtensions
{
    private const string AttachmentsKey = "Namotion.Hosting.HostedServiceAttachments";
    private const string SubjectTargetKey = "Namotion.Hosting.SubjectTarget";

    /// <summary>Gets an immutable snapshot of the hosted service attachments on the subject.</summary>
    public static ImmutableArray<IHostedServiceAttachment> GetHostedServiceAttachments(this IInterceptorSubject subject)
    {
        subject.TryGetHostedServiceAttachments(out var attachments);
        return attachments;
    }

    /// <summary>
    /// Reads the attachments and reports whether the subject has ever carried one, which stays true
    /// after the last attachment has been detached.
    /// </summary>
    /// <remarks>
    /// Lets a context detach skip the liveness clear for almost every subject without skipping it for
    /// one that could still hold an entry. <see cref="RemoveAttachment"/> stores null rather than
    /// removing the key, so the key outlives the attachments. TryGetValue, not GetOrAdd, because this
    /// runs for every subject on every detach and GetOrAdd would insert into every data bag to read it.
    /// </remarks>
    internal static bool TryGetHostedServiceAttachments(
        this IInterceptorSubject subject, out ImmutableArray<IHostedServiceAttachment> attachments)
    {
        if (!subject.Data.TryGetValue((null, AttachmentsKey), out var value))
        {
            attachments = [];
            return false;
        }

        attachments = value is ImmutableArray<IHostedServiceAttachment> stored ? stored : [];
        return true;
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
        // Resolved before the add, because the lookup throws when the subject is reachable from two
        // hosting contexts and that throw after the add leaves the caller holding no attachment and the
        // subject holding a factory the next context attach starts. Resolved again when the first found
        // none, because a context published between the two is otherwise missed by both sides. Nothing
        // ahead of the add may read subject.Data: DataGatedSubject gates on the first read. See
        // docs/design/hosting-service-ownership.md#an-attach-and-a-context-entry-are-the-same-two-facts-in-opposite-orders.
        var handler = subject.Context.TryGetService<HostedServiceHandler>();
        var attachment = AddAttachment(subject, factory);
        handler ??= TryResolveHandlerAfterPublish(subject);

        // Liveness before the take, because the take reads it: a subject that hosted nothing when it
        // entered the graph has no entry, and this is the moment it earns one.
        handler?.MarkLiveIfAttached(subject);

        // The liveness read, the ownership take and the append have to be one step, which a caller
        // cannot compose without reopening the window a concurrent context detach slips through.
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
        // Resolved before the attachment is published, for the reason on the synchronous overload.
        var handler = subject.Context.TryGetService<HostedServiceHandler>();

        var attachment = AddAttachment(subject, factory);

        // Resolved again for the reason on the synchronous overload.
        handler ??= TryResolveHandlerAfterPublish(subject);

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
            // The token bounds this wait only; the transition runs to completion either way.
            await start.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (attachment.Fault is { } fault)
        {
            RemoveAttachment(subject, attachment);

            // The removal above puts this target out of reach, so nothing retires before shutdown an
            // ownership left installed here and a host that retries failed attaches leaks a subject per failure.
            // Marked first, so a context attach that snapshotted this attachment before the removal
            // cannot take the target in the gap.
            attachment.Target.MarkDetached();

            // The faulted start left no instance, but one queued behind it can, and the same completion
            // releases that body and this caller. Appended rather than awaited: the chain orders it
            // behind that start, while awaiting it would wedge on a startup scope this caller cannot
            // close.
            _ = handler.AppendStop(subject, attachment.Target, signal: null, waitFor: null, CancellationToken.None);

            // Released rather than only retired, unlike an explicit detach: the removal above is what
            // puts this target out of reach, so an ownership left installed is retired by nothing
            // before the drain. Safe to release ahead of the stop, which reads Current and never Owner.
            attachment.Target.ReleaseOwnership(handler);

            // Captured rather than rethrown: the fault was raised on the transition thread, and a plain
            // throw overwrites its stack trace with this one, which is the stack a user reads when a
            // failing subject aborts host startup.
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
        // Resolved before the removal, for the reason on AttachHostedService. Here the throw would
        // leave the instance running with no stop appended and nothing left to reach it through.
        var handler = subject.Context.TryGetService<HostedServiceHandler>();

        if (!RemoveAttachment(subject, attachment))
        {
            return false;
        }

        var target = ((IHostedServiceAttachmentTarget)attachment).Target;

        // Marked before the stop is appended, and that order is the whole guard: an attach that has
        // published this attachment but not yet appended its start either reads the mark and appends
        // nothing, or appends ahead of the stop below, which then stops and disposes what it created.
        target.MarkDetached();

        handler?.AppendStop(subject, target, signal: null, waitFor: null, CancellationToken.None);

        // Retired without releasing ownership, and that asymmetry is load bearing: a release here makes
        // a start queued ahead of this detach read Owner as null and refuse, which leaves the stop above
        // no instance to dispose. Without the retirement every attach and detach cycle keeps the target
        // and its subject on the handler for the handler's whole life.
        handler?.ForgetOwnership(target);
        return true;
    }

    /// <summary>Detaches a hosted service attachment and waits for the instance to stop and be disposed.</summary>
    public static async Task<bool> DetachHostedServiceAsync(
        this IInterceptorSubject subject, IHostedServiceAttachment attachment, CancellationToken cancellationToken)
    {
        // Resolved before the attachment is removed, for the reason on the synchronous overload.
        var handler = subject.Context.TryGetService<HostedServiceHandler>();

        if (!RemoveAttachment(subject, attachment))
        {
            return false;
        }

        var target = ((IHostedServiceAttachmentTarget)attachment).Target;

        // Marked before the stop is appended, for the reason on the synchronous overload.
        target.MarkDetached();

        if (handler is null)
        {
            return true;
        }

        handler.EnsureStarted();
        var stop = handler.AppendStop(subject, target, signal: null, waitFor: null, cancellationToken);

        // Retired without releasing ownership, and before the await so a cancelled wait cannot skip it.
        // The reason for the asymmetry is on the synchronous overload.
        handler.ForgetOwnership(target);

        await stop.WaitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Resolves the handler once more for a first lookup that found none, after the add has published.
    /// </summary>
    /// <remarks>
    /// Counts the reachable handlers instead of going through TryGetService, whose throw on two of them
    /// would land after the subject has already been mutated. Two reachable handlers is therefore the
    /// one shape this declines to act on, and every other path on this subject throws on it anyway.
    /// </remarks>
    private static HostedServiceHandler? TryResolveHandlerAfterPublish(IInterceptorSubject subject)
    {
        // The add is a release store, and release does not order it against this later load, so without
        // the fence the two sides miss each other however they are ordered in time. The publishing side
        // needs none: InterceptorSubjectContext.PublishState publishes with an Interlocked.Exchange.
        Interlocked.MemoryBarrier();

        var handlers = subject.Context.GetServices<HostedServiceHandler>();
        return handlers.Length == 1 ? handlers[0] : null;
    }

    private static HostedServiceAttachment<T> AddAttachment<T>(IInterceptorSubject subject, Func<T> factory)
        where T : class, IHostedService
    {
        // Outside the update delegate, which may run more than once with no rollback: building the
        // record inside it can register a target that loses the swap and is never seen again.
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

        // AddOrUpdate's add factory runs when the key is absent, so detaching an attachment this
        // subject never had would insert it and mark the subject "has ever hosted" for life, costing it
        // the detach fast path. Nothing removes the key, so it cannot vanish between the two calls.
        if (!subject.Data.ContainsKey((null, AttachmentsKey)))
        {
            return false;
        }

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

        // Deliberately leaves liveness alone: clearing it here would retroactively cancel a start
        // ordered ahead of this detach. The context detach ends the entry instead.
        return removed;
    }

    /// <summary>Gets the subject's own target, creating it on first use.</summary>
    /// <remarks>
    /// Extends <see cref="IHostedService"/> rather than taking one, so a subject target cannot exist on
    /// a subject that is not one. A context detach relies on that, using the type test in place of a
    /// second data lookup.
    /// </remarks>
    internal static HostedServiceTarget GetOrAddSubjectTarget(this IHostedService hostedService)
    {
        var subject = (IInterceptorSubject)hostedService;

        // Read first: every re-attach comes through here, and building the target ahead of the
        // GetOrAdd throws it away again on all of them.
        if (subject.Data.TryGetValue((null, SubjectTargetKey), out var existing) && existing is HostedServiceTarget found)
        {
            return found;
        }

        // The value overload: a factory closure is a display class allocated at the top of the method,
        // so the fast path above would allocate on every call.
        var target = new HostedServiceTarget(factory: null, subject: hostedService);
        var stored = subject.Data.GetOrAdd((null, SubjectTargetKey), target);
        return stored as HostedServiceTarget ?? target;
    }

    internal static HostedServiceTarget? TryGetSubjectTarget(this IInterceptorSubject subject)
        => subject.Data.TryGetValue((null, SubjectTargetKey), out var value) ? value as HostedServiceTarget : null;
}
