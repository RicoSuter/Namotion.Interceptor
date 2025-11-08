using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Cache;

internal delegate TProperty ReadFunc<TProperty>(ref PropertyReadContext context, Func<IInterceptorSubject, TProperty> func);
internal delegate void WriteAction<TProperty>(ref PropertyWriteContext<TProperty> context, Action<IInterceptorSubject, TProperty> action);
internal delegate object? InvokeFunc(ref MethodInvocationContext context, Func<IInterceptorSubject, object?[], object?> func);