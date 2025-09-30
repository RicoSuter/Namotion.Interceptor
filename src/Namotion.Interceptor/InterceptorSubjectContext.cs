using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor;

public class InterceptorSubjectContext : IInterceptorSubjectContext
{
    private ConcurrentDictionary<Type, Delegate>? _readInterceptorFunction;
    private ConcurrentDictionary<Type, Delegate>? _writeInterceptorFunction;
    private ConcurrentDictionary<Type, IEnumerable>? _serviceCache;

    private readonly HashSet<object> _services = []; // TODO(perf): Keep null initially?
    private readonly HashSet<InterceptorSubjectContext> _usedByContexts = [];
    private readonly HashSet<InterceptorSubjectContext> _fallbackContexts = [];

    private InterceptorSubjectContext? _noServicesSingleFallbackContext;
    
    public static InterceptorSubjectContext Create()
    {
        return new InterceptorSubjectContext();
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

        EnsureInitialized();
        if (!_serviceCache!.TryGetValue(typeof(TInterface), out var services))
        {
            services = _serviceCache!.GetOrAdd(typeof(TInterface), _ =>
            {
                lock (this)
                {
                    return GetServicesWithoutCache<TInterface>().ToArray();
                }
            });
        }
        
        return (TInterface[])services;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureInitialized()
    {
        if (_serviceCache is null)
        {
            lock (this)
            {
                _serviceCache = new ConcurrentDictionary<Type, IEnumerable>();
                _readInterceptorFunction = new ConcurrentDictionary<Type, Delegate>();
                _writeInterceptorFunction = new ConcurrentDictionary<Type, Delegate>();
            }
        }
    }

    public virtual bool AddFallbackContext(IInterceptorSubjectContext context)
    {
        var contextImpl = (InterceptorSubjectContext)context;
        lock (this)
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
        lock (this)
            return _fallbackContexts.Contains(context);
    }

    public virtual bool RemoveFallbackContext(IInterceptorSubjectContext context)
    {
        var contextImpl = (InterceptorSubjectContext)context;
        lock (this)
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
        lock (this)
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
        lock (this)
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

    public TProperty ExecuteInterceptedRead<TProperty>(ref PropertyReadContext context, Func<IInterceptorSubject, TProperty> readValue)
    {
        var noServicesSingleFallbackContext = _noServicesSingleFallbackContext;
        if (noServicesSingleFallbackContext is not null)
        {
            return noServicesSingleFallbackContext.ExecuteInterceptedRead(ref context, readValue);
        }

        EnsureInitialized();
        var func = GetReadInterceptorFunction<TProperty>();
        return func(ref context, readValue);
    }

    public void ExecuteInterceptedWrite<TProperty>(ref PropertyWriteContext<TProperty> context, Action<IInterceptorSubject, TProperty> writeValue)
    {
        var noServicesSingleFallbackContext = _noServicesSingleFallbackContext;
        if (noServicesSingleFallbackContext is not null)
        {
            noServicesSingleFallbackContext.ExecuteInterceptedWrite(ref context, writeValue);
            return;
        }
        
        EnsureInitialized();
        var action = GetWriteInterceptorFunction<TProperty>();
        action(ref context, writeValue);
    }

    // public object? InvokeMethod(IInterceptorSubject subject, string methodName, object?[] parameters, Func<object?[], object?> invokeMethod)
    // {
    //     var methodInterceptors = GetServices<IMethodInterceptor>();
    //
    //     var returnInvokeMethod = new InvokeMethodInterceptionDelegate((ref context) => invokeMethod(context.Parameters));
    //     foreach (var handler in methodInterceptors)
    //     {
    //         var previousInvokeMethod = returnInvokeMethod;
    //         returnInvokeMethod = (ref innerContext) =>
    //         {
    //             return handler.InvokeMethod(innerContext,
    //                 (ref innerInnerContext) => previousInvokeMethod(ref innerInnerContext));
    //         };
    //     }
    //
    //     var context = new MethodInvocationContext(subject, methodName, parameters); 
    //     return returnInvokeMethod(ref context);
    // }
    
    delegate TProperty ReadFunc<TProperty>(ref PropertyReadContext context, Func<IInterceptorSubject, TProperty> func);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadFunc<TProperty> GetReadInterceptorFunction<TProperty>()
    {
        if (_readInterceptorFunction!.TryGetValue(typeof(TProperty), out var cached))
        {
            return (ReadFunc<TProperty>)cached;
        }

        var readInterceptors = GetServices<IReadInterceptor>();
        var func = ReadInterceptorChain<TProperty>.Create(readInterceptors);
        _readInterceptorFunction.TryAdd(typeof(TProperty), func);
        return func;
    }

    delegate void WriteAction<TProperty>(ref PropertyWriteContext<TProperty> context, Action<IInterceptorSubject, TProperty> action);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private WriteAction<TProperty> GetWriteInterceptorFunction<TProperty>()
    {
        if (_writeInterceptorFunction!.TryGetValue(typeof(TProperty), out var cached))
        {
            return (WriteAction<TProperty>)cached;
        }

        var writeInterceptors = GetServices<IWriteInterceptor>();
        var action = WriteInterceptorChain<TProperty>.Create(writeInterceptors);
        _writeInterceptorFunction.TryAdd(typeof(TProperty), action);
        return action;
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
        _serviceCache?.Clear();
        _readInterceptorFunction?.Clear();
        _writeInterceptorFunction?.Clear();

        _noServicesSingleFallbackContext = _services.Count == 0 && _fallbackContexts.Count == 1 
            ? _fallbackContexts.Single() : null;

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
                return static (ref interception, innerReadValue) => innerReadValue(interception.Property.Subject);
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
                return static (ref interception, innerWriteValue) =>
                {
                    innerWriteValue(interception.Property.Subject, interception.NewValue);
                };
            }

            var chain = new WriteInterceptorChain<IWriteInterceptor, TProperty>(
                interceptorArray,
                static (interceptor, ref context, next) => interceptor.WriteProperty(ref context, next),
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
        public delegate TProperty ExecuteTerminalFunc(ref PropertyWriteContext<TProperty> context, object obj);
        public delegate void ExecuteInterceptorAction(TInterceptor interceptor, ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> @delegate);
        
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
        public void Execute(ref PropertyWriteContext<TProperty> context, object terminal)
        {
            ExecuteAtIndex(0, ref context, terminal);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ExecuteAtIndex(int index, ref PropertyWriteContext<TProperty> context, object terminal)
        {
            if (index >= _interceptors.Length)
            {
                _executeTerminal(ref context, terminal);
                return;
            }

            var interceptor = _interceptors[index];
            var continuation = _continuations[index];
            
            continuation.SetState(terminal);
            _executeInterceptor(interceptor, ref context, continuation.ContinuationDelegate);
        }

        private sealed class WriteContinuationNode
        {
            private readonly WriteInterceptorChain<TInterceptor, TProperty> _chain;
            private readonly int _nextIndex;
            private object _currentTerminal = null!;

            public readonly WriteInterceptionDelegate<TProperty> ContinuationDelegate;

            public WriteContinuationNode(WriteInterceptorChain<TInterceptor, TProperty> chain, int nextIndex)
            {
                _chain = chain;
                _nextIndex = nextIndex;

                ContinuationDelegate = ExecuteNext;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void SetState(object terminal)
            {
                _currentTerminal = terminal;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void ExecuteNext(ref PropertyWriteContext<TProperty> context)
            {
                _chain.ExecuteAtIndex(_nextIndex, ref context, _currentTerminal);
            }
        }
    }

    private sealed class ReadInterceptorChain<TInterceptor, TProperty>
    {
        public delegate TProperty ExecuteInterceptorFunc(TInterceptor interceptor, ref PropertyReadContext context, ReadInterceptionDelegate<TProperty> a);
        public delegate TProperty ReadInterceptionFunc(ref PropertyReadContext context, object obj);
        
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
        public TProperty Execute(ref PropertyReadContext context, object terminal)
        {
            return ExecuteAtIndex(0, ref context, terminal);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private TProperty ExecuteAtIndex(int index, ref PropertyReadContext context, object terminal)
        {
            if (index >= _interceptors.Length)
            {
                return _executeTerminal(ref context, terminal);
            }

            var interceptor = _interceptors[index];
            var continuation = _continuations[index];
            
            continuation.SetState(terminal);
            return _executeInterceptor(interceptor, ref context, continuation.ContinuationDelegate);
        }

        private sealed class ContinuationNode
        {
            private readonly ReadInterceptorChain<TInterceptor, TProperty> _chain;
            private readonly int _nextIndex;
            private object _currentTerminal = null!;

            public readonly ReadInterceptionDelegate<TProperty> ContinuationDelegate;

            public ContinuationNode(ReadInterceptorChain<TInterceptor, TProperty> chain, int nextIndex)
            {
                _chain = chain;
                _nextIndex = nextIndex;

                ContinuationDelegate = ExecuteNext;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void SetState(object terminal)
            {
                _currentTerminal = terminal;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private TProperty ExecuteNext(ref PropertyReadContext context)
            {
                return _chain.ExecuteAtIndex(_nextIndex, ref context, _currentTerminal);
            }
        }
    }
}