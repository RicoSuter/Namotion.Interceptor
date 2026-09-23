using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Cache;

internal static class ReadInterceptorFactory<TProperty>
{
    public static ReadFunc<TProperty> Create(ImmutableArray<IReadInterceptor> interceptors)
    {
        // Reads lock like writes, so a value wider than atomic access cannot tear. Static lambdas, not
        // method groups: a delegate over a static method is invoked through a shuffle thunk.
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
