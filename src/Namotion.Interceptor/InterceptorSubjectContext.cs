using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor;

public class InterceptorSubjectContext : IInterceptorSubjectContext
{
    // TODO(perf): Do not initialize these dictionaries until they are needed
    private readonly ConcurrentDictionary<Type, Delegate> _readInterceptorFunction = new();
    private readonly ConcurrentDictionary<Type, Delegate> _writeInterceptorFunction = new();
    private readonly ConcurrentDictionary<Type, IEnumerable> _serviceCache = new();

    private readonly HashSet<object> _services = [];

    private readonly HashSet<InterceptorSubjectContext> _usedByContexts = [];
    private readonly HashSet<InterceptorSubjectContext> _fallbackContexts = [];
    private InterceptorSubjectContext? _noServicesSingleFallbackContext;
    
    public static InterceptorSubjectContext Create()
    {
        return new InterceptorSubjectContext();
    }

    private void ResetInterceptorFunctions()
    { 
        var noServicesSingleFallbackContext = _noServicesSingleFallbackContext;
        if (noServicesSingleFallbackContext is not null)
        {
            return;
        }
        
        _readInterceptorFunction.Clear();
        _writeInterceptorFunction.Clear();
    }

    public TProperty ExecuteInterceptedRead<TProperty>(ref ReadPropertyInterception interception, Func<IInterceptorSubject, TProperty> readValue)
    {
        var noServicesSingleFallbackContext = _noServicesSingleFallbackContext;
        if (noServicesSingleFallbackContext is not null)
        {
            return noServicesSingleFallbackContext.ExecuteInterceptedRead(ref interception, readValue);
        }

        var func = GetReadInterceptorFunction<TProperty>();
        return func(ref interception, readValue);
    }

    public void ExecuteInterceptedWrite<TProperty>(ref WritePropertyInterception<TProperty> interception, Action<IInterceptorSubject, TProperty> writeValue)
    {
        var noServicesSingleFallbackContext = _noServicesSingleFallbackContext;
        if (noServicesSingleFallbackContext is not null)
        {
            noServicesSingleFallbackContext.ExecuteInterceptedWrite(ref interception, writeValue);
            return;
        }
        
        var action = GetWriteInterceptorFunction<TProperty>();
        action(ref interception, writeValue);
    }
    
    delegate TProperty ReadFunc<TProperty>(ref ReadPropertyInterception interception, Func<IInterceptorSubject, TProperty> func);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadFunc<TProperty> GetReadInterceptorFunction<TProperty>()
    {
        if (_readInterceptorFunction.TryGetValue(typeof(TProperty), out var cached))
        {
            return (ReadFunc<TProperty>)cached;
        }

        var readInterceptors = GetServices<IReadInterceptor>();
        var func = ReadInterceptorChain<TProperty>.Create(readInterceptors);
        _readInterceptorFunction.TryAdd(typeof(TProperty), func);
        return func;
    }

    delegate void WriteAction<TProperty>(ref WritePropertyInterception<TProperty> interception, Action<IInterceptorSubject, TProperty> action);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private WriteAction<TProperty> GetWriteInterceptorFunction<TProperty>()
    {
        if (_writeInterceptorFunction.TryGetValue(typeof(TProperty), out var cached))
        {
            return (WriteAction<TProperty>)cached;
        }

        var writeInterceptors = GetServices<IWriteInterceptor>();
        var action = WriteInterceptorChain<TProperty>.Create(writeInterceptors);
        _writeInterceptorFunction.TryAdd(typeof(TProperty), action);
        return action;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IEnumerable<TInterface> GetServices<TInterface>()
    {
        // When there is only a fallback context and no services then we do not
        // need to create an own cache and waste time creating and maintaining it.
        // We can just redirect the call to the fallback context.
        var noServicesSingleFallbackContext = _noServicesSingleFallbackContext;
        if (noServicesSingleFallbackContext is not null)
        {
            return noServicesSingleFallbackContext.GetServices<TInterface>();
        }

        var services = _serviceCache.GetOrAdd(
            typeof(TInterface), _ =>
            {
                lock (_services)
                    return GetServicesWithoutCache<TInterface>().ToArray();
            });

        return (TInterface[])services;
    }

    public virtual bool AddFallbackContext(IInterceptorSubjectContext context)
    {
        var contextImpl = (InterceptorSubjectContext)context;
        lock (_services)
        {
            if (_fallbackContexts.Add(contextImpl))
            {
                contextImpl._usedByContexts.Add(this);
                OnContextChanged();
                return true;
            }

            return false;
        }
    }

    protected bool HasFallbackContext(IInterceptorSubjectContext context)
    {
        lock (_services)
            return _fallbackContexts.Contains(context);
    }

    public virtual bool RemoveFallbackContext(IInterceptorSubjectContext context)
    {
        var contextImpl = (InterceptorSubjectContext)context;
        lock (_services)
        {
            if (_fallbackContexts.Remove(contextImpl))
            {
                contextImpl._usedByContexts.Remove(this);
                OnContextChanged();
                return true;
            }

            return false;
        }
    }

    public bool TryAddService<TService>(Func<TService> factory, Func<TService, bool> exists)
    {
        lock (_services)
        {
            if (GetServicesWithoutCache<TService>().Any(exists))
            {
                return false;
            }

            AddService(factory());
        }

        return true;
    }

    public void AddService<TService>(TService service)
    {
        lock (_services)
        {
            _services.Add(service!);
            OnContextChanged();
        }
    }

    public TInterface? TryGetService<TInterface>()
    {
        var services = (TInterface[])GetServices<TInterface>();
        var length = services.Length;
        return length switch
        {
            1 => services[0],
            0 => default,
            _ => throw new InvalidOperationException($"There must be exactly one service of type {typeof(TInterface).FullName}.")
        };
    }

    private IEnumerable<TInterface> GetServicesWithoutCache<TInterface>()
    {
        return _services
            .OfType<TInterface>()
            .Concat(_fallbackContexts.SelectMany(c => c.GetServices<TInterface>()))
            .Distinct();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void OnContextChanged()
    {
        _serviceCache.Clear();
        _noServicesSingleFallbackContext = _services.Count == 0 && _fallbackContexts.Count == 1 
            ? _fallbackContexts.Single() : null;

        ResetInterceptorFunctions();
        foreach (var parent in _usedByContexts)
        {
            parent.OnContextChanged();
        }
    }

    private static class ReadInterceptorChain<TProperty>
    {
        public static ReadFunc<TProperty> Create(IEnumerable<IReadInterceptor> interceptors)
        {
            var interceptorArray = interceptors.ToArray();
            if (interceptorArray.Length == 0)
            {
                return (ref interception, innerReadValue) => innerReadValue(interception.Property.Subject);
            }

            var chain = new ReadInterceptorChain<IReadInterceptor, TProperty>(
                interceptorArray,
                static (interceptor, ref interception, next) => interceptor.ReadProperty(ref interception, next),
                static (ref interception, innerReadValue) => ((Func<IInterceptorSubject, TProperty>)innerReadValue)(interception.Property.Subject)
            );
            return chain.Execute;
        }
    }

    private static class WriteInterceptorChain<TProperty>
    {
        public static WriteAction<TProperty> Create(IEnumerable<IWriteInterceptor> interceptors)
        {
            var interceptorArray = interceptors.ToArray();
            if (interceptorArray.Length == 0)
            {
                return (ref interception, innerWriteValue) =>
                {
                    innerWriteValue(interception.Property.Subject, interception.NewValue);
                };
            }

            var chain = new WriteInterceptorChain<IWriteInterceptor, TProperty>(
                interceptorArray,
                static (interceptor, ref interception, next) => interceptor.WriteProperty(ref interception, next),
                static (ref interception, innerWriteValue) =>
                {
                    var writeAction = (Action<IInterceptorSubject, TProperty>)innerWriteValue;
                    writeAction(interception.Property.Subject, interception.NewValue);
                    return interception.NewValue;
                }
            );
            return chain.Execute;
        }
    }

    private sealed class WriteInterceptorChain<TInterceptor, TProperty>
    {
        public delegate TProperty ExecuteTerminalFunc(ref WritePropertyInterception<TProperty> interception, object obj);
        public delegate void ExecuteInterceptorAction(TInterceptor interceptor, ref WritePropertyInterception<TProperty> interception, WriteInterceptionAction<TProperty> action);
        
        private readonly TInterceptor[] _interceptors;
        private readonly ExecuteInterceptorAction _executeInterceptor;
        private readonly ExecuteTerminalFunc _executeTerminal;
        private readonly WriteContinuationNode[] _continuations;

        public WriteInterceptorChain(
            TInterceptor[] interceptors,
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
        public void Execute(ref WritePropertyInterception<TProperty> interception, object terminal)
        {
            ExecuteAtIndex(0, ref interception, terminal);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ExecuteAtIndex(int index, ref WritePropertyInterception<TProperty> interception, object terminal)
        {
            if (index >= _interceptors.Length)
            {
                _executeTerminal(ref interception, terminal);
                return;
            }

            var interceptor = _interceptors[index];
            var continuation = _continuations[index];
            
            continuation.SetState(terminal);
            _executeInterceptor(interceptor, ref interception, continuation.ContinuationAction);
        }

        private sealed class WriteContinuationNode
        {
            private readonly WriteInterceptorChain<TInterceptor, TProperty> _chain;
            private readonly int _nextIndex;
            private object _currentTerminal = null!;

            public readonly WriteInterceptionAction<TProperty> ContinuationAction;

            public WriteContinuationNode(WriteInterceptorChain<TInterceptor, TProperty> chain, int nextIndex)
            {
                _chain = chain;
                _nextIndex = nextIndex;

                ContinuationAction = ExecuteNext;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void SetState(object terminal)
            {
                _currentTerminal = terminal;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void ExecuteNext(ref WritePropertyInterception<TProperty> interception)
            {
                _chain.ExecuteAtIndex(_nextIndex, ref interception, _currentTerminal);
            }
        }
    }

    private sealed class ReadInterceptorChain<TInterceptor, TProperty>
    {
        public delegate TProperty ExecuteInterceptorFunc(TInterceptor interceptor, ref ReadPropertyInterception context, ReadInterceptionFunc<TProperty> a);
        public delegate TProperty ReadInterceptionFunc(ref ReadPropertyInterception interception, object obj);
        
        private readonly TInterceptor[] _interceptors;
        private readonly ExecuteInterceptorFunc _executeInterceptor;
        private readonly ReadInterceptionFunc _executeTerminal;
        private readonly ContinuationNode[] _continuations;

        public ReadInterceptorChain(
            TInterceptor[] interceptors,
            ExecuteInterceptorFunc executeInterceptor,
            ReadInterceptionFunc executeTerminal)
        {
            _interceptors = interceptors;
            _executeInterceptor = executeInterceptor;
            _executeTerminal = executeTerminal;
            
            _continuations = new ContinuationNode[interceptors.Length];
            for (var i = 0; i < interceptors.Length; i++)
            {
                _continuations[i] = new ContinuationNode(this, i + 1);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TProperty Execute(ref ReadPropertyInterception interception, object terminal)
        {
            return ExecuteAtIndex(0, ref interception, terminal);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private TProperty ExecuteAtIndex(int index, ref ReadPropertyInterception interception, object terminal)
        {
            if (index >= _interceptors.Length)
            {
                return _executeTerminal(ref interception, terminal);
            }

            var interceptor = _interceptors[index];
            var continuation = _continuations[index];
            
            continuation.SetState(terminal);
            return _executeInterceptor(interceptor, ref interception, continuation.ContinuationFunc);
        }

        private sealed class ContinuationNode
        {
            private readonly ReadInterceptorChain<TInterceptor, TProperty> _chain;
            private readonly int _nextIndex;
            private object _currentTerminal = null!;

            public readonly ReadInterceptionFunc<TProperty> ContinuationFunc;

            public ContinuationNode(ReadInterceptorChain<TInterceptor, TProperty> chain, int nextIndex)
            {
                _chain = chain;
                _nextIndex = nextIndex;

                ContinuationFunc = ExecuteNext;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void SetState(object terminal)
            {
                _currentTerminal = terminal;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private TProperty ExecuteNext(ref ReadPropertyInterception interception)
            {
                return _chain.ExecuteAtIndex(_nextIndex, ref interception, _currentTerminal);
            }
        }
    }
}