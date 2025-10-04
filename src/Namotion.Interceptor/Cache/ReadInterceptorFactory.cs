using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Cache;

internal static class ReadInterceptorFactory<TProperty>
{
    public static ReadFunc<TProperty> Create(IEnumerable<IReadInterceptor> interceptors)
    {
        var interceptorArray = interceptors.ToArray();
        if (interceptorArray.Length == 0)
        {
            return static (ref interception, innerReadValue) => innerReadValue(interception.Property.Subject);
        }

        var chain = new ReadInterceptorChain<IReadInterceptor, TProperty>(
            interceptorArray,
            static (interceptor, ref interception, next) => interceptor.ReadProperty(ref interception, next),
            static (ref interception, innerReadValue) => innerReadValue(interception.Property.Subject)
        );
        return chain.Execute;
    }
}