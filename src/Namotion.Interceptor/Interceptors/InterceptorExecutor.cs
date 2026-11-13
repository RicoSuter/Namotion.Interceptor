using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Interceptors;

public sealed class InterceptorExecutor : InterceptorSubjectContext, IInterceptorExecutor
{
    private readonly IInterceptorSubject _subject;

    /// <summary>
    /// The subject this executor was constructed for. Exposed so the terminal write can assert that the
    /// context's executor and the locked subject are the same pairing its plain increment relies on.
    /// </summary>
    internal IInterceptorSubject Subject => _subject;

    /// <summary>
    /// Monotonic per-subject commit counter. Incremented by the terminal write while the subject's
    /// SyncRoot is held, so a plain increment is exclusive: no Interlocked needed. Dense over
    /// committed writes and never reset, so it stays comparable across detach and reattach.
    ///
    /// It records commit order, it does not establish it: the lock is what serializes the commits, and
    /// this counter labels them afterwards. Consumers do order changes by comparing it (see the flush
    /// merging in docs/tracking.md), but nothing in the write path depends on its value.
    ///
    /// Consumes a revision exactly when the terminal write runs. A vetoed write and a write stopped
    /// by the equality check never reach the terminal and consume nothing, but a derived property's
    /// recalculation does reach it (with a no-op write delegate) and takes a revision of its own.
    /// </summary>
    internal long Revision;

    public InterceptorExecutor(IInterceptorSubject subject)
    {
        _subject = subject;
    }

    /// <summary>
    /// Returns the subject's executor, publishing one on first access. Call it from the subject's
    /// <see cref="IInterceptorSubject.Context"/> accessor, passing that subject's own backing field.
    /// Public because the source generator emits the call into the consumer's assembly.
    /// </summary>
    /// <remarks>
    /// Compare-and-swap rather than <c>??=</c>: a lazy assignment lets two threads racing the first
    /// access each publish an executor and discard one, along with everything that had been put on it,
    /// including the per-subject commit revision counter. It is also the store that safely publishes
    /// the new instance, which a plain assignment is not.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IInterceptorExecutor GetOrCreate(ref IInterceptorExecutor? context, IInterceptorSubject subject)
    {
        // The allocation sits in a separate non-inlined method so the accessor stays a load and a
        // branch, small enough to inline into its own callers.
        return context ?? CreateAndPublish(ref context, subject);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IInterceptorExecutor CreateAndPublish(ref IInterceptorExecutor? context, IInterceptorSubject subject)
    {
        var created = new InterceptorExecutor(subject);
        return Interlocked.CompareExchange(ref context, created, null) ?? created;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TProperty GetPropertyValue<TProperty>(string propertyName, Func<IInterceptorSubject, TProperty> readValue)
    {
        var context = new PropertyReadContext<TProperty>(new PropertyReference(_subject, propertyName));
        PropertyAccessObservation.ReadObserver?.OnPropertyRead(context.Property);
        return ExecuteInterceptedRead(ref context, readValue);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> writeValue)
    {
        var context = new PropertyWriteContext<TProperty>(
            this,
            new PropertyReference(_subject, propertyName),
            currentValue,
            newValue);

        ExecuteObservedWrite(ref context, writeValue);
        return context.IsWritten;
    }

    /// <summary>
    /// Publishes a derived property's calculated value through the write chain without storing anything, with
    /// <see cref="PropertyWriteContext{TProperty}.IsDerivedRecalculation"/> set. A nonzero
    /// <paramref name="rawTimestamp"/> pre-populates the new write context's timestamp cache, which lets a
    /// recalculation cascade share the trigger's captured time without pushing a
    /// <see cref="SubjectChangeContext.WithChangedTimestamp(DateTimeOffset?)"/> scope. With
    /// <paramref name="finalValueIsNewValue"/> set, publishing reuses the new value instead of invoking the
    /// getter again (see <see cref="PropertyWriteContext{TProperty}.FinalValueIsNewValue"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void PublishDerivedPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, long rawTimestamp, bool finalValueIsNewValue)
    {
        var context = new PropertyWriteContext<TProperty>(
            this,
            new PropertyReference(_subject, propertyName),
            currentValue,
            newValue,
            rawTimestamp,
            finalValueIsNewValue);

        ExecuteObservedWrite(ref context, static (_, _) => { });
    }

    /// <summary>
    /// Runs the chain and reports the write to the write observer afterwards, also when the chain threw.
    /// </summary>
    private void ExecuteObservedWrite<TProperty>(ref PropertyWriteContext<TProperty> context, Action<IInterceptorSubject, TProperty> writeValue)
    {
        var depth = PropertyAccessObservation.EnterWrite();
        var hasFailed = true;
        try
        {
            ExecuteInterceptedWrite(ref context, writeValue);
            hasFailed = false;
        }
        finally
        {
            // An observer exception replaces the chain's; observers must not throw when the chain has failed.
            PropertyAccessObservation.ExitWrite(depth);
            PropertyAccessObservation.WriteObserver?.OnPropertyWritten(new PropertyWriteObservation(
                context.Property,
                context.IsWritten,
                context.IsWritten ? context.WriteTimestampRaw : 0,
                context.IsDerivedRecalculation,
                depth,
                hasFailed));
        }
    }

    /// <summary>
    /// Reads a property of a subject that has no context. Called by generated code.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static TProperty GetPropertyValueWithoutContext<TProperty>(IInterceptorSubject subject, string propertyName, Func<IInterceptorSubject, TProperty> readValue)
    {
        PropertyAccessObservation.ReadObserver?.OnPropertyRead(new PropertyReference(subject, propertyName));
        return readValue(subject);
    }

    /// <summary>
    /// Writes a property of a subject that has no context. Called by generated code.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static bool SetPropertyValueWithoutContext<TProperty>(IInterceptorSubject subject, string propertyName, TProperty newValue, Action<IInterceptorSubject, TProperty> setValue)
    {
        var observer = PropertyAccessObservation.WriteObserver;
        if (observer is null)
        {
            setValue(subject, newValue);

            // The first observer can be installed during the write, and an evaluation can read the old value after
            // that: the write is reported to it. The fence pairs with the one of the installation, so either this
            // read sees the observer or that evaluation sees the new value.
            Interlocked.MemoryBarrier();
            PropertyAccessObservation.WriteObserver?.OnPropertyWritten(new PropertyWriteObservation(
                new PropertyReference(subject, propertyName), true, 0, false, PropertyAccessObservation.WriteDepth + 1, false));
            return true;
        }

        var depth = PropertyAccessObservation.EnterWrite();
        var hasFailed = true;
        try
        {
            setValue(subject, newValue);
            hasFailed = false;
        }
        finally
        {
            // An observer exception replaces the write's; observers must not throw when the write has failed.
            PropertyAccessObservation.ExitWrite(depth);
            observer.OnPropertyWritten(new PropertyWriteObservation(
                new PropertyReference(subject, propertyName), !hasFailed, 0, false, depth, hasFailed));
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public object? InvokeMethod(string methodName, object?[] parameters, Func<IInterceptorSubject, object?[], object?> invokeMethod)
    {
        var context = new MethodInvocationContext(_subject, methodName, parameters);
        return ExecuteInterceptedInvoke(ref context, invokeMethod);
    }

    public override bool AddFallbackContext(IInterceptorSubjectContext context)
    {
        var result = base.AddFallbackContext(context);
        if (result)
        {
            var array = context.GetServices<ILifecycleInterceptor>();
            for (var index = 0; index < array.Length; index++)
            {
                var interceptor = array[index];
                interceptor.AttachSubjectToContext(_subject);
            }
        }

        return result;
    }

    public override bool RemoveFallbackContext(IInterceptorSubjectContext context)
    {
        if (HasFallbackContext(context))
        {
            var array = context.GetServices<ILifecycleInterceptor>();
            for (var index = 0; index < array.Length; index++)
            {
                var interceptor = array[index];
                interceptor.DetachSubjectFromContext(_subject);
            }

            return base.RemoveFallbackContext(context);
        }

        return false;
    }
}