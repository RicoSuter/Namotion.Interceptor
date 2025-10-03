using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Cache;

internal sealed class MethodInvocationChain<TInterceptor>
{
    public delegate object? ExecuteInterceptorFunc(TInterceptor interceptor, MethodInvocationContext context, InvokeMethodInterceptionDelegate next);

    public delegate object? ExecuteTerminalFunc(ref MethodInvocationContext context, Func<IInterceptorSubject, object?[], object?> terminal);

    private readonly TInterceptor[] _interceptors;
    private readonly ExecuteInterceptorFunc _executeInterceptor;
    private readonly ExecuteTerminalFunc _executeTerminal;
    private readonly InvocationContinuationNode[] _continuations;

    [ThreadStatic]
    private static Func<IInterceptorSubject, object?[], object?>? _threadLocalTerminal;

    public MethodInvocationChain(
        TInterceptor[] interceptors,
        ExecuteInterceptorFunc executeInterceptor,
        ExecuteTerminalFunc executeTerminal)
    {
        _interceptors = interceptors;
        _executeInterceptor = executeInterceptor;
        _executeTerminal = executeTerminal;

        _continuations = new InvocationContinuationNode[interceptors.Length];
        for (var i = 0; i < interceptors.Length; i++)
        {
            _continuations[i] = new InvocationContinuationNode(this, i + 1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public object? Execute(ref MethodInvocationContext context, Func<IInterceptorSubject, object?[], object?> terminal)
    {
        _threadLocalTerminal = terminal;
        return ExecuteAtIndex(0, ref context);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object? ExecuteAtIndex(int index, ref MethodInvocationContext context)
    {
        if (index >= _interceptors.Length)
        {
            return _executeTerminal(ref context, _threadLocalTerminal!);
        }

        var interceptor = _interceptors[index];
        var continuation = _continuations[index];

        return _executeInterceptor(interceptor, context, continuation.ContinuationDelegate);
    }

    private sealed class InvocationContinuationNode
    {
        private readonly MethodInvocationChain<TInterceptor> _chain;
        private readonly int _nextIndex;

        public readonly InvokeMethodInterceptionDelegate ContinuationDelegate;

        public InvocationContinuationNode(MethodInvocationChain<TInterceptor> chain, int nextIndex)
        {
            _chain = chain;
            _nextIndex = nextIndex;
            ContinuationDelegate = ExecuteNext;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private object? ExecuteNext(ref MethodInvocationContext context)
        {
            return _chain.ExecuteAtIndex(_nextIndex, ref context);
        }
    }
}