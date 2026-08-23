using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Cache;

internal static class ReadInterceptorFactory<TProperty>
{
    public static ReadFunc<TProperty> Create(ImmutableArray<IReadInterceptor> interceptors)
    {
        // The #if DEBUG bodies differ only by the GetterWriteGuard marking around the inner
        // reader, which is what detects a getter writing a subject-typed property. The guard
        // needs a finally (getters may throw), and an exception region would change the Release
        // codegen of this hot path, so Release keeps the exact unguarded shape.
        if (interceptors.Length == 0)
        {
#if DEBUG
            return static (ref PropertyReadContext<TProperty> context, Func<IInterceptorSubject, TProperty> innerReadValue) =>
            {
                GetterWriteGuard.EnterGetter();
                try
                {
                    return innerReadValue(context.Property.Subject);
                }
                finally
                {
                    GetterWriteGuard.ExitGetter();
                }
            };
#else
            return static (ref PropertyReadContext<TProperty> context, Func<IInterceptorSubject, TProperty> innerReadValue) => innerReadValue(context.Property.Subject);
#endif
        }

        var chain = new ReadInterceptorChain<TProperty>(
            interceptors,
#if DEBUG
            static (ref context, innerReadValue) =>
            {
                lock (context.Property.Subject.SyncRoot)
                {
                    GetterWriteGuard.EnterGetter();
                    try
                    {
                        return innerReadValue(context.Property.Subject);
                    }
                    finally
                    {
                        GetterWriteGuard.ExitGetter();
                    }
                }
            });
#else
            static (ref context, innerReadValue) =>
            {
                lock (context.Property.Subject.SyncRoot)
                {
                    return innerReadValue(context.Property.Subject);
                }
            });
#endif
        return chain.Execute;
    }
}
