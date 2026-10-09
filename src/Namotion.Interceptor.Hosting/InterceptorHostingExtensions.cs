using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Hosting;

/// <summary>Extension methods for attaching and detaching hosted services to and from interceptor subjects.</summary>
public static class InterceptorHostingExtensions
{
    private const string AttachmentsKey = "Namotion.Hosting.HostedServiceAttachments";
    private const string SubjectSlotKey = "Namotion.Hosting.SubjectSlot";

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
        => Attach(subject, factory, openGate: false, out _, out _);

    /// <summary>
    /// Attaches a hosted service factory and waits for the instance to start. Transactional: when the
    /// start faults, the attachment is removed before the exception propagates.
    /// </summary>
    /// <remarks>
    /// The token bounds the wait and not the start, which runs to completion. A wait cancelled before
    /// the start faulted leaves the attachment on the subject with its fault recorded.
    /// </remarks>
    public static async Task<IHostedServiceAttachment<T>> AttachHostedServiceAsync<T>(
        this IInterceptorSubject subject, Func<T> factory, CancellationToken cancellationToken)
        where T : class, IHostedService
    {
        var attachment = Attach(subject, factory, openGate: true, out var handler, out var start);
        if (handler is null)
        {
            // No handler means no context to bound the lifetime, so the factory is stored and nothing runs.
            return attachment;
        }

        if (start is not null)
        {
            // The token bounds this wait only; the transition runs to completion either way.
            await start.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        // The start's own outcome rather than Fault, for the reason on StartFault.
        if (attachment.Slot.StartFault is { } fault)
        {
            // Released rather than only untracked, unlike an explicit detach, and the stop is enqueued
            // rather than awaited: docs/design/hosting-service-ownership.md#a-faulted-awaited-attach-releases-instead.
            RemoveAttachment(subject, attachment);
            _ = MarkDetachedAndEnqueueStop(subject, attachment.Slot, handler, CancellationToken.None);
            attachment.Slot.ReleaseOwnership(handler);

            // Captured rather than rethrown: the fault was raised on the transition thread, and a plain
            // throw overwrites its stack trace with this one, which is the stack a user reads when a
            // failing subject aborts host startup.
            ExceptionDispatchInfo.Throw(fault);
        }

        return attachment;
    }

    /// <summary>
    /// Detaches a hosted service attachment. The instance is stopped, disposed and forgotten, and the
    /// factory is removed, so a later context attach starts nothing.
    /// </summary>
    public static bool DetachHostedService(this IInterceptorSubject subject, IHostedServiceAttachment attachment)
        => Detach(subject, attachment, openGate: false, CancellationToken.None, out _);

    /// <summary>
    /// Detaches a hosted service attachment and waits for the instance to stop and be disposed. The
    /// token is handed to the instance's own <c>StopAsync</c> as well as bounding the wait, so a
    /// cancelled token cuts the stop short; the instance is disposed either way.
    /// </summary>
    public static async Task<bool> DetachHostedServiceAsync(
        this IInterceptorSubject subject, IHostedServiceAttachment attachment, CancellationToken cancellationToken)
    {
        if (!Detach(subject, attachment, openGate: true, cancellationToken, out var stop))
        {
            return false;
        }

        if (stop is not null)
        {
            await stop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Adds the attachment and, when a handler is reachable, takes ownership and enqueues its start.
    /// <paramref name="start"/> is null when nothing was enqueued.
    /// </summary>
    private static HostedServiceAttachment<T> Attach<T>(
        IInterceptorSubject subject,
        Func<T> factory,
        bool openGate,
        out HostedServiceHandler? handler,
        out Task? start)
        where T : class, IHostedService
    {
        // Resolved before the add and again after it, for the reasons in
        // docs/design/hosting-service-ownership.md#an-attach-and-a-context-entry-are-the-same-two-facts-in-opposite-orders.
        // Nothing ahead of the add may read subject.Data: DataGatedSubject gates on the first read.
        handler = subject.Context.TryGetService<HostedServiceHandler>();
        var attachment = AddAttachment(subject, factory);
        handler ??= TryResolveHandlerAfterPublish(subject);

        if (handler is null)
        {
            start = null;
            return attachment;
        }

        if (openGate)
        {
            handler.OpenGate();
        }

        // Liveness before the take, because the take reads it: a subject that hosted nothing when it
        // entered the graph has no entry, and this is the moment it earns one.
        handler.MarkLiveIfAttached(subject);

        // The liveness read, the ownership take and the enqueue have to be one step, which a caller
        // cannot compose without reopening the window a concurrent context detach slips through.
        start = handler.TryTakeOwnershipAndStart(subject, attachment.Slot);
        return attachment;
    }

    /// <summary>
    /// Removes the attachment and enqueues the stop for its slot. <paramref name="stop"/> is null when
    /// no handler is reachable.
    /// </summary>
    private static bool Detach(
        IInterceptorSubject subject,
        IHostedServiceAttachment attachment,
        bool openGate,
        CancellationToken cancellationToken,
        out Task? stop)
    {
        // Resolved before the removal, for the reason on Attach. Here the throw would leave the instance
        // running with no stop enqueued and nothing left to reach it through.
        var handler = subject.Context.TryGetService<HostedServiceHandler>();

        if (!RemoveAttachment(subject, attachment))
        {
            stop = null;
            return false;
        }

        if (openGate)
        {
            handler?.OpenGate();
        }

        var slot = ((IHostedServiceSlotAccess)attachment).Slot;
        stop = MarkDetachedAndEnqueueStop(subject, slot, handler, cancellationToken);

        // Untracked without releasing, and before a caller awaits the stop so a cancelled wait cannot skip it:
        // docs/design/hosting-service-ownership.md#an-explicit-detach-untracks-the-slot-for-the-drain-without-releasing-ownership.
        handler?.UntrackForDrain(slot);
        return true;
    }

    /// <summary>
    /// Marks the slot of an attachment already removed from the subject detached and enqueues its
    /// stop. Returns the stop, or null without a handler.
    /// </summary>
    private static Task? MarkDetachedAndEnqueueStop(
        IInterceptorSubject subject, HostedServiceSlot slot, HostedServiceHandler? handler, CancellationToken cancellationToken)
    {
        // Marked before the stop is enqueued, and that order is the whole guard: an attach that has
        // published this attachment but not yet enqueued its start either reads the mark and enqueues
        // nothing, or enqueues ahead of the stop below, which then stops and disposes what it created.
        slot.MarkDetached();
        return handler?.EnqueueAttachmentStop(subject, slot, cancellationToken);
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
        // attachment inside it can register a slot that loses the swap and is never seen again.
        var attachment = new HostedServiceAttachment<T>(new HostedServiceSlot(factory, subject: null));

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
                // Reset on every run: the dictionary re-invokes this delegate when its swap loses, and
                // a rerun that no longer finds the attachment must not report the previous run's find,
                // or two concurrent detaches of one handle both return true.
                removed = false;

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

    /// <summary>Gets the subject's own slot, creating it on first use.</summary>
    /// <remarks>
    /// Extends <see cref="IHostedService"/> rather than taking one, so a subject slot cannot exist on
    /// a subject that is not one. A context detach relies on that, using the type test in place of a
    /// second data lookup.
    /// </remarks>
    internal static HostedServiceSlot GetOrAddSubjectSlot(this IHostedService hostedService)
    {
        var subject = (IInterceptorSubject)hostedService;

        // Read first: every re-attach comes through here, and building the slot ahead of the
        // GetOrAdd throws it away again on all of them.
        if (subject.Data.TryGetValue((null, SubjectSlotKey), out var existing) && existing is HostedServiceSlot found)
        {
            return found;
        }

        // The value overload: a factory closure is a display class allocated at the top of the method,
        // so the fast path above would allocate on every call.
        var slot = new HostedServiceSlot(factory: null, subject: hostedService);
        var stored = subject.Data.GetOrAdd((null, SubjectSlotKey), slot);
        return stored as HostedServiceSlot ?? slot;
    }

    internal static HostedServiceSlot? TryGetSubjectSlot(this IInterceptorSubject subject)
        => subject.Data.TryGetValue((null, SubjectSlotKey), out var value) ? value as HostedServiceSlot : null;
}
