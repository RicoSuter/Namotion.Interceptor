using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Cache;

internal static class ReadInterceptorFactory<TProperty>
{
    public static ReadFunc<TProperty> Create(ImmutableArray<IReadInterceptor> interceptors)
    {
        // Generated code passes a terminal that is one field access of TProperty, and the runtime
        // already loads references and word-sized primitives atomically, so those skip the Monitor,
        // which is otherwise the largest cost of an intercepted read. The skip gives up the acquire
        // fence the lock carried, never atomicity.
        //
        // Dynamic registry properties dispatch with TProperty widened to object and a getter that
        // can read a field of any declared type and box it, so an object read can still copy a wide
        // struct and has to keep the lock.
        ReadFunc<TProperty> terminal =
            typeof(TProperty) != typeof(object) && AtomicAccess.IsGuaranteedFor(typeof(TProperty))
                ? ReadDirectly
                : ReadUnderLock;

        return interceptors.Length == 0
            ? terminal
            : new ReadInterceptorChain<TProperty>(interceptors, terminal).Execute;
    }

    private static TProperty ReadDirectly(ref PropertyReadContext<TProperty> context, Func<IInterceptorSubject, TProperty> innerReadValue)
    {
        return innerReadValue(context.Property.Subject);
    }

    // The write terminal commits under SyncRoot. A value wider than the runtime's atomic access can
    // be observed half written unless it is read under the same lock, whether or not interceptors
    // sit in front of the read.
    private static TProperty ReadUnderLock(ref PropertyReadContext<TProperty> context, Func<IInterceptorSubject, TProperty> innerReadValue)
    {
        lock (context.Property.Subject.SyncRoot)
        {
            return innerReadValue(context.Property.Subject);
        }
    }
}
