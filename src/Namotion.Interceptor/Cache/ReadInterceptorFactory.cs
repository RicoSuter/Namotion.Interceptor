using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Cache;

internal static class ReadInterceptorFactory<TProperty>
{
    public static ReadFunc<TProperty> Create(ImmutableArray<IReadInterceptor> interceptors)
    {
        if (interceptors.Length == 0)
        {
            return static (ref interception, innerReadValue) => innerReadValue(interception.Property.Subject);
        }

        var chain = new ReadInterceptorChain<IReadInterceptor, TProperty>(
            interceptors,
            static (interceptor, ref interception, next) => interceptor.ReadProperty(ref interception, next),
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