using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Cache;

internal static class ReadInterceptorFactory<TProperty>
{
    public static ReadFunc<TProperty> Create(ImmutableArray<IReadInterceptor> interceptors)
    {
        // Types the runtime loads atomically skip the lock; object keeps it, since a registry getter can box a
        // wide struct. Static lambdas, not method groups: a delegate over a static method goes through a shuffle thunk.
        ReadFunc<TProperty> terminal =
            typeof(TProperty) != typeof(object) && AtomicAccess.IsGuaranteedFor(typeof(TProperty))
                ? static (ref context, innerReadValue) => innerReadValue(context.Property.Subject)
                : static (ref context, innerReadValue) =>
                {
                    lock (context.Property.Subject.SyncRoot)
                    {
                        return innerReadValue(context.Property.Subject);
                    }
                };

        return interceptors.Length == 0
            ? terminal
            : new ReadInterceptorChain<TProperty>(interceptors, terminal).Execute;
    }
}
