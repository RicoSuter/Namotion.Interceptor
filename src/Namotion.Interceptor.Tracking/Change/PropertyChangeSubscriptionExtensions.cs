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
    /// Delivery keeps the inline part of the
    /// <see cref="SubscribeInline(PropertyReference, IPropertyChangeObserver)"/> contract: on the writing
    /// thread, and a throwing handler propagates back into the setter. The context-level
    /// <c>GetPropertyChangeObservable</c> reschedules onto a scheduler by default and is therefore not the
    /// same thing.
    /// <para>
    /// Unlike that contract, notifications are serialized per subscriber as the Rx grammar requires, so
    /// stateful operators such as <c>Take</c>, <c>Skip</c>, <c>Scan</c>, <c>DistinctUntilChanged</c> and
    /// <c>Buffer</c> by count compose safely over concurrent writers with no extra work. The handler runs
    /// under a per-subscription lock held across the call, so it must not block and must not take locks of
    /// its own that a writer might hold.
    /// </para>
    /// <para>
    /// Adding any operator also changes what a throwing handler does. Operators wrap the observer in the
    /// auto-detaching decorator this type avoids, so the first handler exception stops propagating to the
    /// writer with the subscription intact and instead tears the subscription down silently. A handler
    /// composed over this sequence must therefore not throw at all.
    /// </para>
    /// <para>
    /// Composing the off-thread hop by hand costs more than the scheduler overloads of <c>Subscribe</c>:
    /// <c>ObserveOn</c> dedicates a private thread to each subscription on both <c>Scheduler.Default</c> and
    /// <c>TaskPoolScheduler</c>, because <c>Scheduler.AsLongRunning()</c> resolves an
    /// <c>ISchedulerLongRunning</c> from both through their <c>IServiceProvider</c> implementation rather than
    /// a direct interface cast. That thread is taken on the first signal rather than at subscribe, so an idle
    /// property hides the cost until it stops being idle. A handler exception also escapes the sink into the
    /// scheduler, terminating the process on the former and silently ending the stream on the latter.
    /// </para>
    /// <para>
    /// The sequence never completes and never signals OnError, so operators that wait for completion, such
    /// as <c>ToTask</c> and <c>LastAsync</c>, never return, and nothing disposes the subscription for you.
    /// Disposing what <c>Subscribe</c> returns is as mandatory as for
    /// <see cref="SubscribeInline(PropertyReference, IPropertyChangeObserver)"/>: a dropped handle keeps the
    /// observer receiving changes and permanently disables the process-wide idle write fast path, with no
    /// finalizer and no recovery.
    /// </para>
    /// </remarks>
    public static IObservable<SubjectPropertyChange> GetInlineChangeObservable(this PropertyReference property)
        => new InlineChangeObservable(property);

    /// <summary>
    /// Subscribes to changes of a single property and delivers them on <paramref name="scheduler"/> instead
    /// of on the writing thread, one at a time and in dispatch order.
    /// </summary>
    /// <remarks>
    /// Same ownership and dormancy contract as
    /// <see cref="SubscribeInline(PropertyReference, IPropertyChangeObserver)"/>, with three differences that
    /// follow from delivery being scheduled. Within this subscription the observer is never re-entered, so it
    /// needs no synchronization of its own; an observer, closure, or <paramref name="onError"/> delegate
    /// shared across several subscriptions is still invoked concurrently. It must still not block: the drain
    /// owns the scheduler thread for the whole call, so a blocking observer silently starves every other
    /// subscription sharing that scheduler. An exception from the observer
    /// cannot reach the writer and is reported to <paramref name="onError"/>, leaving the subscription live.
    /// And dormancy stops acceptance but not the drain, so a change accepted before the subject detaches is
    /// still delivered afterwards, unlike disposal, which drops what is still queued; an observer that looks
    /// its subject up in the registry or resolves its path must handle a subject that is no longer there.
    /// <para>
    /// Deferral widens the staleness window from microseconds to however long the queue is, so read
    /// <see cref="SubjectPropertyChange.GetCurrentValue{TValue}"/> rather than the delivered new value when
    /// you need the current one. That read can throw where the inline one practically cannot: on an
    /// <c>object</c>-typed or otherwise polymorphic property a window that wide lets the runtime type move on,
    /// and the read then throws <see cref="InvalidCastException"/> instead of returning something merely
    /// stale, which without <paramref name="onError"/> is a silently skipped delivery. Staleness is also not
    /// only about which commit a value describes: a delivered reference-typed value is the live object rather
    /// than a snapshot, so an array or other mutable object stays shared with the writer, which can keep
    /// mutating it after the delivery.
    /// </para>
    /// <para>
    /// The isolation from the writer is one-way. A throwing lifecycle handler, or a throwing inline observer
    /// that ran earlier on the same write, unwinds it before the change is enqueued: the value stays committed
    /// to the model, this subscription never hears about it and <paramref name="onError"/> stays silent.
    /// </para>
    /// <para>
    /// The queue is unbounded. A writer faster than the observer grows it without limit, and every buffered
    /// change keeps its subject alive; watch <see cref="ScheduledPropertySubscription.PendingCount"/> and keep
    /// the observer cheap enough for the drain to outrun the writer. Draining a backlog does not give the
    /// memory back, because the queue keeps the largest segment it ever grew to, so the subscription costs its
    /// peak backlog rather than its current one from then on, until it is disposed. Rate-limiting a hot
    /// property by composing <c>Sample</c> or <c>Throttle</c> over <see cref="GetInlineChangeObservable"/> is
    /// not the remedy: those operators deliver from a scheduler work item that does not catch a handler
    /// exception, so one throwing handler terminates the process on <c>Scheduler.Default</c>. An observer that
    /// writes the property it observes never drains: each delivery enqueues its own successor, so work items
    /// continue indefinitely and <paramref name="onError"/> never fires. The batched handoff keeps that a
    /// yielding loop rather than a held thread, so it degrades instead of starving, where the inline overload
    /// would raise a loud StackOverflowException.
    /// </para>
    /// <para>
    /// The caller owns the scheduler and must dispose subscriptions before it. A schedule that throws is
    /// reported to <paramref name="onError"/> and faults the subscription, which
    /// <see cref="ScheduledPropertySubscription.IsFaulted"/> reports even when no handler was supplied; a
    /// schedule that succeeds and whose work item never runs cannot be detected, and that subscription goes
    /// quiet. Prefer <c>Scheduler.Default</c>, whose thread pool takes the execution context per work item, so
    /// the suppression applied when scheduling keeps all ambient state away from the observer. A scheduler
    /// that owns a thread, such as <c>EventLoopScheduler</c>, instead keeps for life the <c>AsyncLocal</c>
    /// values ambient when that thread was created, so create one outside any transaction scope: otherwise a
    /// property write the observer makes joins a long-disposed transaction whose buffer has since been pooled
    /// and rented out, vanishing from its own path and committing under an unrelated transaction, with
    /// nothing thrown and <paramref name="onError"/> silent.
    /// </para>
    /// </remarks>
    /// <param name="property">The property to subscribe to.</param>
    /// <param name="observer">The observer, invoked on <paramref name="scheduler"/>.</param>
    /// <param name="scheduler">The scheduler each change is delivered on. Its <c>Schedule</c> is called from
    /// inside the write, so it must not block, for the same reason <paramref name="onError"/> must not: a
    /// scheduler that blocks there blocks the writer and can do so under the transaction commit lock.
    /// Synchronous schedulers are rejected, and would not give inline delivery anyway: under
    /// <c>WithTransactions</c> the drain would run consumer code under the commit lock, where an observer that
    /// starts a transaction is rejected as nested and one that blocks waiting on that lock deadlocks. Only the
    /// two singletons are detectable; any other scheduler that runs actions inline, including a
    /// <c>DisableOptimizations()</c> wrapper over them, carries the same hazard undetected and additionally
    /// turns the batched handoff into recursion rather than a yield, one stack frame per batch of queued
    /// changes, so the drain's stack depth grows with the backlog.</param>
    /// <param name="onError">Invoked when the observer or the scheduler throws; the exception is swallowed
    /// when null, which also makes a permanently throwing observer invisible, since such an observer keeps
    /// firing and keeps failing rather than stopping. It must not throw, and an escape is caught and
    /// swallowed because a handler failure entering a scheduler work item terminates the process. It may run
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
        // Wrapping first would bypass the observer null guard and fail on a writer thread at dispatch time.
        ArgumentNullException.ThrowIfNull(callback);
        return subject.SubscribeToProperty(propertySelector, new DelegateObserver(callback), scheduler, onError);
    }

    private static void ThrowIfSynchronous(IScheduler scheduler)
    {
        // Only the two singletons are detectable. Any other scheduler that runs actions inline, including a
        // DisableOptimizations wrapper over these, has the same hazard and cannot be rejected: it also turns
        // the batch handoff into recursion instead of a yield, one stack frame per MaxBatch queued changes.
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
