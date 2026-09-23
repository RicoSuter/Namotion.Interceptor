using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Cache;

internal static class ReadInterceptorFactory<TProperty>
{
    public static ReadFunc<TProperty> Create(ImmutableArray<IReadInterceptor> interceptors)
    {
        // The write terminal commits under SyncRoot. A value wider than the runtime's atomic access can
        // be observed half written unless it is read under the same lock, whether or not interceptors
        // sit in front of the read.
        //
        // The terminals are static lambdas with the lock body duplicated on purpose: a delegate over a
        // static method is invoked through an argument shuffle thunk, and a shared body holding a lock
        // is not inlined into the lambdas.
        if (interceptors.Length == 0)
        {
            return static (ref PropertyReadContext<TProperty> context, Func<IInterceptorSubject, TProperty> innerReadValue) =>
            {
                lock (context.Property.Subject.SyncRoot)
                {
                    return innerReadValue(context.Property.Subject);
                }
            };
        }

        var chain = new ReadInterceptorChain<TProperty>(
            interceptors,
            static (ref context, innerReadValue) =>
            {
                lock (context.Property.Subject.SyncRoot)
                {
                    return innerReadValue(context.Property.Subject);
                }
            });
        return chain.Execute;
    }
}
