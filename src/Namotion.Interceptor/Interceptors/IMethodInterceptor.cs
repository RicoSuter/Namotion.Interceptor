namespace Namotion.Interceptor.Interceptors;

public interface IMethodInterceptor
{
    /// <summary>
    /// Intercepts a method invocation. Always forward the context you received to
    /// <paramref name="next"/>; a freshly constructed context loses the per-call state the chain
    /// threads through it (including the terminal invocation).
    /// </summary>
    object? InvokeMethod(MethodInvocationContext context, InvokeMethodInterceptionDelegate next);
}

public delegate object? InvokeMethodInterceptionDelegate(ref MethodInvocationContext context);

public struct MethodInvocationContext
{
    public IInterceptorSubject Subject { get; }

    public string MethodName { get; }

    public object?[] Parameters { get; }

    // The terminal invoke action for this call. Threaded through the per-call context instead of a
    // ThreadStatic on the shared chain instance: per-call state on the per-call context, robust
    // against reentrant invocations. It is set once before the chain runs and never mutated, so it
    // survives the by-value interceptor hops (each copy inherits the field) to reach the terminal.
    internal Func<IInterceptorSubject, object?[], object?>? Terminal;

    // Internal so every meaningfully constructed context comes from the library's execution entry
    // points, which always thread the per-call chain state (such as the terminal) through it.
    internal MethodInvocationContext(IInterceptorSubject subject, string methodName, object?[] parameters)
    {
        Subject = subject;
        MethodName = methodName;
        Parameters = parameters;
    }
}