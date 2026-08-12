using System.Linq.Expressions;
using System.Reactive.Concurrency;
using System.Reflection;

namespace Namotion.Interceptor.Tracking.Change;

public static class PropertyChangeSubscriptionExtensions
{
    /// <summary>
    /// Subscribes an observer to changes of a single property (subject instance + name). Delivery is
    /// inline, on the writing thread, and dormant while the subject is not attached to a context
    /// with a <see cref="PropertyChangeInterceptor"/>. See <see cref="IPropertyChangeObserver"/> for the contract.
    /// </summary>
    /// <remarks>
    /// Disposing the returned handle is mandatory: the subject holds a strong reference, so a dropped
    /// handle keeps the observer alive and permanently disables the process-wide idle write fast path.
    /// Dispatches already in flight may still invoke the observer after Dispose returns.
    /// Under concurrent writes to the same property, notifications may arrive out of commit order because
    /// dispatch runs outside the subject lock; if you need the current value, re-read the property rather
    /// than relying on the delivered new value.
    /// Provided the downstream interceptor chain returns normally after the commit, a write that commits
    /// after SubscribeInline returns is always delivered while the subscription stays live and no earlier
    /// inline observer of the same write throws. A write that committed before may not be, and reading
    /// the property after subscribing observes that earlier state. OldValue is the value the setter observed
    /// when it started, including when the subscription raced the write.
    /// </remarks>
    public static IDisposable SubscribeInline(this PropertyReference property, IPropertyChangeObserver observer)
    {
        // A null observer would install a silent never-firing subscription that still opens the process-wide gate.
        ArgumentNullException.ThrowIfNull(observer);

        var metadata = property.Metadata; // throws InvalidOperationException when the name is not a known property
        if (!(metadata.IsIntercepted || metadata.IsDerived))
        {
            throw new ArgumentException(
                $"Property '{property.Name}' on {property.Subject.GetType().Name} cannot be subscribed to: it is not an intercepted or derived property, so its changes never enter the interception chain.",
                nameof(property));
        }

        return PropertyChangeSubscription.Create(property, observer);
    }

    /// <summary>Delegate overload of <see cref="SubscribeInline(PropertyReference, IPropertyChangeObserver)"/>.</summary>
    public static IDisposable SubscribeInline(this PropertyReference property, PropertyChangeCallback callback)
    {
        // A null callback wrapped in DelegateObserver would fail on a writer thread at dispatch time.
        ArgumentNullException.ThrowIfNull(callback);
        return property.SubscribeInline(new DelegateObserver(callback));
    }

    /// <summary>
    /// Strongly-typed subscription to a direct property of <paramref name="subject"/>, for example
    /// <c>subject.SubscribeToPropertyInline(x => x.Temperature, observer)</c>. Only a direct property access on
    /// the lambda parameter is accepted; chained, captured, static, field, and method selectors throw.
    /// </summary>
    /// <remarks>
    /// Same ownership, concurrency, and delivery contract as
    /// <see cref="SubscribeInline(PropertyReference, IPropertyChangeObserver)"/>.
    /// </remarks>
    public static IDisposable SubscribeToPropertyInline<TSubject, TValue>(
        this TSubject subject,
        Expression<Func<TSubject, TValue>> propertySelector,
        IPropertyChangeObserver observer)
        where TSubject : IInterceptorSubject
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(propertySelector);

        var name = ResolveDirectPropertyName(propertySelector);
        return new PropertyReference(subject, name).SubscribeInline(observer);
    }

    /// <summary>Delegate overload of <see cref="SubscribeToPropertyInline{TSubject,TValue}(TSubject, Expression{Func{TSubject,TValue}}, IPropertyChangeObserver)"/>.</summary>
    public static IDisposable SubscribeToPropertyInline<TSubject, TValue>(
        this TSubject subject,
        Expression<Func<TSubject, TValue>> propertySelector,
        PropertyChangeCallback callback)
        where TSubject : IInterceptorSubject
    {
        // Wrapping first would bypass the observer null guard and fail on a writer thread at dispatch time.
        ArgumentNullException.ThrowIfNull(callback);
        return subject.SubscribeToPropertyInline(propertySelector, new DelegateObserver(callback));
    }

    /// <summary>
    /// Exposes a single property's changes as an observable, so Rx operators compose over a per-property
    /// subscription. Each subscriber installs its own underlying subscription, and each call returns a
    /// distinct instance.
    /// </summary>
    /// <remarks>
    /// Delivery keeps the contract of <see cref="SubscribeInline(PropertyReference, IPropertyChangeObserver)"/>
    /// exactly: inline, on the writing thread, possibly concurrent, and a throwing handler propagates
    /// back into the setter. It is that channel wearing an <see cref="IObservable{T}"/>, not a safer one.
    /// The context-level <c>GetPropertyChangeObservable</c> reschedules onto a scheduler by default and is
    /// therefore not the same thing.
    /// <para>
    /// Notifications are not serialized, so this sequence violates the Rx grammar. OnNext is raised straight
    /// from the writing thread, and concurrent writers to one property raise it concurrently on one observer.
    /// <see cref="PropertyChangeInterceptor"/> wraps its context-level subject in <c>Subject.Synchronize</c>
    /// precisely to be grammar-conformant; this sequence deliberately is not. Rx operators assume serialized
    /// notifications and most stateful sinks are unlocked, so apply <c>.Synchronize()</c> before any stateful
    /// operator, including <c>Take</c>, <c>Skip</c>, <c>Scan</c>, <c>DistinctUntilChanged</c> and
    /// <c>Buffer</c> by count. Without it they corrupt rarely rather than never: an unsynchronized
    /// <c>Take(1)</c> fed by four writer threads forwarded more than one item in 1 round out of 2000.
    /// </para>
    /// <para>
    /// Adding any operator also changes what a throwing handler does. Operators wrap the observer in the
    /// auto-detaching decorator this type avoids, so the first handler exception stops propagating to the
    /// writer with the subscription intact and instead tears the subscription down silently. A handler
    /// composed over this sequence must therefore not throw at all.
    /// </para>
    /// <para>
    /// Two further hazards when composing the off-thread hop by hand. <c>ObserveOn</c> dedicates a private
    /// thread to each subscription when <c>Scheduler.AsLongRunning()</c> resolves an
    /// <c>ISchedulerLongRunning</c>, which it does for both <c>Scheduler.Default</c> and
    /// <c>TaskPoolScheduler</c> through their <c>IServiceProvider</c> implementation rather than a direct
    /// cast, so composing it per property is unaffordable. That thread is taken on the first signal, not at
    /// subscribe, so an idle property hides the cost. And an exception from the handler escapes the
    /// <c>ObserveOn</c> sink into the scheduler, which does not catch it: on <c>Scheduler.Default</c> it goes
    /// unhandled and terminates the process, and on <c>TaskPoolScheduler</c> it becomes an unobserved task
    /// exception and the subscription stops delivering, so one bad change silently ends the stream. Prefer
    /// the scheduler overloads of <c>Subscribe</c>, which have neither.
    /// </para>
    /// <para>
    /// The sequence never completes and never signals OnError, so operators that wait for completion, such
    /// as <c>ToTask</c> and <c>LastAsync</c>, never return. Because it never completes, nothing disposes the
    /// subscription for you, and disposing what <c>Subscribe</c> returns is as mandatory as for
    /// <see cref="SubscribeInline(PropertyReference, IPropertyChangeObserver)"/>: a dropped handle keeps the
    /// observer receiving changes and permanently disables the process-wide idle write fast path, with no
    /// finalizer and no recovery.
    /// </para>
    /// </remarks>
    /// <param name="property">The property to observe.</param>
    public static IObservable<SubjectPropertyChange> GetInlineChangeObservable(this PropertyReference property)
        => new InlineChangeObservable(property);

    /// <summary>
    /// Subscribes to changes of a single property and delivers them on <paramref name="scheduler"/> instead
    /// of on the writing thread, one at a time and in dispatch order.
    /// </summary>
    /// <remarks>
    /// Same ownership and dormancy contract as
    /// <see cref="SubscribeInline(PropertyReference, IPropertyChangeObserver)"/>, with four differences that follow
    /// from delivery being scheduled. Within this subscription the observer is never re-entered, so it needs
    /// no synchronization of its own; an observer, closure, or <paramref name="onError"/> delegate shared
    /// across several subscriptions is still invoked concurrently. An exception from the observer cannot
    /// reach the writer and is reported to <paramref name="onError"/>, leaving the subscription live. A
    /// change still queued when the subscription is disposed is dropped, except one enqueued by a writer that
    /// had already passed its own state check, which stays queued and keeps pinning its subject for as long
    /// as the handle is reachable. And a change accepted before the
    /// subject detaches is still delivered afterwards, which disposal is not: dormancy stops acceptance, not
    /// the drain, so an observer that looks its subject up in the registry or resolves its path must handle a
    /// subject that is no longer there.
    /// <para>
    /// The isolation from the writer is one-way. This channel sits downstream of everyone else's throws: a
    /// throwing lifecycle handler, or a throwing inline observer that ran earlier on the same write, unwinds
    /// the write before the change is enqueued. The value stays committed to the model, this subscription
    /// never hears about it, <paramref name="onError"/> stays silent and
    /// <see cref="ScheduledPropertySubscription.PendingCount"/> reads zero.
    /// </para>
    /// <para>
    /// The queue is unbounded. A writer faster than the observer grows it without limit, and every buffered
    /// change keeps its subject alive; watch <see cref="ScheduledPropertySubscription.PendingCount"/> and keep
    /// the observer cheap enough for the drain to outrun the writer. Rate-limiting a hot property by composing
    /// <c>Sample</c> or <c>Throttle</c> over <see cref="GetInlineChangeObservable"/> is not the remedy: those
    /// operators deliver from a scheduler work item that does not catch a handler exception, so one throwing
    /// handler terminates the process on <c>Scheduler.Default</c>. An observer
    /// that writes the property it observes never drains, quietly, where the inline overload would
    /// raise a StackOverflowException.
    /// </para>
    /// <para>
    /// The caller owns the scheduler and must dispose subscriptions before it. A schedule that throws is
    /// reported to <paramref name="onError"/> and faults the subscription, which
    /// <see cref="ScheduledPropertySubscription.IsFaulted"/> reports even when no handler was supplied; a
    /// schedule that succeeds and whose work item never runs cannot be detected, and that subscription goes
    /// quiet. Prefer <c>Scheduler.Default</c>, whose thread pool takes the execution context per work item, so
    /// the suppression applied when scheduling is the whole story and no ambient state reaches the observer. A
    /// scheduler that owns a thread, such as <c>EventLoopScheduler</c>, creates it on the first <c>Schedule</c>
    /// and that thread keeps the <c>AsyncLocal</c> values ambient at its creation for life, which no
    /// suppression can strip. Create such a scheduler outside any transaction scope: a worker thread born
    /// inside one carries that transaction forever, so a property write the observer makes lands in a
    /// transaction disposed long ago whose pending-change buffer has since been pooled and rented by a live
    /// one. The write then vanishes from its own path and is committed by an unrelated transaction, with
    /// nothing thrown and <paramref name="onError"/> silent.
    /// </para>
    /// </remarks>
    /// <param name="property">The property to subscribe to.</param>
    /// <param name="observer">The observer, invoked on <paramref name="scheduler"/>.</param>
    /// <param name="scheduler">The scheduler each change is delivered on. Synchronous schedulers are rejected.
    /// Its <c>Schedule</c> is called from inside the write, so it must not block, for the same reason
    /// <paramref name="onError"/> must not: a scheduler that blocks there blocks the writer and can do so
    /// under the transaction commit lock.</param>
    /// <param name="onError">Invoked when the observer or the scheduler throws; the exception is swallowed
    /// when null, which also makes a permanently throwing observer invisible. It must not throw, may run
    /// after Dispose returns, is serialized per subscription rather than per delegate, and can run
    /// synchronously on the writer thread under a transaction commit lock, so it must not write properties,
    /// start a transaction, or block.</param>
    public static ScheduledPropertySubscription Subscribe(
        this PropertyReference property,
        IPropertyChangeObserver observer,
        IScheduler scheduler,
        Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(scheduler);
        ThrowIfSynchronous(scheduler);

        return ScheduledPropertySubscription.Create(property, observer, scheduler, onError);
    }

    /// <summary>Delegate overload of <see cref="Subscribe(PropertyReference, IPropertyChangeObserver, IScheduler, Action{Exception})"/>.</summary>
    public static ScheduledPropertySubscription Subscribe(
        this PropertyReference property,
        PropertyChangeCallback callback,
        IScheduler scheduler,
        Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return property.Subscribe(new DelegateObserver(callback), scheduler, onError);
    }

    /// <summary>
    /// Strongly-typed scheduled subscription to a direct property of <paramref name="subject"/>, for example
    /// <c>subject.SubscribeToProperty(x => x.Temperature, observer, scheduler)</c>.
    /// </summary>
    /// <remarks>
    /// Same contract as
    /// <see cref="Subscribe(PropertyReference, IPropertyChangeObserver, IScheduler, Action{Exception})"/>,
    /// and the same selector restriction as
    /// <see cref="SubscribeToPropertyInline{TSubject,TValue}(TSubject, Expression{Func{TSubject,TValue}}, IPropertyChangeObserver)"/>.
    /// </remarks>
    public static ScheduledPropertySubscription SubscribeToProperty<TSubject, TValue>(
        this TSubject subject,
        Expression<Func<TSubject, TValue>> propertySelector,
        IPropertyChangeObserver observer,
        IScheduler scheduler,
        Action<Exception>? onError = null)
        where TSubject : IInterceptorSubject
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(propertySelector);

        var name = ResolveDirectPropertyName(propertySelector);
        return new PropertyReference(subject, name).Subscribe(observer, scheduler, onError);
    }

    /// <summary>Delegate overload of <see cref="SubscribeToProperty{TSubject,TValue}(TSubject, Expression{Func{TSubject,TValue}}, IPropertyChangeObserver, IScheduler, Action{Exception})"/>.</summary>
    public static ScheduledPropertySubscription SubscribeToProperty<TSubject, TValue>(
        this TSubject subject,
        Expression<Func<TSubject, TValue>> propertySelector,
        PropertyChangeCallback callback,
        IScheduler scheduler,
        Action<Exception>? onError = null)
        where TSubject : IInterceptorSubject
    {
        // Wrapping first would bypass the observer null guard, since a wrapped null callback is a non-null
        // observer, and the failure would surface on a writer thread at dispatch time instead.
        ArgumentNullException.ThrowIfNull(callback);
        return subject.SubscribeToProperty(propertySelector, new DelegateObserver(callback), scheduler, onError);
    }

    private static void ThrowIfSynchronous(IScheduler scheduler)
    {
        // Only the two singletons are detectable. Any scheduler that runs actions inline has the same
        // hazard, including DisableOptimizations wrappers over these, and cannot be rejected. Such a
        // scheduler also turns the batch handoff into recursion instead of a yield, one stack frame per
        // MaxBatch queued changes.
        if (ReferenceEquals(scheduler, ImmediateScheduler.Instance)
            || ReferenceEquals(scheduler, CurrentThreadScheduler.Instance))
        {
            throw new ArgumentException(
                "A synchronous scheduler does not deliver inline: the dispatcher's work-in-progress counter " +
                "means one writer ends up draining every other writer's changes inside its own setter, so " +
                "that writer's latency grows with total throughput. Use property.SubscribeInline(callback) " +
                "for inline delivery.",
                nameof(scheduler));
        }
    }

    private static string ResolveDirectPropertyName<TSubject, TValue>(Expression<Func<TSubject, TValue>> propertySelector)
    {
        var body = propertySelector.Body;

        // Unwrap a Convert/ConvertChecked boxing or numeric-cast node (e.g. Expression<Func<T, object>>).
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            body = unary.Operand;
        }

        if (body is not MemberExpression member
            || member.Member is not PropertyInfo property
            || member.Expression != propertySelector.Parameters[0])
        {
            throw new ArgumentException(
                "Only a direct property access on the lambda parameter is supported, for example x => x.Foo. " +
                "Chained (x => x.Child.Foo), captured-variable, static, field, and method selectors are not allowed.",
                nameof(propertySelector));
        }

        return property.Name;
    }

    private sealed class DelegateObserver(PropertyChangeCallback callback) : IPropertyChangeObserver
    {
        public void OnChange(in SubjectPropertyChange change) => callback(in change);
    }
}
