using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Cache;

internal static class WriteInterceptorFactory<TProperty>
{
    public static WriteAction<TProperty> Create(IEnumerable<IWriteInterceptor> interceptors)
    {
        var interceptorArray = interceptors.ToArray();
        if (interceptorArray.Length == 0)
        {
            return static (ref interception, innerWriteValue) => innerWriteValue(interception.Property.Subject, interception.NewValue);
        }

        var chain = new WriteInterceptorChain<IWriteInterceptor, TProperty>(
            interceptorArray,
            static (interceptor, ref context, next) => interceptor.WriteProperty(ref context, next),
            static (ref interception, innerWriteValue) =>
            {
                innerWriteValue(interception.Property.Subject, interception.NewValue);
                return interception.NewValue;
            }
        );
        return chain.Execute;
    }
}