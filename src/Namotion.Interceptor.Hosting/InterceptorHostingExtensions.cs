using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Hosting;

/// <summary>Extension methods for attaching and detaching hosted services to and from interceptor subjects.</summary>
public static class InterceptorHostingExtensions
{
    private const string AttachmentsKey = "Namotion.Hosting.HostedServiceAttachments";
    private const string SubjectSlotKey = "Namotion.Hosting.SubjectSlot";
    private const string ActivationKey = "Namotion.Hosting.Activation";

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
            ReleaseFaultedAttachmentAndThrow(subject, attachment, handler, fault);
        }

        return attachment;
    }

    /// <summary>
    /// Removes an attachment whose awaited start faulted, enqueues its stop and releases the ownership
    /// the attach took, then throws the fault.
    /// </summary>
    [DoesNotReturn]
    private static void ReleaseFaultedAttachmentAndThrow(
        IInterceptorSubject subject, IHostedServiceAttachment attachment, HostedServiceHandler handler, Exception fault)
    {
        // Released rather than only retired, unlike an explicit detach, and the stop is enqueued
        // rather than awaited: docs/design/hosting-service-ownership.md#a-faulted-awaited-attach-releases-instead.
        var slot = ((IHostedServiceSlotAccess)attachment).Slot;
        RemoveAttachment(subject, attachment);
        _ = MarkDetachedAndEnqueueStop(subject, slot, handler, CancellationToken.None);
        ClearActivationEntry(subject, attachment);
        slot.ReleaseOwnership(handler);

        // Captured rather than rethrown: the fault was raised on the transition thread, and a plain
        // throw overwrites its stack trace with this one, which is the stack a user reads when a
        // failing subject aborts host startup.
        ExceptionDispatchInfo.Throw(fault);
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
    /// Activates the subject's own hosted service: attaches
    /// <see cref="ISubjectHostedServiceFactory.CreateHostedService"/> as a factory attachment, which the
    /// handler then runs while the subject is in a hosting graph, exactly as an attachment made with
    /// <see cref="AttachHostedService{T}"/>. Idempotent across explicit and automatic activation: every
    /// call returns the one activation attachment on the subject until that attachment is detached
    /// explicitly, after which the next call attaches a fresh one. On the idempotent path
    /// <paramref name="serviceProvider"/> is ignored and the existing activation's provider is kept,
    /// which for an automatic activation is the provider of the handler starting the service.
    /// </summary>
    /// <remarks>
    /// Must not be called from a hosted service's stop or dispose path, because it attaches a hosted
    /// service: docs/hosting.md#keep-the-dispose-path-out-of-the-lifecycle-lock.
    /// </remarks>
    /// <returns>
    /// The activation's attachment, or null when the subject is not an <see cref="ISubjectHostedServiceFactory"/>.
    /// </returns>
    public static IHostedServiceAttachment? ActivateHostedService(
        this IInterceptorSubject subject, IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        if (subject is not ISubjectHostedServiceFactory factory)
        {
            return null;
        }

        // Resolved before the entry is written, for the reason on Attach: the reservation is a
        // mutation the next context attach acts on.
        var handler = subject.Context.TryGetService<HostedServiceHandler>();
        var activation = subject.GetOrAddActivation(factory, serviceProvider, out var published);
        if (published)
        {
            StartPublishedAttachment(subject, activation.Slot, openGate: false, ref handler);
            handler?.TrackActivation(subject, activation);
        }

        return activation;
    }

    /// <summary>
    /// Activates and, when <paramref name="handler"/> owns the activation, waits for its start. On a
    /// recorded start fault the activation is removed before the fault propagates, with the release a
    /// faulted <see cref="AttachHostedServiceAsync{T}"/> uses; a cancelled wait leaves it in place.
    /// Returns without waiting when the subject is not an <see cref="ISubjectHostedServiceFactory"/>
    /// or another handler, or none, owns the activation. The caller has opened the handler with
    /// <see cref="HostedServiceHandler.OpenGate"/>.
    /// </summary>
    /// <remarks>
    /// On the idempotent path the start waited for is the existing activation's, so a start fault
    /// already recorded on it is released and rethrown without a retry, the same reading of a fault
    /// that <see cref="HostedServiceHandler.WaitForStartAsync"/> gives.
    /// </remarks>
    internal static async Task ActivateHostedServiceAsync(
        this IInterceptorSubject subject,
        IServiceProvider serviceProvider,
        HostedServiceHandler handler,
        CancellationToken cancellationToken)
    {
        var attachment = subject.ActivateHostedService(serviceProvider);
        if (attachment is null)
        {
            return;
        }

        var slot = ((IHostedServiceSlotAccess)attachment).Slot;
        if (!ReferenceEquals(slot.Owner, handler))
        {
            return;
        }

        await handler.WaitForQueuedTransitionsAsync(slot, cancellationToken).ConfigureAwait(false);

        // The start's own outcome rather than Fault, for the reason on StartFault.
        if (slot.StartFault is { } fault)
        {
            ReleaseFaultedAttachmentAndThrow(subject, attachment, handler, fault);
        }
    }

    /// <summary>
    /// The subject's activation attachment when one is on the subject and not explicitly detached, or
    /// null. Reads only, so a context attach of an already activated subject allocates nothing.
    /// </summary>
    internal static HostedServiceAttachment<IHostedService>? TryGetLiveActivation(this IInterceptorSubject subject)
        => subject.Data.TryGetValue((null, ActivationKey), out var value)
            && value is HostedServiceAttachment<IHostedService> { Slot.IsDetached: false } activation
            ? activation
            : null;

    /// <summary>
    /// Returns the subject's live activation attachment, published, or reserves the entry with a new one
    /// for <paramref name="factory"/> and publishes it to the subject's attachments.
    /// <paramref name="published"/> is true for the call that published, which then starts it; every
    /// other call returns the attachment that call has published. <paramref name="serviceProvider"/>
    /// is what the service is created with; null means the provider of the handler starting it.
    /// </summary>
    /// <remarks>
    /// Lock free, because the automatic activation runs inside the lifecycle lock and an explicit one
    /// takes that lock on its way to the start, so a lock of the entry's own held across either would
    /// give two threads opposite orders. The entry is reserved before the attachment is published, never
    /// after: a loser that had already published would have to remove an attachment a context attach
    /// may have started in between. Starting the winner's attachment is the winner's job, through the
    /// same two lookups an attach makes around its add.
    /// <para>
    /// The detach mark rather than <see cref="HostedServiceAttachmentState.Removed"/> or membership in
    /// the subject's attachments: the state reads Running and then Stopping for as long as the detach's
    /// stop takes, and membership is false for the window between a winner's reservation and its publish.
    /// </para>
    /// </remarks>
    internal static HostedServiceAttachment<IHostedService> GetOrAddActivation(
        this IInterceptorSubject subject, ISubjectHostedServiceFactory factory, IServiceProvider? serviceProvider, out bool published)
    {
        HostedServiceAttachment<IHostedService>? candidate = null;
        while (true)
        {
            var reserved = subject.Data.TryGetValue((null, ActivationKey), out var value);
            if (value is HostedServiceAttachment<IHostedService> { Slot.IsDetached: false } activation)
            {
                // Waits for the winner's publish, so what this returns can be detached: a detach of an
                // attachment the subject does not hold yet returns false and leaves the winner to
                // publish and start it. Bounded, because the winner holds no lock and runs no user
                // code between its reservation and its publish. An attachment detached meanwhile is
                // read again, which reserves a fresh one.
                var spinner = new SpinWait();
                while (!activation.Slot.IsDetached && !subject.GetHostedServiceAttachments().Contains(activation))
                {
                    spinner.SpinOnce();
                }

                if (activation.Slot.IsDetached)
                {
                    continue;
                }

                published = false;
                return activation;
            }

            // Built once per call, outside the exchange, for the reason on AddAttachment.
            candidate ??= ActivationFactory.CreateAttachment(factory, serviceProvider);

            var won = reserved
                ? subject.Data.TryUpdate((null, ActivationKey), candidate, value)
                : subject.Data.TryAdd((null, ActivationKey), candidate);
            if (won)
            {
                try
                {
                    PublishAttachment(subject, candidate);
                }
                catch
                {
                    // A reservation that is never published holds every later activator in its wait
                    // for good, an automatic one inside the lifecycle lock included. The mark is what
                    // releases them; the entry is cleared so the next one reserves afresh.
                    candidate.Slot.MarkDetached();
                    subject.Data.TryRemove(KeyValuePair.Create<(string? property, string key), object?>((null, ActivationKey), candidate));
                    throw;
                }

                published = true;
                return candidate;
            }
        }
    }

    /// <summary>
    /// Drops a detached attachment from the activation entry when it is the activation, so the subject
    /// does not retain it. Runs after the detach mark, which is what the next activation reads: an
    /// empty entry and a detached one both make it reserve a fresh attachment.
    /// </summary>
    private static void ClearActivationEntry(IInterceptorSubject subject, IHostedServiceAttachment attachment)
    {
        // The type test first, because this runs on every explicit detach and only a factory subject
        // can hold an entry. Removed only while it still holds this attachment, so a reservation that
        // has already replaced it is left alone.
        if (subject is ISubjectHostedServiceFactory)
        {
            subject.Data.TryRemove(KeyValuePair.Create<(string? property, string key), object?>((null, ActivationKey), attachment));
        }
    }

    /// <summary>
    /// The factory of an activation attachment. Creates the service with the provider the activation
    /// was given, or, for an automatic activation, with the provider of the handler that is starting
    /// it, read when the service is created.
    /// </summary>
    /// <remarks>
    /// Reaches the handler through the slot's owner rather than capturing one: the activation
    /// outlives the handler that published it, and a subject moved to a second host's graph is started
    /// by that host's handler with that host's provider, where a captured handler would hand it the
    /// first host's disposed container and root it. The start body reads the owner immediately before
    /// the factory, so a release landing in between falls back to a provider that resolves nothing,
    /// and the stop that release's detach enqueued disposes what was created.
    /// </remarks>
    private sealed class ActivationFactory(ISubjectHostedServiceFactory factory, IServiceProvider? serviceProvider)
    {
        // Assigned right after the slot is built, because the slot takes its factory delegate in
        // the constructor. Published together with the attachment, so a start body reads it set.
        private HostedServiceSlot? _slot;

        public static HostedServiceAttachment<IHostedService> CreateAttachment(
            ISubjectHostedServiceFactory factory, IServiceProvider? serviceProvider)
        {
            var activationFactory = new ActivationFactory(factory, serviceProvider);
            var slot = new HostedServiceSlot(activationFactory.Create, subject: null, isActivation: true);
            activationFactory._slot = slot;
            return new HostedServiceAttachment<IHostedService>(slot);
        }

        private IHostedService Create()
            => factory.CreateHostedService(
                serviceProvider ?? _slot?.Owner?.ServiceProvider ?? EmptyServiceProvider.Instance);
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
        start = StartPublishedAttachment(subject, attachment.Slot, openGate, ref handler);
        return attachment;
    }

    /// <summary>
    /// The second half of an attach, after the add has published: resolves the handler once more when
    /// the lookup before the add found none, then takes ownership and enqueues the start. Returns the
    /// start, or null when nothing was enqueued.
    /// </summary>
    private static Task? StartPublishedAttachment(
        IInterceptorSubject subject, HostedServiceSlot slot, bool openGate, ref HostedServiceHandler? handler)
    {
        handler ??= TryResolveHandlerAfterPublish(subject);
        if (handler is null)
        {
            return null;
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
        return handler.TryTakeOwnershipAndStart(subject, slot);
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
        ClearActivationEntry(subject, attachment);

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
        if (handler is null)
        {
            return null;
        }

        // An activation's stop takes the slot's signal while this handler owns it, so the stops of
        // the subject's other attachments ordered behind it, and a signal they created on asking, are
        // released. A refused enqueue means another handler, or none, owns the slot, so no signal
        // was created for this one, and the plain stop still stops and disposes the instance.
        return slot.CarriesStopSignal
            ? handler.EnqueueStopIfOwned(subject, slot, waitFor: null, cancellationToken)
                ?? handler.EnqueueAttachmentStop(subject, slot, cancellationToken)
            : handler.EnqueueAttachmentStop(subject, slot, cancellationToken);
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
        // record inside it can register a slot that loses the swap and is never seen again.
        var attachment = new HostedServiceAttachment<T>(new HostedServiceSlot(factory, subject: null));
        PublishAttachment(subject, attachment);
        return attachment;
    }

    /// <summary>Adds a built attachment to the subject's attachments, which is what a context attach reads.</summary>
    private static void PublishAttachment(IInterceptorSubject subject, IHostedServiceAttachment attachment)
    {
        subject.Data.AddOrUpdate((null, AttachmentsKey),
            _ => ImmutableArray.Create<IHostedServiceAttachment>(attachment),
            (_, value) => value is ImmutableArray<IHostedServiceAttachment> attachments
                ? attachments.Add(attachment)
                : ImmutableArray.Create<IHostedServiceAttachment>(attachment));
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
