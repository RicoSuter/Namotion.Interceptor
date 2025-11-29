using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Cache;

internal sealed class WriteInterceptorChain<TInterceptor, TProperty>
{
    public delegate TProperty ExecuteTerminalFunc(ref PropertyWriteContext<TProperty> context, Action<IInterceptorSubject, TProperty> terminal);

    public delegate void ExecuteInterceptorAction(TInterceptor interceptor, ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> @delegate);

    private readonly ImmutableArray<TInterceptor> _interceptors;
    private readonly ExecuteInterceptorAction _executeInterceptor;
    private readonly ExecuteTerminalFunc _executeTerminal;
    private readonly WriteContinuationNode[] _continuations;

    // Using thread static per generic type instantiation, one per TProperty type,
    // this is required to make this is thread-safe
    [ThreadStatic]
    // ReSharper disable once StaticMemberInGenericType
    private static Action<IInterceptorSubject, TProperty>? _threadLocalTerminal;

    public WriteInterceptorChain(
        ImmutableArray<TInterceptor> interceptors,
        ExecuteInterceptorAction executeInterceptor,
        ExecuteTerminalFunc executeTerminal)
    {
        _interceptors = interceptors;
        _executeInterceptor = executeInterceptor;
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

        _executeInterceptor(interceptor, ref context, continuation.ContinuationDelegate);
    }

    private sealed class WriteContinuationNode
    {
        private readonly WriteInterceptorChain<TInterceptor, TProperty> _chain;
        private readonly int _nextIndex;

        public readonly WriteInterceptionDelegate<TProperty> ContinuationDelegate;

        public WriteContinuationNode(WriteInterceptorChain<TInterceptor, TProperty> chain, int nextIndex)
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