using System.Collections.Concurrent;

namespace Namotion.Interceptor;

public readonly struct InterceptorCollection : IInterceptorCollection
{
    private readonly IInterceptorSubject _subject;
    private readonly List<IReadInterceptor> _readInterceptors = [];
    private readonly List<IWriteInterceptor> _writeInterceptors = [];

    public InterceptorCollection(IInterceptorSubject subject)
    {
        _subject = subject;
    }

    public IEnumerable<IInterceptor> Interceptors => _readInterceptors
        .OfType<IInterceptor>()
        .Union(_writeInterceptors);

    public void AddInterceptor(IInterceptor interceptor)
    {
        if (interceptor is IReadInterceptor readInterceptor)
        {
            _readInterceptors.Add(readInterceptor);
            _getters.Clear();
        }

        if (interceptor is IWriteInterceptor writeInterceptor)
        {
            _writeInterceptors.Add(writeInterceptor);
            _setters.Clear();
        }

        interceptor.AttachTo(_subject);
    }

    public void RemoveInterceptor(IInterceptor interceptor)
    {
        if (interceptor is IReadInterceptor readInterceptor)
        {
            _readInterceptors.Remove(readInterceptor);
            _getters.Clear();
        }

        if (interceptor is IWriteInterceptor writeInterceptor)
        {
            _writeInterceptors.Remove(writeInterceptor);
            _setters.Clear();
        }

        interceptor.DetachFrom(_subject);
    }

    private static readonly ConcurrentDictionary<Func<object?>, Func<ReadPropertyInterception, object?>> _getters = new();
    private static readonly ConcurrentDictionary<Action<object?>, Func<WritePropertyInterception, object?>> _setters = new();
    
    public object? GetProperty(IInterceptorSubject subject, string propertyName, Func<object?> readValue)
    {
        var readInterceptors = _readInterceptors;
        var interception = new ReadPropertyInterception(new PropertyReference(subject, propertyName));
        return _getters
            .GetOrAdd(readValue, _ =>
            {
                var returnReadValue = new Func<ReadPropertyInterception, object?>(_ => readValue());
    
                foreach (var handler in readInterceptors)
                {
                    var previousReadValue = returnReadValue;
                    returnReadValue = context =>
                        handler.ReadProperty(context, ctx => previousReadValue(ctx));
                }
    
                return returnReadValue;
            })
            .Invoke(interception);
    }
    
    public void SetProperty(IInterceptorSubject subject, string propertyName, object? newValue, Func<object?> readValue, Action<object?> writeValue)
    {
        var writeInterceptors = _writeInterceptors;
        var interception = new WritePropertyInterception(new PropertyReference(subject, propertyName), readValue(), newValue);
        _setters
            .GetOrAdd(writeValue, _ =>
            {
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
    
                return returnWriteValue;
            })
            .Invoke(interception);
    }
}