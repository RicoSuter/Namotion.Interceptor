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
    private Func<WritePropertyInterception, Action<IInterceptorSubject, TProperty>, TProperty> GetWriteInterceptorFunction<TProperty>()
    {
        if (_writeInterceptorFunction.TryGetValue(typeof(TProperty), out var cached))
        {
            return (Func<WritePropertyInterception, Action<IInterceptorSubject, TProperty>, TProperty>)cached;
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

    public void ExecuteInterceptedWrite<TProperty>(WritePropertyInterception interception, Action<IInterceptorSubject, TProperty> writeValue)
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

            var chain = new AllocationFreeChain<ReadPropertyInterception, IReadInterceptor, TProperty>(
                interceptorArray,
                (interceptor, context, next) => interceptor.ReadProperty(context, next),
                (interception, innerReadValue) => ((Func<IInterceptorSubject, TProperty>)innerReadValue)(interception.Property.Subject)
            );
            return chain.Execute;
        }
    }

    private static class WriteInterceptorChain<TProperty>
    {
        public static Func<WritePropertyInterception, Action<IInterceptorSubject, TProperty>, TProperty> Create(IEnumerable<IWriteInterceptor> interceptors)
        {
            var interceptorArray = interceptors.ToArray();
            if (interceptorArray.Length == 0)
            {
                return (interception, innerWriteValue) =>
                {
                    innerWriteValue(interception.Property.Subject, (TProperty)interception.NewValue!);
                    return (TProperty)interception.NewValue!;
                };
            }

            var chain = new AllocationFreeChain<WritePropertyInterception, IWriteInterceptor, TProperty>(
                interceptorArray,
                (interceptor, context, next) => interceptor.WriteProperty(context, next),
                (interception, innerWriteValue) =>
                {
                    var writeAction = (Action<IInterceptorSubject, TProperty>)innerWriteValue;
                    writeAction(interception.Property.Subject, (TProperty)interception.NewValue!);
                    return (TProperty)interception.NewValue!;
                }
            );
            return chain.Execute;
        }
    }

    private sealed class AllocationFreeChain<TContext, TInterceptor, TProperty>
    {
        private readonly TInterceptor[] _interceptors;
        private readonly ChainNode[] _nodes;
        private readonly Func<TInterceptor, TContext, Func<TContext, TProperty>, TProperty> _executeInterceptor;
        private readonly Func<TContext, object, TProperty> _executeTerminal;

        public AllocationFreeChain(
            TInterceptor[] interceptors,
            Func<TInterceptor, TContext, Func<TContext, TProperty>, TProperty> executeInterceptor,
            Func<TContext, object, TProperty> executeTerminal)
        {
            _interceptors = interceptors;
            _executeInterceptor = executeInterceptor;
            _executeTerminal = executeTerminal;
            _nodes = new ChainNode[interceptors.Length];
            
            for (var i = 0; i < interceptors.Length; i++)
            {
                _nodes[i] = new ChainNode(this, i);
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
            var node = _nodes[index];
            node.SetContext(terminal);
            
            return _executeInterceptor(interceptor, context, node.GetContinuation());
        }

        private sealed class ChainNode
        {
            private readonly AllocationFreeChain<TContext, TInterceptor, TProperty> _chain;
            private readonly int _nextIndex;
            private readonly Func<TContext, TProperty> _continuation;
            
            private object _currentTerminal = null!;

            public ChainNode(AllocationFreeChain<TContext, TInterceptor, TProperty> chain, int currentIndex)
            {
                _chain = chain;
                _nextIndex = currentIndex + 1;
                _continuation = ExecuteNext;
            }

            public void SetContext(object terminal)
            {
                _currentTerminal = terminal;
            }

            public Func<TContext, TProperty> GetContinuation()
            {
                return _continuation;
            }

            private TProperty ExecuteNext(TContext context)
            {
                return _chain.ExecuteAtIndex(_nextIndex, context, _currentTerminal);
            }
        }
    }
}