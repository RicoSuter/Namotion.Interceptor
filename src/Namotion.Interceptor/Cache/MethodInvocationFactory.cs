using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Cache;

internal static class MethodInvocationFactory
{
    public static InvokeFunc Create(IEnumerable<IMethodInterceptor> interceptors)
    {
        var interceptorArray = interceptors.ToArray();
        if (interceptorArray.Length == 0)
        {
            return static (ref context, innerInvokeMethod) => innerInvokeMethod(context.Subject, context.Parameters);
        }

        var chain = new MethodInvocationChain<IMethodInterceptor>(
            interceptorArray,
            static (interceptor, context, next) => interceptor.InvokeMethod(context, next),
            static (ref context, innerInvokeMethod) => ((Func<IInterceptorSubject, object?[], object?>)innerInvokeMethod)(context.Subject, context.Parameters)
        );
        return chain.Execute;
    }
}