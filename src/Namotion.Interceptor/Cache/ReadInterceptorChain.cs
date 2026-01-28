using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Cache;

internal sealed class ReadInterceptorChain<TProperty>
{
    public delegate TProperty ReadInterceptionFunc(ref PropertyReadContext context, Func<IInterceptorSubject, TProperty> terminal);

    private readonly ImmutableArray<IReadInterceptor> _interceptors;
    private readonly ReadInterceptionFunc _executeTerminal;
    private readonly ContinuationNode[] _continuations;

    [ThreadStatic]
    // ReSharper disable once StaticMemberInGenericType
    private static Func<IInterceptorSubject, TProperty>? _threadLocalTerminal;

    public ReadInterceptorChain(
        ImmutableArray<IReadInterceptor> interceptors,
        ReadInterceptionFunc executeTerminal)
    {
        _interceptors = interceptors;
        _executeTerminal = executeTerminal;

        _continuations = new ContinuationNode[interceptors.Length];
        for (var i = 0; i < interceptors.Length; i++)
        {
            _continuations[i] = new ContinuationNode(this, i + 1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TProperty Execute(ref PropertyReadContext context, Func<IInterceptorSubject, TProperty> terminal)
    {
        _threadLocalTerminal = terminal;
        return ExecuteAtIndex(0, ref context);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private TProperty ExecuteAtIndex(int index, ref PropertyReadContext context)
    {
        if (index >= _interceptors.Length)
        {
            return _executeTerminal(ref context, _threadLocalTerminal!);
        }

        var interceptor = _interceptors[index];
        var continuation = _continuations[index];

        return interceptor.ReadProperty(ref context, continuation.ContinuationDelegate);
    }

    private sealed class ContinuationNode
    {
        private readonly ReadInterceptorChain<TProperty> _chain;
        private readonly int _nextIndex;

        public readonly ReadInterceptionDelegate<TProperty> ContinuationDelegate;

        public ContinuationNode(ReadInterceptorChain<TProperty> chain, int nextIndex)
        {
            _chain = chain;
            _nextIndex = nextIndex;

            ContinuationDelegate = ExecuteNext;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private TProperty ExecuteNext(ref PropertyReadContext context)
        {
            return _chain.ExecuteAtIndex(_nextIndex, ref context);
        }
    }
}