namespace Namotion.Interceptor;

public readonly struct InterceptorExecutor : IInterceptorExecutor
{
    private readonly IInterceptorSubject _subject;
    
    private readonly List<IReadInterceptor> _readInterceptors = [];
    private readonly List<IWriteInterceptor> _writeInterceptors = [];
    
    private readonly InterceptorCollection _collection = new();

    public InterceptorExecutor(IInterceptorSubject subject)
    {
        _subject = subject;
    }

    // private readonly ConcurrentDictionary<Func<object?>, Func<ReadPropertyInterception, object?>> _getters = new();
    // private readonly ConcurrentDictionary<Action<object?>, Func<WritePropertyInterception, object?>> _setters = new();
    
    public object? GetProperty(IInterceptorSubject subject, string propertyName, Func<object?> readValue)
    {
        var readInterceptors = _readInterceptors;
        var interception = new ReadPropertyInterception(new PropertyReference(subject, propertyName));
        
        var returnReadValue = new Func<ReadPropertyInterception, object?>(_ => readValue());
    
        foreach (var handler in readInterceptors)
        {
            var previousReadValue = returnReadValue;
            returnReadValue = context =>
                handler.ReadProperty(context, ctx => previousReadValue(ctx));
        }

        return returnReadValue(interception);
        
        // return _getters
        //     .GetOrAdd(readValue, _ =>
        //     {
        //         var returnReadValue = new Func<ReadPropertyInterception, object?>(_ => readValue());
        //
        //         foreach (var handler in readInterceptors)
        //         {
        //             var previousReadValue = returnReadValue;
        //             returnReadValue = context =>
        //                 handler.ReadProperty(context, ctx => previousReadValue(ctx));
        //         }
        //
        //         return returnReadValue;
        //     })
        //     .Invoke(interception);
    }
    
    public void SetProperty(IInterceptorSubject subject, string propertyName, object? newValue, Func<object?> readValue, Action<object?> writeValue)
    {
        var writeInterceptors = _writeInterceptors;
        var interception = new WritePropertyInterception(new PropertyReference(subject, propertyName), readValue(), newValue);

        var returnWriteValue = new Func<WritePropertyInterception, object?>(value =>
        {
            writeValue(value.NewValue);
            return value.NewValue;
        });
    
        foreach (var handler in writeInterceptors)
        {
            var previousWriteValue = returnWriteValue;
            returnWriteValue = (context) =>
            {
                return handler.WriteProperty(context,
                    innerContext => previousWriteValue(innerContext));
            };
        }

        returnWriteValue(interception);
        
        // _setters
        //     .GetOrAdd(writeValue, _ =>
        //     {
        //         var returnWriteValue = new Func<WritePropertyInterception, object?>(value =>
        //         {
        //             writeValue(value.NewValue);
        //             return value.NewValue;
        //         });
        //
        //         foreach (var handler in writeInterceptors)
        //         {
        //             var previousWriteValue = returnWriteValue;
        //             returnWriteValue = (context) =>
        //             {
        //                 return handler.WriteProperty(context,
        //                     innerContext => previousWriteValue(innerContext));
        //             };
        //         }
        //
        //         return returnWriteValue;
        //     })
        //     .Invoke(interception);
    }

    public void AddInterceptorCollection(IInterceptorCollection interceptorCollection)
    {
        _collection.AddInterceptorCollection(interceptorCollection);
        
        foreach (var interceptor in interceptorCollection.GetServices<IInterceptor>())
        {
            if (interceptor is IReadInterceptor readInterceptor)
            {
                _readInterceptors.Add(readInterceptor);
            }
    
            if (interceptor is IWriteInterceptor writeInterceptor)
            {
                _writeInterceptors.Add(writeInterceptor);
            }
            
            interceptor.AttachTo(_subject);
        }
    }

    public void RemoveInterceptorCollection(IInterceptorCollection interceptorCollection)
    {
        foreach (var interceptor in interceptorCollection.GetServices<IInterceptor>())
        {
            if (interceptor is IReadInterceptor readInterceptor)
            {
                _readInterceptors.Remove(readInterceptor);
            }
    
            if (interceptor is IWriteInterceptor writeInterceptor)
            {
                _writeInterceptors.Remove(writeInterceptor);
            }
    
            interceptor.DetachFrom(_subject);
        }
        
        _collection.RemoveInterceptorCollection(interceptorCollection);
    }

    public bool TryAddService<TService>(Func<TService> factory, Func<TService, bool> exists)
    {
        return _collection.TryAddService(factory, exists);
    }

    public void AddService<TService>(TService service)
    {
        _collection.AddService(service);
    }

    public TInterface? TryGetService<TInterface>()
    {
        return _collection.TryGetService<TInterface>();
    }

    public IEnumerable<TInterface> GetServices<TInterface>()
    {
        return _collection.GetServices<TInterface>();
    }
}