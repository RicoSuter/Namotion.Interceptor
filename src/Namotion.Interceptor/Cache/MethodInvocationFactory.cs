using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Cache;

internal static class MethodInvocationFactory
{
    public static InvokeFunc Create(ImmutableArray<IMethodInterceptor> interceptors)
    {
        if (interceptors.Length == 0)
        {
            return static (ref context, innerInvokeMethod) => innerInvokeMethod(context.Subject, context.Parameters);
        }

        var chain = new MethodInvocationChain<IMethodInterceptor>(
            interceptors,
            static (interceptor, context, next) => interceptor.InvokeMethod(context, next),
            static (ref context, innerInvokeMethod) => innerInvokeMethod(context.Subject, context.Parameters)
        );
        return chain.Execute;
    }
}