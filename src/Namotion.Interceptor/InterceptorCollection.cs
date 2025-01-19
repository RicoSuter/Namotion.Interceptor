using System.Collections.Concurrent;

namespace Namotion.Interceptor;

public readonly struct InterceptorCollection : IInterceptorCollection
{
    private readonly IInterceptorSubject _subject;
    
    private readonly List<IInterceptor> _interceptors = [];
    private readonly List<IReadInterceptor> _readInterceptors = [];
    private readonly List<IWriteInterceptor> _writeInterceptors = [];

    public InterceptorCollection(IInterceptorSubject subject)
    {
        _subject = subject;
    }

    public IEnumerable<IInterceptor> Interceptors => _interceptors;

    public void AddInterceptors(params IEnumerable<IInterceptor> interceptors)
    {
        foreach (var interceptor in interceptors)
        {
            if (interceptor is IReadInterceptor readInterceptor)
            {
                _readInterceptors.Add(readInterceptor);
            }

            if (interceptor is IWriteInterceptor writeInterceptor)
            {
                _writeInterceptors.Add(writeInterceptor);
            }
            
            _interceptors.Add(interceptor);
            interceptor.AttachTo(_subject);
        }

        // _getters.Clear();
        // _setters.Clear();
    }

    public void RemoveInterceptors(params IEnumerable<IInterceptor> interceptors)
    {
        foreach (var interceptor in interceptors)
        {
            if (interceptor is IReadInterceptor readInterceptor)
            {
                _readInterceptors.Remove(readInterceptor);
            }

            if (interceptor is IWriteInterceptor writeInterceptor)
            {
                _writeInterceptors.Remove(writeInterceptor);
            }

            _interceptors.Remove(interceptor);
            interceptor.DetachFrom(_subject);
        }

        // _getters.Clear();
        // _setters.Clear();
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
}