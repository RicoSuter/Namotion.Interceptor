using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Cache;

internal static class ReadInterceptorFactory<TProperty>
{
    public static ReadFunc<TProperty> Create(ImmutableArray<IReadInterceptor> interceptors)
    {
        // The lock guards a single field access and is never held across two properties, so it has
        // never given a reader any ordering. Atomicity is all it adds, and the runtime already
        // guarantees that for references and word-sized primitives, so those skip the Monitor,
        // which is otherwise the largest cost of an intercepted read.
        //
        // That holds only while the terminal really is one field access of TProperty, which is what
        // generated code passes. Dynamic registry properties and the dynamic proxy dispatch with
        // TProperty widened to object and a terminal that reads the declared type and boxes it, so
        // an object read can still copy a wide struct and has to keep the lock.
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
