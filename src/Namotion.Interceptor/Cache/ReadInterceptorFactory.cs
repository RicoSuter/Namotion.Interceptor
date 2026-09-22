using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Cache;

internal static class ReadInterceptorFactory<TProperty>
{
    public static ReadFunc<TProperty> Create(ImmutableArray<IReadInterceptor> interceptors)
    {
        if (interceptors.Length == 0)
        {
            return ReadUnderLock;
        }

        var chain = new ReadInterceptorChain<TProperty>(interceptors, ReadUnderLock);
        return chain.Execute;
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
