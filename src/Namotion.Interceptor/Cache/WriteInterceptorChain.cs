using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Cache;

internal sealed class WriteInterceptorChain<TProperty>
{
    public delegate TProperty ExecuteTerminalFunc(ref PropertyWriteContext<TProperty> context, Action<IInterceptorSubject, TProperty> terminal);

    private readonly ImmutableArray<IWriteInterceptor> _interceptors;
    private readonly ExecuteTerminalFunc _executeTerminal;
    private readonly WriteContinuationNode[] _continuations;

    // Using thread static per generic type instantiation, one per TProperty type,
    // this is required to make this is thread-safe
    [ThreadStatic]
    // ReSharper disable once StaticMemberInGenericType
    private static Action<IInterceptorSubject, TProperty>? _threadLocalTerminal;

    public WriteInterceptorChain(
        ImmutableArray<IWriteInterceptor> interceptors,
        ExecuteTerminalFunc executeTerminal)
    {
        _interceptors = interceptors;
        _executeTerminal = executeTerminal;

        _continuations = new WriteContinuationNode[interceptors.Length];
        for (var i = 0; i < interceptors.Length; i++)
        {
            _continuations[i] = new WriteContinuationNode(this, i + 1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Execute(ref PropertyWriteContext<TProperty> context, Action<IInterceptorSubject, TProperty> terminal)
    {
        _threadLocalTerminal = terminal;
        ExecuteAtIndex(0, ref context);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ExecuteAtIndex(int index, ref PropertyWriteContext<TProperty> context)
    {
        if (index >= _interceptors.Length)
        {
            _executeTerminal(ref context, _threadLocalTerminal!);
            return;
        }

        var interceptor = _interceptors[index];
        var continuation = _continuations[index];

        interceptor.WriteProperty(ref context, continuation.ContinuationDelegate);
    }

    private sealed class WriteContinuationNode
    {
        private readonly WriteInterceptorChain<TProperty> _chain;
        private readonly int _nextIndex;

        public readonly WriteInterceptionDelegate<TProperty> ContinuationDelegate;

        public WriteContinuationNode(WriteInterceptorChain<TProperty> chain, int nextIndex)
        {
            _chain = chain;
            _nextIndex = nextIndex;
            ContinuationDelegate = ExecuteNext;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ExecuteNext(ref PropertyWriteContext<TProperty> context)
        {
            _chain.ExecuteAtIndex(_nextIndex, ref context);
        }
    }
}