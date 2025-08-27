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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Func<ReadPropertyInterception, Func<IInterceptorSubject, TProperty>, TProperty> GetReadInterceptorFunction<TProperty>()
    {
        if (_readInterceptorFunction.TryGetValue(typeof(TProperty), out var cached))
        {
            return (Func<ReadPropertyInterception, Func<IInterceptorSubject, TProperty>, TProperty>)cached;
        }

        var readInterceptors = GetServices<IReadInterceptor>();
        var func = ReadInterceptorChain<TProperty>.Create(readInterceptors);
        _readInterceptorFunction.TryAdd(typeof(TProperty), func);
        return func;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Action<WritePropertyInterception<TProperty>, Action<IInterceptorSubject, TProperty>> GetWriteInterceptorFunction<TProperty>()
    {
        if (_writeInterceptorFunction.TryGetValue(typeof(TProperty), out var cached))
        {
            return (Action<WritePropertyInterception<TProperty>, Action<IInterceptorSubject, TProperty>>)cached;
        }

        var writeInterceptors = GetServices<IWriteInterceptor>();
        var func = WriteInterceptorChain<TProperty>.Create(writeInterceptors);
        _writeInterceptorFunction.TryAdd(typeof(TProperty), func);
        return func;
    }

    public TProperty ExecuteInterceptedRead<TProperty>(ReadPropertyInterception interception, Func<IInterceptorSubject, TProperty> readValue)
    {
        var noServicesSingleFallbackContext = _noServicesSingleFallbackContext;
        if (noServicesSingleFallbackContext is not null)
        {
            return noServicesSingleFallbackContext.ExecuteInterceptedRead(interception, readValue);
        }

        var func = GetReadInterceptorFunction<TProperty>();
        return func(interception, readValue);
    }

    public void ExecuteInterceptedWrite<TProperty>(WritePropertyInterception<TProperty> interception, Action<IInterceptorSubject, TProperty> writeValue)
    {
        var noServicesSingleFallbackContext = _noServicesSingleFallbackContext;
        if (noServicesSingleFallbackContext is not null)
        {
            noServicesSingleFallbackContext.ExecuteInterceptedWrite(interception, writeValue);
            return;
        }
        
        var func = GetWriteInterceptorFunction<TProperty>();
        func(interception, writeValue);
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
        public static Func<ReadPropertyInterception, Func<IInterceptorSubject, TProperty>, TProperty> Create(IEnumerable<IReadInterceptor> interceptors)
        {
            var interceptorArray = interceptors.ToArray();
            if (interceptorArray.Length == 0)
            {
                return (interception, innerReadValue) => innerReadValue(interception.Property.Subject);
            }

            var chain = new OptimizedInterceptorChain<ReadPropertyInterception, IReadInterceptor, TProperty>(
                interceptorArray,
                static (interceptor, context, next) => interceptor.ReadProperty(context, next),
                static (interception, innerReadValue) => ((Func<IInterceptorSubject, TProperty>)innerReadValue)(interception.Property.Subject)
            );
            return chain.Execute;
        }
    }

    private static class WriteInterceptorChain<TProperty>
    {
        public static Action<WritePropertyInterception<TProperty>, Action<IInterceptorSubject, TProperty>> Create(IEnumerable<IWriteInterceptor> interceptors)
        {
            var interceptorArray = interceptors.ToArray();
            if (interceptorArray.Length == 0)
            {
                return (interception, innerWriteValue) =>
                {
                    innerWriteValue(interception.Property.Subject, (TProperty)interception.NewValue!);
                };
            }

            var chain = new OptimizedInterceptorChain2<WritePropertyInterception<TProperty>, IWriteInterceptor, TProperty>(
                interceptorArray,
                static (interceptor, context, next) => interceptor.WriteProperty(context, next),
                static (interception, innerWriteValue) =>
                {
                    var writeAction = (Action<IInterceptorSubject, TProperty>)innerWriteValue;
                    writeAction(interception.Property.Subject, (TProperty)interception.NewValue!);
                    return (TProperty)interception.NewValue!;
                }
            );
            return chain.Execute;
        }
    }
    
    

    /// <summary>
    /// Allocation-free interceptor chain that pre-allocates all continuation delegates 
    /// and reuses them across executions by updating their captured state.
    /// </summary>
    private sealed class OptimizedInterceptorChain2<TContext, TInterceptor, TProperty>
    {
        private readonly TInterceptor[] _interceptors;
        private readonly Action<TInterceptor, TContext, Action<TContext>> _executeInterceptor;
        private readonly Func<TContext, object, TProperty> _executeTerminal;
        private readonly ContinuationNode2[] _continuations;

        public OptimizedInterceptorChain2(
            TInterceptor[] interceptors,
            Action<TInterceptor, TContext, Action<TContext>> executeInterceptor,
            Func<TContext, object, TProperty> executeTerminal)
        {
            _interceptors = interceptors;
            _executeInterceptor = executeInterceptor;
            _executeTerminal = executeTerminal;
            
            // Pre-allocate all continuation delegates to avoid any allocations during execution
            _continuations = new ContinuationNode2[interceptors.Length];
            for (var i = 0; i < interceptors.Length; i++)
            {
                _continuations[i] = new ContinuationNode2(this, i + 1);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Execute(TContext context, object terminal)
        {
            ExecuteAtIndex(0, context, terminal);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ExecuteAtIndex(int index, TContext context, object terminal)
        {
            if (index >= _interceptors.Length)
            {
                _executeTerminal(context, terminal);
                return;
            }

            var interceptor = _interceptors[index];
            var continuation = _continuations[index];
            
            // Update the continuation's state without any allocations
            continuation.SetState(terminal);
            
            _executeInterceptor(interceptor, context, continuation.ContinuationDelegate);
        }

        /// <summary>
        /// Represents a single node in the interceptor chain with pre-allocated continuation delegate.
        /// The delegate is reused across all executions by updating the captured terminal state.
        /// </summary>
        private sealed class ContinuationNode2
        {
            private readonly OptimizedInterceptorChain2<TContext, TInterceptor, TProperty> _chain;
            private readonly int _nextIndex;
            
            // Pre-allocated delegate that will be reused across all executions
            public readonly Action<TContext> ContinuationDelegate;
            
            // Mutable state that gets updated for each execution
            private object _currentTerminal = null!;

            public ContinuationNode2(OptimizedInterceptorChain2<TContext, TInterceptor, TProperty> chain, int nextIndex)
            {
                _chain = chain;
                _nextIndex = nextIndex;
                // Pre-allocate the delegate once - this is the only allocation
                ContinuationDelegate = ExecuteNext;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void SetState(object terminal)
            {
                _currentTerminal = terminal;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void ExecuteNext(TContext context)
            {
                _chain.ExecuteAtIndex(_nextIndex, context, _currentTerminal);
            }
        }
    }

    /// <summary>
    /// Allocation-free interceptor chain that pre-allocates all continuation delegates 
    /// and reuses them across executions by updating their captured state.
    /// </summary>
    private sealed class OptimizedInterceptorChain<TContext, TInterceptor, TProperty>
    {
        private readonly TInterceptor[] _interceptors;
        private readonly Func<TInterceptor, TContext, Func<TContext, TProperty>, TProperty> _executeInterceptor;
        private readonly Func<TContext, object, TProperty> _executeTerminal;
        private readonly ContinuationNode[] _continuations;

        public OptimizedInterceptorChain(
            TInterceptor[] interceptors,
            Func<TInterceptor, TContext, Func<TContext, TProperty>, TProperty> executeInterceptor,
            Func<TContext, object, TProperty> executeTerminal)
        {
            _interceptors = interceptors;
            _executeInterceptor = executeInterceptor;
            _executeTerminal = executeTerminal;
            
            // Pre-allocate all continuation delegates to avoid any allocations during execution
            _continuations = new ContinuationNode[interceptors.Length];
            for (var i = 0; i < interceptors.Length; i++)
            {
                _continuations[i] = new ContinuationNode(this, i + 1);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TProperty Execute(TContext context, object terminal)
        {
            return ExecuteAtIndex(0, context, terminal);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private TProperty ExecuteAtIndex(int index, TContext context, object terminal)
        {
            if (index >= _interceptors.Length)
            {
                return _executeTerminal(context, terminal);
            }

            var interceptor = _interceptors[index];
            var continuation = _continuations[index];
            
            // Update the continuation's state without any allocations
            continuation.SetState(terminal);
            
            return _executeInterceptor(interceptor, context, continuation.ContinuationDelegate);
        }

        /// <summary>
        /// Represents a single node in the interceptor chain with pre-allocated continuation delegate.
        /// The delegate is reused across all executions by updating the captured terminal state.
        /// </summary>
        private sealed class ContinuationNode
        {
            private readonly OptimizedInterceptorChain<TContext, TInterceptor, TProperty> _chain;
            private readonly int _nextIndex;
            
            // Pre-allocated delegate that will be reused across all executions
            public readonly Func<TContext, TProperty> ContinuationDelegate;
            
            // Mutable state that gets updated for each execution
            private object _currentTerminal = null!;

            public ContinuationNode(OptimizedInterceptorChain<TContext, TInterceptor, TProperty> chain, int nextIndex)
            {
                _chain = chain;
                _nextIndex = nextIndex;
                // Pre-allocate the delegate once - this is the only allocation
                ContinuationDelegate = ExecuteNext;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void SetState(object terminal)
            {
                _currentTerminal = terminal;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private TProperty ExecuteNext(TContext context)
            {
                return _chain.ExecuteAtIndex(_nextIndex, context, _currentTerminal);
            }
        }
    }
}