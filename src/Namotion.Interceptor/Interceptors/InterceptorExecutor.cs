using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Interceptors;

public sealed class InterceptorExecutor : InterceptorSubjectContext, IInterceptorExecutor
{
    private readonly IInterceptorSubject _subject;

    /// <summary>
    /// Monotonic per-subject commit counter. Incremented by the terminal write while the subject's
    /// SyncRoot is held, so a plain increment is exclusive: no Interlocked needed. Dense over
    /// committed writes (vetoed and no-op writes never reach the terminal) and never reset, so it
    /// stays comparable across detach and reattach. A label only: ordering does not depend on it.
    /// </summary>
    internal long Revision;

    public InterceptorExecutor(IInterceptorSubject subject)
    {
        _subject = subject;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TProperty GetPropertyValue<TProperty>(string propertyName, Func<IInterceptorSubject, TProperty> readValue)
    {
        var context = new PropertyReadContext<TProperty>(new PropertyReference(_subject, propertyName));
        return ExecuteInterceptedRead(ref context, readValue);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> writeValue)
    {
        var context = new PropertyWriteContext<TProperty>(
            new PropertyReference(_subject, propertyName),
            currentValue,
            newValue);

        ExecuteInterceptedWrite(ref context, writeValue);
        return context.IsWritten;
    }

    /// <summary>
    /// Cascade re-entry path: skips the lazy-resolve machinery by pre-populating the new write
    /// context's timestamp cache. Lets the cascade share the trigger's captured time without
    /// pushing a <see cref="SubjectChangeContext.WithChangedTimestamp(DateTimeOffset?)"/> scope.
    /// Pass <c>finalValueIsNewValue</c> as true when the new value is already the stabilized getter
    /// output, so publishing reuses it instead of invoking the getter again (see
    /// <see cref="PropertyWriteContext{TProperty}.FinalValueIsNewValue"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> writeValue, long rawTimestamp, bool finalValueIsNewValue)
    {
        var context = new PropertyWriteContext<TProperty>(
            new PropertyReference(_subject, propertyName),
            currentValue,
            newValue,
            rawTimestamp)
        {
            FinalValueIsNewValue = finalValueIsNewValue
        };

        ExecuteInterceptedWrite(ref context, writeValue);
        return context.IsWritten;
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